using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace ServiceDashboard.Tests;

/// <summary>Collects every log event so tests can prove secrets never reach the logs.</summary>
public sealed class CapturingSink : ILogEventSink
{
    private readonly List<LogEvent> _events = [];
    public void Emit(LogEvent logEvent) { lock (_events) _events.Add(logEvent); }
    public string AllText() { lock (_events) return string.Join("\n", _events.Select(e => e.RenderMessage() + " " + string.Join(" ", e.Properties.Select(p => p.Key + "=" + p.Value)) + " " + e.Exception)); }
}

/// <summary>The real application on an in-memory server, with its own temporary SQLite database and the Fake AD provider.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sd-tests-" + Guid.NewGuid().ToString("N"));
    public CapturingSink Logs { get; } = new();
    /// <summary>Extra configuration; set before the first request (the host starts lazily).</summary>
    public Dictionary<string, string?> Extra { get; } = new();
    public string EnvironmentName { get; set; } = "Development";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.UseSetting("App:DataDirectory", Path.Combine(Root, "data"));
        builder.UseSetting("App:AssetDirectory", Path.Combine(Root, "assets"));
        builder.UseSetting("App:BackupDirectory", Path.Combine(Root, "backups"));
        builder.UseSetting("Serilog:LogDirectory", Path.Combine(Root, "logs"));
        builder.UseSetting("Okta:DevelopmentSignIn", "true");
        builder.UseSetting("ActiveDirectory:Provider", "Fake");
        builder.UseSetting("App:SearchRateLimitPerMinute", "100000");
        builder.UseSetting("App:WriteRateLimitPerMinute", "100000");
        foreach (var (k, v) in Extra) builder.UseSetting(k, v);
        builder.ConfigureServices(s => s.AddSingleton<ILogEventSink>(Logs));
    }

    public TestClient NewClient() => new(this);

    /// <summary>Settings for a Production start that passes the startup checks (real-looking values, no Fake provider, no dev sign-in).</summary>
    public void UseValidProductionConfig()
    {
        EnvironmentName = "Production";
        Extra["Okta:DevelopmentSignIn"] = "false";
        Extra["ActiveDirectory:Provider"] = "Ldap";
        Extra["ActiveDirectory:Domain"] = "example.test";
        Extra["ActiveDirectory:Server"] = "dc.example.test";
        Extra["ActiveDirectory:BaseDn"] = "DC=example,DC=test";
        Extra["ActiveDirectory:UseLdaps"] = "true";
        Extra["ActiveDirectory:VerifyCertificate"] = "true";
        Extra["Okta:Issuer"] = "https://okta.example.test";
        Extra["Okta:ClientId"] = "client-id";
        Extra["Okta:ClientSecret"] = "client-secret";
    }

    /// <summary>Runs a query against the app's database in its own scope.</summary>
    public T Db<T>(Func<ServiceDashboard.Data.AppDbContext, T> f)
    {
        using var scope = Services.CreateScope();
        return f(scope.ServiceProvider.GetRequiredService<ServiceDashboard.Data.AppDbContext>());
    }

    public Guid UserId(string devUser) => Db(db => db.Users.Single(u => u.Email == devUser + "@example.invalid").Id);
    public Guid RoleId(string name) => Db(db => db.Roles.Single(r => r.Name == name).Id);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); } catch { /* best effort */ }
    }
}

/// <summary>An HTTP client with a cookie jar that knows how to sign in as a seeded development user and send anti-forgery tokens.</summary>
public sealed class TestClient
{
    public HttpClient Http { get; }
    private string _csrf = "";

    public TestClient(TestApp app, Uri? baseAddress = null) =>
        Http = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false, BaseAddress = baseAddress ?? new Uri("http://localhost") });

    public async Task<TestClient> SignInAsync(string devUser)
    {
        var cfg = await Http.GetFromJsonAsync<JsonElement>("/api/auth/config");
        _csrf = cfg.GetProperty("csrfToken").GetString()!;
        var users = await Http.GetFromJsonAsync<JsonElement>("/api/auth/dev-users");
        var id = users.EnumerateArray().First(u => u.GetProperty("email").GetString() == devUser + "@example.invalid").GetProperty("id").GetGuid();
        var res = await Send(HttpMethod.Post, "/api/auth/dev-login", new { userId = id });
        if (res.StatusCode == HttpStatusCode.OK) await RefreshCsrfAsync();
        LastSignIn = res;
        return this;
    }

    public HttpResponseMessage? LastSignIn { get; private set; }

    /// <summary>Gets an anti-forgery token without signing in (as the login page does).</summary>
    public async Task PrimeCsrfAsync() =>
        _csrf = (await Http.GetFromJsonAsync<JsonElement>("/api/auth/config")).GetProperty("csrfToken").GetString()!;

    public async Task RefreshCsrfAsync()
    {
        var me = await Http.GetAsync("/api/auth/me");
        if (me.IsSuccessStatusCode) _csrf = (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("csrfToken").GetString()!;
    }

    public Task<HttpResponseMessage> Get(string url) => Http.GetAsync(url);

    public Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body != null) req.Content = JsonContent.Create(body);
        if (method != HttpMethod.Get) req.Headers.Add("X-XSRF-TOKEN", _csrf);
        return Http.SendAsync(req);
    }

    public Task<HttpResponseMessage> Post(string url, object? body = null) => Send(HttpMethod.Post, url, body ?? new { });
    public Task<HttpResponseMessage> Put(string url, object? body = null) => Send(HttpMethod.Put, url, body ?? new { });
    public Task<HttpResponseMessage> Delete(string url) => Send(HttpMethod.Delete, url);

    public Task<HttpResponseMessage> Upload(string url, byte[] bytes, string fileName, string contentType = "image/png")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        req.Headers.Add("X-XSRF-TOKEN", _csrf);
        return Http.SendAsync(req);
    }

    public async Task<JsonElement> Json(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();
}
