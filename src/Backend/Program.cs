using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using ServiceDashboard.Configuration;
using ServiceDashboard.Data;
using ServiceDashboard.Middleware;
using ServiceDashboard.Modules.ActiveDirectory;
using ServiceDashboard.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- Startup checks: refuse to start with unsafe or incomplete configuration ----
var startupErrors = StartupValidator.Validate(builder.Configuration, builder.Environment.IsDevelopment(), builder.Environment.IsProduction());
if (startupErrors.Count > 0)
    throw new InvalidOperationException("The application cannot start:\n - " + string.Join("\n - ", startupErrors));

// ---- Options (unfilled <PLACEHOLDER> values are treated as "not set") ----
static void Clean(object o)
{
    foreach (var p in o.GetType().GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite))
        if (StartupValidator.IsPlaceholder((string?)p.GetValue(o))) p.SetValue(o, "");
}
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.Section));
builder.Services.Configure<OktaOptions>(builder.Configuration.GetSection(OktaOptions.Section));
builder.Services.Configure<ActiveDirectoryOptions>(builder.Configuration.GetSection(ActiveDirectoryOptions.Section));
builder.Services.PostConfigure<AppOptions>(Clean);
builder.Services.PostConfigure<OktaOptions>(Clean);
builder.Services.PostConfigure<ActiveDirectoryOptions>(Clean);

var appOptions = builder.Configuration.GetSection(AppOptions.Section).Get<AppOptions>() ?? new();
Clean(appOptions);
var paths = new AppPaths(appOptions, builder.Environment.ContentRootPath);
paths.EnsureCreated();
builder.Services.AddSingleton(paths);

// ---- Logging (Serilog, rolling files, redaction) ----
var logDir = builder.Configuration["Serilog:LogDirectory"];
if (string.IsNullOrWhiteSpace(logDir) || StartupValidator.IsPlaceholder(logDir))
    logDir = Path.Combine(builder.Environment.ContentRootPath, "logs");
builder.Host.UseSerilog((ctx, services, cfg) =>
{
    cfg.MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.With<SensitiveDataEnricher>()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(Path.Combine(Path.GetFullPath(logDir!, ctx.HostingEnvironment.ContentRootPath), "app-.log"),
            rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}");
    foreach (var sink in services.GetServices<ILogEventSink>()) cfg.WriteTo.Sink(sink); // tests attach a capturing sink
});

// ---- Database ----
builder.Services.AddDbContextFactory<AppDbContext>(o => o
    .UseSqlite($"Data Source={paths.DatabaseFile};Default Timeout=30")
    .AddInterceptors(new SqlitePragmaInterceptor()));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();

// ---- Core services ----
builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<AccessService>();
builder.Services.AddScoped<AccessManagementService>();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<UserProvisioning>();
builder.Services.AddScoped<ModuleCatalog>();
builder.Services.AddSingleton<IAuditService, AuditService>();
builder.Services.AddScoped<MaintenanceTasks>();
builder.Services.AddScoped<ObjectActivity>();
builder.Services.AddHostedService<MaintenanceService>();

// ---- Authentication and authorization ----
builder.AddAppAuthentication();
builder.Services.AddAuthorization(o => o.AddPermissionPolicies());
builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-XSRF-TOKEN";
    o.Cookie.Name = "sd.antiforgery";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});

// ---- Web API ----
builder.Services.AddControllers(o => o.Filters.Add<AntiforgeryFilter>())
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    ctx.ProblemDetails.Extensions["correlationId"] = ctx.HttpContext.TraceIdentifier;
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
if (builder.Environment.IsDevelopment()) builder.Services.AddOpenApi();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    string Key(HttpContext c) => c.User.Identity?.IsAuthenticated == true
        ? "u:" + c.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        : "ip:" + c.Connection.RemoteIpAddress;
    o.AddPolicy(RateLimitPolicies.Search, c => RateLimitPartition.GetFixedWindowLimiter(Key(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = appOptions.SearchRateLimitPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(RateLimitPolicies.Write, c => RateLimitPartition.GetFixedWindowLimiter(Key(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = appOptions.WriteRateLimitPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = 429;
        await ctx.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = 429, Title = "Too many requests", Detail = "Slow down and try again in a minute.",
            Extensions = { ["correlationId"] = ctx.HttpContext.TraceIdentifier },
        }, options: null, contentType: "application/problem+json", cancellationToken: ct);
    };
});

// ---- Modules (one registration call each) ----
builder.Services.AddActiveDirectoryModule(builder.Configuration);

var app = builder.Build();

// ---- Database migration and seed data ----
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();

// ---- HTTP pipeline ----
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
var okta = app.Services.GetRequiredService<IOptions<OktaOptions>>().Value;
var formAction = string.IsNullOrEmpty(okta.Issuer) ? "" : " " + new Uri(okta.Issuer).GetLeftPart(UriPartial.Authority);
app.UseMiddleware<SecurityHeadersMiddleware>(
    "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; " +
    $"frame-ancestors 'none'; base-uri 'self'; object-src 'none'; form-action 'self'{formAction}");
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseSerilogRequestLogging(o =>
{
    o.Logger = app.Services.GetRequiredService<Serilog.ILogger>(); // this host's logger, not the process-wide static one
    o.GetLevel = (ctx, _, ex) =>
        ex != null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
        : ctx.Request.Path.StartsWithSegments("/api") ? Serilog.Events.LogEventLevel.Information : Serilog.Events.LogEventLevel.Debug;
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapControllers();
app.MapFallbackToFile("index.html"); // the React app handles every non-API route

app.Run();

public static class RateLimitPolicies
{
    public const string Search = "search";
    public const string Write = "write";
}

public partial class Program;
