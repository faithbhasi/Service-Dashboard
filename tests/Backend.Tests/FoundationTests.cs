using System.Net;
using System.Net.Http.Json;
using ServiceDashboard.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Microsoft.Extensions.Configuration;

namespace ServiceDashboard.Tests;

public class StartupValidationTests
{
    private static List<string> Validate(Dictionary<string, string?> values, bool dev, bool prod) =>
        StartupValidator.Validate(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), dev, prod);

    [Fact]
    public void Development_sign_in_outside_Development_is_refused()
    {
        var errors = Validate(new() { ["Okta:DevelopmentSignIn"] = "true", ["ActiveDirectory:Provider"] = "Fake" }, dev: false, prod: true);
        Assert.Contains(errors, e => e.Contains("DevelopmentSignIn"));
    }

    [Fact]
    public void Development_sign_in_in_Development_is_allowed()
    {
        Assert.Empty(Validate(new() { ["Okta:DevelopmentSignIn"] = "true", ["ActiveDirectory:Provider"] = "Fake" }, dev: true, prod: false));
    }

    [Fact]
    public void Production_refuses_disabled_certificate_checks_and_plain_ldap()
    {
        var errors = Validate(new()
        {
            ["ActiveDirectory:Provider"] = "Ldap", ["ActiveDirectory:VerifyCertificate"] = "false", ["ActiveDirectory:UseLdaps"] = "false",
            ["ActiveDirectory:Domain"] = "example.test", ["ActiveDirectory:Server"] = "dc.example.test", ["ActiveDirectory:BaseDn"] = "DC=example,DC=test",
            ["Okta:Issuer"] = "https://x", ["Okta:ClientId"] = "x", ["Okta:ClientSecret"] = "x",
            ["App:DataDirectory"] = "d", ["App:AssetDirectory"] = "a", ["Serilog:LogDirectory"] = "l",
        }, dev: false, prod: true);
        Assert.Contains(errors, e => e.Contains("VerifyCertificate"));
        Assert.Contains(errors, e => e.Contains("UseLdaps"));
    }

    [Fact]
    public void Local_test_bind_credentials_are_refused_outside_Development()
    {
        var values = new Dictionary<string, string?> { ["ActiveDirectory:Provider"] = "Fake", ["ActiveDirectory:BindUsername"] = "svc@example.test" };
        Assert.Contains(Validate(values, dev: false, prod: false), e => e.Contains("BindUsername"));
        Assert.Contains(Validate(values, dev: false, prod: true), e => e.Contains("BindUsername"));
        Assert.DoesNotContain(Validate(values, dev: true, prod: false), e => e.Contains("BindUsername"));
    }

    [Fact]
    public void Production_refuses_unfilled_placeholders_and_the_fake_provider()
    {
        var errors = Validate(new()
        {
            ["ActiveDirectory:Provider"] = "Fake", ["Okta:Issuer"] = "<OKTA_ISSUER_URL>", ["Okta:ClientId"] = "<OKTA_CLIENT_ID>",
            ["Okta:ClientSecret"] = "<SET_VIA_USER_SECRETS_OR_ENVIRONMENT>", ["App:DataDirectory"] = "<DATA_DIRECTORY>",
        }, dev: false, prod: true);
        Assert.Contains(errors, e => e.Contains("Fake"));
        Assert.Contains(errors, e => e.Contains("Okta:Issuer"));
        Assert.Contains(errors, e => e.Contains("App:DataDirectory"));
    }
}

public class LogRedactionTests
{
    private sealed class Sink : ILogEventSink
    {
        public readonly List<string> Lines = [];
        public void Emit(LogEvent e) => Lines.Add(e.RenderMessage() + " | " + string.Join(", ", e.Properties.Select(p => $"{p.Key}={p.Value}")));
    }

    private static (ILogger Log, Sink Sink) Create()
    {
        var sink = new Sink();
        return (new LoggerConfiguration().Enrich.With<SensitiveDataEnricher>().WriteTo.Sink(sink).CreateLogger(), sink);
    }

    private sealed record Body(string UserName, string NewPassword);

    [Fact]
    public void Password_properties_are_redacted_even_when_nested()
    {
        var (log, sink) = Create();
        log.Information("Reset requested {@Request} with {Password}", new Body("alice", "Sup3r-Secret!"), "Sup3r-Secret!");
        log.Information("Token is {AccessToken}", "abc.def.ghi");
        var all = string.Join("\n", sink.Lines);
        Assert.DoesNotContain("Sup3r-Secret!", all);
        Assert.DoesNotContain("abc.def.ghi", all);
        Assert.Contains("alice", all);
    }

    [Fact]
    public void Password_text_inside_a_string_value_is_redacted()
    {
        var (log, sink) = Create();
        log.Information("Payload {Body}", "{\"user\":\"a\"} password=Hunter2!! trailing");
        Assert.DoesNotContain("Hunter2!!", string.Join("\n", sink.Lines));
    }
}

public class SignInTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    public SignInTests(TestApp app) => _app = app;

    [Fact]
    public async Task Anonymous_requests_to_protected_endpoints_get_401_problem_details_with_a_correlation_id()
    {
        var c = _app.NewClient();
        var res = await c.Get("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.NotEmpty(res.Headers.GetValues("X-Correlation-ID"));
        Assert.Contains("problem+json", res.Content.Headers.ContentType!.ToString());
        Assert.False((await c.Json(res)).GetProperty("correlationId").GetString() is null or "");
    }

    [Fact]
    public async Task Admin_can_sign_in_and_sees_every_permission()
    {
        var c = await _app.NewClient().SignInAsync("dev.admin");
        var me = await c.Json(await c.Get("/api/auth/me"));
        Assert.True(me.GetProperty("hasAccess").GetBoolean());
        Assert.Equal(ServiceDashboard.Models.Permissions.All.Count, me.GetProperty("permissions").GetArrayLength());
    }

    [Fact]
    public async Task User_without_a_role_signs_in_but_has_no_access()
    {
        var c = await _app.NewClient().SignInAsync("dev.noaccess");
        var me = await c.Json(await c.Get("/api/auth/me"));
        Assert.False(me.GetProperty("hasAccess").GetBoolean());
        Assert.Equal(0, me.GetProperty("permissions").GetArrayLength());
    }

    [Fact]
    public async Task User_disabled_in_the_app_is_denied_and_the_attempt_is_audited()
    {
        var c = await _app.NewClient().SignInAsync("dev.disabled");
        Assert.Equal(HttpStatusCode.Forbidden, c.LastSignIn!.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Get("/api/auth/me")).StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDashboard.Data.AppDbContext>();
        Assert.Contains(db.AuditLogs.ToList(), a => a.Action == "logon.denied" && a.Result == ServiceDashboard.Models.AuditResult.Denied);
    }

    [Fact]
    public async Task State_changing_requests_without_an_antiforgery_token_are_rejected()
    {
        var c = await _app.NewClient().SignInAsync("dev.admin");
        var res = await c.Http.PutAsJsonAsync("/api/auth/preferences", new { theme = "dark", navCollapsed = false });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Sign_in_and_page_access_are_audited()
    {
        var c = await _app.NewClient().SignInAsync("dev.helpdesk");
        Assert.True((await c.Json(await c.Post("/api/auth/access", new { page = "ad.users" }))).GetProperty("allowed").GetBoolean());
        Assert.False((await c.Json(await c.Post("/api/auth/access", new { page = "settings" }))).GetProperty("allowed").GetBoolean());

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDashboard.Data.AppDbContext>();
        var rows = db.AuditLogs.ToList();
        Assert.Contains(rows, a => a.Action == "logon.signin");
        Assert.Contains(rows, a => a.Action == "page.view" && a.Target == "settings" && a.Result == ServiceDashboard.Models.AuditResult.Denied);
    }

    [Fact]
    public async Task Audit_rows_cannot_be_updated()
    {
        await _app.NewClient().SignInAsync("dev.admin");
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDashboard.Data.AppDbContext>();
        var row = db.AuditLogs.First();
        row.Error = "tampered";
        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
    }
}
