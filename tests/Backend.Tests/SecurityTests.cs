using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ServiceDashboard.Configuration;
using ServiceDashboard.Models;
using ServiceDashboard.Services;

namespace ServiceDashboard.Tests;

public class EndpointSecurityTests
{
    // The only routes that may be reached without a permission. Everything else must name a policy.
    private static readonly HashSet<string> Anonymous =
    [
        "api/auth/config", "api/auth/login", "api/auth/dev-users", "api/auth/dev-login", "api/auth/logout",
        "api/settings/branding", "api/settings/personalization/logo/{kind}", "api/health",
    ];
    // Signed-in users only: they return the caller's own data or apply per-module permissions inside.
    private static readonly HashSet<string> SessionOnly =
        ["api/auth/me", "api/auth/preferences", "api/auth/access", "api/settings/shell", "api/search"];

    private sealed record Ep(string Route, string Method, string[] Policies, bool AllowAnonymous);

    private static List<Ep> Endpoints(TestApp app) =>
        app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("api/"))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => new Ep(
                e.RoutePattern.RawText!, m,
                e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy ?? "").ToArray(),
                e.Metadata.GetMetadata<IAllowAnonymous>() != null)))
            .ToList();

    private static string Fill(string route) => "/" + Regex.Replace(route, @"\{(\w+)(?::(\w+))?\??\}", m =>
        m.Groups[2].Value switch { "guid" => Guid.NewGuid().ToString(), "long" => "1", _ => "x" });

    [Fact]
    public async Task Every_API_endpoint_names_a_permission_policy_or_is_explicitly_whitelisted()
    {
        using var app = new TestApp();
        var known = Permissions.AllIds.Concat(PermissionPolicies.Combined.Keys).ToHashSet();
        var eps = Endpoints(app);
        Assert.True(eps.Count > 60, $"only found {eps.Count} endpoints");

        foreach (var e in eps)
        {
            if (e.AllowAnonymous)
            {
                Assert.True(Anonymous.Contains(e.Route), $"{e.Method} {e.Route} allows anonymous access but is not on the whitelist");
                continue;
            }
            Assert.True(e.Policies.Length > 0, $"{e.Method} {e.Route} has no authorization at all");
            if (e.Policies.Any(p => p == "")) Assert.True(SessionOnly.Contains(e.Route), $"{e.Method} {e.Route} only requires a session; add a permission policy or whitelist it");
            foreach (var p in e.Policies.Where(p => p != "")) Assert.True(known.Contains(p), $"{e.Method} {e.Route} uses unknown policy '{p}'");
        }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Every_protected_endpoint_rejects_anonymous_callers_and_users_without_the_permission()
    {
        using var app = new TestApp();
        var eps = Endpoints(app).Where(e => !e.AllowAnonymous).ToList();
        var anon = app.NewClient();
        var nobody = await app.NewClient().SignInAsync("dev.noaccess"); // signed in, no role, no permissions

        var checkedWithPermission = 0;
        foreach (var e in eps)
        {
            var url = Fill(e.Route);
            var method = new HttpMethod(e.Method);
            // The logo upload only routes for multipart bodies (model binding of IFormFile), so send one.
            var isUpload = method == HttpMethod.Post && e.Route.Contains("/logo/");
            var a = isUpload ? await anon.Upload(url, [1], "x.png") : await anon.Send(method, url, method == HttpMethod.Get ? null : new { });
            Assert.True(a.StatusCode == HttpStatusCode.Unauthorized, $"anonymous {e.Method} {e.Route} returned {(int)a.StatusCode}, expected 401");

            if (e.Policies.All(p => p == "")) continue; // session-only endpoints have no permission to lack
            var n = isUpload ? await nobody.Upload(url, [1], "x.png") : await nobody.Send(method, url, method == HttpMethod.Get ? null : new { });
            Assert.True(n.StatusCode == HttpStatusCode.Forbidden, $"{e.Method} {e.Route} returned {(int)n.StatusCode} for a user with no permissions, expected 403");
            checkedWithPermission++;
        }
        Assert.True(checkedWithPermission > 55, $"only {checkedWithPermission} permissioned endpoints were exercised");
    }
}

public class ArchitectureTests
{
    private static readonly Assembly Backend = typeof(Program).Assembly;
    private const string Providers = "ServiceDashboard.Modules.ActiveDirectory.Providers";

    private static bool IsForbidden(Type t) =>
        t.Namespace is { } ns && (ns.StartsWith("System.DirectoryServices") || ns.StartsWith("System.Management.Automation")
            || ns.StartsWith(Providers + ".Ldap") || ns.StartsWith(Providers + ".PowerShell") || ns.StartsWith(Providers + ".Fake"));

    private static IEnumerable<Type> Flatten(Type t)
    {
        yield return t;
        if (t.HasElementType) foreach (var x in Flatten(t.GetElementType()!)) yield return x;
        if (t.IsGenericType) foreach (var g in t.GetGenericArguments()) foreach (var x in Flatten(g)) yield return x;
    }

    private static IEnumerable<Type> Signatures(Type t)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var f in t.GetFields(all)) yield return f.FieldType;
        foreach (var p in t.GetProperties(all)) yield return p.PropertyType;
        foreach (var c in t.GetConstructors(all)) foreach (var p in c.GetParameters()) yield return p.ParameterType;
        foreach (var m in t.GetMethods(all)) { yield return m.ReturnType; foreach (var p in m.GetParameters()) yield return p.ParameterType; }
        if (t.BaseType != null) yield return t.BaseType;
        foreach (var i in t.GetInterfaces()) yield return i;
    }

    [Fact]
    public void Nothing_outside_the_provider_folders_references_LDAP_PowerShell_or_the_Fake_provider()
    {
        var offenders = new List<string>();
        foreach (var type in Backend.GetTypes())
        {
            var ns = type.Namespace ?? "";
            if (ns.StartsWith(Providers + ".Ldap") || ns.StartsWith(Providers + ".PowerShell") || ns.StartsWith(Providers + ".Fake")) continue;
            if (type.Name.StartsWith("ActiveDirectoryModule")) continue; // the single registration method picks the implementation
            if (type.Name.Contains('<') && type.DeclaringType != null && (type.DeclaringType.Namespace ?? "").StartsWith(Providers + ".")) continue;

            foreach (var used in Signatures(type).SelectMany(Flatten).Where(IsForbidden).Distinct())
                offenders.Add($"{type.FullName} uses {used.FullName}");
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    [Fact]
    public void Controllers_and_services_take_IDirectoryProvider_and_the_module_registers_exactly_one_implementation_per_setting()
    {
        var controllers = Backend.GetTypes().Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.True(controllers.Count >= 14);
        foreach (var c in controllers)
            Assert.DoesNotContain(Signatures(c).SelectMany(Flatten), IsForbidden);

        var implementations = Backend.GetTypes().Where(t => typeof(ServiceDashboard.Modules.ActiveDirectory.Providers.IDirectoryProvider).IsAssignableFrom(t) && t.IsClass).ToList();
        Assert.All(implementations, t => Assert.StartsWith(Providers + ".", t.Namespace));
        Assert.Contains(implementations, t => t.Name == "LdapDirectoryProvider");
        Assert.Contains(implementations, t => t.Name == "FakeDirectoryProvider");
    }

    [Fact]
    public void Source_files_outside_the_provider_folders_never_mention_LDAP_or_PowerShell_APIs()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "Backend", "Backend.csproj"))) root = root.Parent;
        Assert.NotNull(root);
        var forbidden = new[] { "System.DirectoryServices", "System.Management.Automation", "LdapConnection", "DirectoryEntry", "SearchRequest(", "PowerShell.Create", "Providers.Ldap", "Providers.Fake", "Providers.PowerShell" };
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", "Backend"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = file.Replace('\\', '/');
            if (rel.Contains("/Providers/Ldap/") || rel.Contains("/Providers/Fake/") || rel.Contains("/Providers/PowerShell/") || rel.Contains("/Data/Migrations/") || rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            if (rel.EndsWith("ActiveDirectoryModule.cs")) continue;
            var text = File.ReadAllText(file);
            foreach (var f in forbidden.Where(text.Contains)) offenders.Add($"{Path.GetFileName(file)} mentions {f}");
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }
}

public class HardeningTests
{
    [Fact]
    public async Task Responses_carry_the_security_headers_and_a_correlation_id()
    {
        using var app = new TestApp();
        var res = await app.NewClient().Get("/api/health");
        var h = res.Headers.ToDictionary(x => x.Key, x => string.Join(",", x.Value), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("default-src 'self'", h["Content-Security-Policy"]);
        Assert.Contains("frame-ancestors 'none'", h["Content-Security-Policy"]);
        Assert.Contains("script-src 'self'", h["Content-Security-Policy"]);
        Assert.DoesNotContain("unsafe-inline", h["Content-Security-Policy"]);
        Assert.Equal("nosniff", h["X-Content-Type-Options"]);
        Assert.Equal("DENY", h["X-Frame-Options"]);
        Assert.False(string.IsNullOrEmpty(h["X-Correlation-ID"]));
        Assert.Equal("no-store", res.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task An_incoming_correlation_id_is_kept_only_when_well_formed()
    {
        using var app = new TestApp();
        var http = app.NewClient().Http;
        var good = new HttpRequestMessage(HttpMethod.Get, "/api/health"); good.Headers.Add("X-Correlation-ID", "abc-12345678");
        Assert.Equal("abc-12345678", (await http.SendAsync(good)).Headers.GetValues("X-Correlation-ID").Single());
        var bad = new HttpRequestMessage(HttpMethod.Get, "/api/health"); bad.Headers.Add("X-Correlation-ID", "bad id; injected");
        Assert.DoesNotContain("injected", (await http.SendAsync(bad)).Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task Errors_never_show_stack_traces_or_internal_details()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        var res = await c.Http.PostAsync("/api/admin/roles", new StringContent("{ this is not json", System.Text.Encoding.UTF8, "application/json"));
        var body = await res.Content.ReadAsStringAsync();
        Assert.True((int)res.StatusCode >= 400);
        Assert.DoesNotContain("   at ", body);
        Assert.DoesNotContain("System.", body);
    }

    [Fact]
    public async Task Search_and_write_endpoints_are_rate_limited_per_user()
    {
        using var app = new TestApp();
        app.Extra["App:SearchRateLimitPerMinute"] = "3";
        app.Extra["App:WriteRateLimitPerMinute"] = "2";
        var c = await app.NewClient().SignInAsync("dev.admin");

        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await c.Get("/api/search?q=alice")).StatusCode);
        var limited = await c.Get("/api/search?q=alice");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("correlationId", await limited.Content.ReadAsStringAsync());

        var dave = ServiceDashboard.Modules.ActiveDirectory.Providers.Fake.FakeDirectoryData.Id("user:dave.locked");
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++) codes.Add((await c.Post($"/api/modules/ad/users/{dave}/unlock", new { justification = "Verified caller identity" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[2]);

        var other = await app.NewClient().SignInAsync("dev.auditor"); // someone else is not affected
        Assert.Equal(HttpStatusCode.OK, (await other.Get("/api/search?q=alice")).StatusCode);
    }

    [Fact]
    public async Task OpenApi_is_available_in_Development_only()
    {
        using var dev = new TestApp();
        Assert.Equal(HttpStatusCode.OK, (await dev.NewClient().Get("/openapi/v1.json")).StatusCode);

        using var prod = new TestApp();
        prod.UseValidProductionConfig();
        Assert.NotEqual(HttpStatusCode.OK, (await prod.NewClient().Get("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Production_starts_with_valid_settings_sends_HSTS_and_hides_development_sign_in()
    {
        using var app = new TestApp();
        app.UseValidProductionConfig();
        var c = new TestClient(app, new Uri("https://app.example.test"));
        var res = await c.Get("/api/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("max-age", res.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await c.Get("/api/auth/dev-users")).StatusCode);
        await c.PrimeCsrfAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await c.Post("/api/auth/dev-login", new { userId = Guid.NewGuid() })).StatusCode);
        Assert.False((await c.Json(await c.Get("/api/auth/config"))).GetProperty("devSignIn").GetBoolean());
    }

    [Theory]
    [InlineData("Okta:DevelopmentSignIn", "true")]
    [InlineData("ActiveDirectory:VerifyCertificate", "false")]
    [InlineData("ActiveDirectory:Provider", "Fake")]
    [InlineData("ActiveDirectory:UseLdaps", "false")]
    [InlineData("Okta:ClientSecret", "<SET_VIA_USER_SECRETS_OR_ENVIRONMENT>")]
    public void Production_refuses_to_start_with_unsafe_or_unfinished_settings(string key, string value)
    {
        using var app = new TestApp();
        app.UseValidProductionConfig();
        app.Extra[key] = value;
        var ex = Assert.ThrowsAny<Exception>(() => app.NewClient());
        Assert.Contains("cannot start", ex.ToString());
    }

    [Fact]
    public void Development_sign_in_is_refused_even_in_a_Test_environment()
    {
        using var app = new TestApp { EnvironmentName = "Staging" };
        var ex = Assert.ThrowsAny<Exception>(() => app.NewClient());
        Assert.Contains("DevelopmentSignIn", ex.ToString());
    }
}

public class SessionTimeoutTests
{
    private static async Task<(TestApp App, CookieValidatePrincipalContext Ctx)> Build(int signedInMinutesAgo, int activeMinutesAgo, string devUser = "dev.admin")
    {
        var app = new TestApp();
        await app.NewClient().SignInAsync(devUser); // creates the seeded users
        var scope = app.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        long Ts(int minutesAgo) => DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToUnixTimeSeconds();
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, app.UserId(devUser).ToString()),
            new Claim(UserProvisioning.SignInClaim, Ts(signedInMinutesAgo).ToString()),
            new Claim(UserProvisioning.ActiveClaim, Ts(activeMinutesAgo).ToString()),
        ], "Cookies");
        var scheme = new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler));
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties(), "Cookies");
        return (app, new CookieValidatePrincipalContext(http, scheme, new CookieAuthenticationOptions(), ticket));
    }

    [Fact]
    public async Task A_session_inside_both_limits_is_accepted_and_its_activity_time_is_refreshed()
    {
        var (app, ctx) = await Build(signedInMinutesAgo: 20, activeMinutesAgo: 5);
        using (app)
        {
            await SessionValidator.ValidateAsync(ctx);
            Assert.NotNull(ctx.Principal);
            Assert.True(ctx.ShouldRenew);
            var active = long.Parse(ctx.Principal!.FindFirstValue(UserProvisioning.ActiveClaim)!);
            Assert.True(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - active < 5);
        }
    }

    [Fact]
    public async Task An_idle_session_is_rejected_after_the_idle_timeout()
    {
        var (app, ctx) = await Build(signedInMinutesAgo: 90, activeMinutesAgo: 45); // default idle timeout is 30 minutes
        using (app) { await SessionValidator.ValidateAsync(ctx); Assert.Null(ctx.Principal); }
    }

    [Fact]
    public async Task A_session_older_than_the_absolute_lifetime_is_rejected_even_if_active()
    {
        var (app, ctx) = await Build(signedInMinutesAgo: 600, activeMinutesAgo: 1); // default absolute lifetime is 480 minutes
        using (app) { await SessionValidator.ValidateAsync(ctx); Assert.Null(ctx.Principal); }
    }

    [Fact]
    public async Task The_timeouts_come_from_the_saved_settings()
    {
        var (app, ctx) = await Build(signedInMinutesAgo: 20, activeMinutesAgo: 20);
        using (app)
        {
            using (var scope = app.Services.CreateScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
                var general = await settings.GetGeneralAsync();
                general.IdleTimeoutMinutes = 10;
                await settings.SaveAsync(SettingKeys.General, general, "test", settings.DefaultGeneral);
            }
            await SessionValidator.ValidateAsync(ctx);
            Assert.Null(ctx.Principal);
        }
    }

    [Fact]
    public async Task A_user_disabled_in_the_app_loses_the_session_immediately()
    {
        var app = new TestApp();
        using (app)
        {
            var admin = await app.NewClient().SignInAsync("dev.admin");
            var victim = await app.NewClient().SignInAsync("dev.user");
            Assert.Equal(HttpStatusCode.OK, (await victim.Get("/api/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.Put($"/api/admin/users/{app.UserId("dev.user")}/status", new { isEnabled = false })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await victim.Get("/api/auth/me")).StatusCode);
        }
    }
}
