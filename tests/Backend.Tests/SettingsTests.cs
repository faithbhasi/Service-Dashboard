using System.Net;
using ServiceDashboard.Controllers;
using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers.Fake;

namespace ServiceDashboard.Tests;

public class SettingsTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static object General(Action<Dictionary<string, object?>>? tweak = null, Action<Dictionary<string, object?>>? banner = null)
    {
        var b = new Dictionary<string, object?> { ["enabled"] = false, ["type"] = "Information", ["text"] = "", ["startLocal"] = null, ["endLocal"] = null };
        banner?.Invoke(b);
        var g = new Dictionary<string, object?>
        {
            ["productName"] = "Test Product", ["environmentLabel"] = "Test", ["timeZone"] = "UTC", ["dateFormat"] = "yyyy-MM-dd", ["supportContact"] = "help@example.invalid",
            ["idleTimeoutMinutes"] = 30, ["absoluteTimeoutMinutes"] = 480, ["banner"] = b,
        };
        tweak?.Invoke(g);
        return g;
    }

    // ---------------------------------------------------------------- permissions

    [Fact]
    public async Task Settings_endpoints_need_the_right_permission()
    {
        using var app = new TestApp();
        var auditor = await app.NewClient().SignInAsync("dev.auditor"); // no settings permissions at all
        foreach (var url in new[] { "/api/settings/general", "/api/settings/modules", "/api/settings/action-policies", "/api/settings/personalization", "/api/modules/ad/settings" })
            Assert.Equal(HttpStatusCode.Forbidden, (await auditor.Get(url)).StatusCode);

        var helpdesk = await app.NewClient().SignInAsync("dev.helpdesk");
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.Put("/api/settings/general", General())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.Put("/api/settings/modules/ad", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.Put("/api/settings/action-policies", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.Put("/api/settings/personalization", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.Post("/api/settings/personalization/reset-colors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.Upload("/api/settings/personalization/logo/light", Png, "x.png")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.Delete("/api/settings/personalization/logo/light")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.Put("/api/modules/ad/settings", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.Post("/api/modules/ad/settings/test-connection")).StatusCode);
    }

    [Fact]
    public async Task Managing_settings_and_managing_personalization_are_separate_permissions()
    {
        using var app = new TestApp();
        var admin = await app.NewClient().SignInAsync("dev.admin");
        var role = await admin.Json(await admin.Post("/api/admin/roles", new { name = "Branding only", permissions = new[] { Permissions.SettingsRead, Permissions.SettingsPersonalizationManage } }));
        await admin.Put($"/api/admin/users/{app.UserId("dev.user")}/roles", new { roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var c = await app.NewClient().SignInAsync("dev.user");
        Assert.Equal(HttpStatusCode.OK, (await c.Get("/api/settings/personalization")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Put("/api/settings/general", General())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.Post("/api/settings/personalization/reset-colors")).StatusCode);
    }

    // ---------------------------------------------------------------- general

    [Fact]
    public async Task General_settings_are_validated_saved_and_audited_with_before_and_after()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/general", General(g => g["timeZone"] = "Mars/Olympus"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/general", General(g => g["dateFormat"] = "dd-mm-yy"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/general", General(g => g["productName"] = " "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/general", General(g => { g["idleTimeoutMinutes"] = 120; g["absoluteTimeoutMinutes"] = 60; }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/general", General(g => g["idleTimeoutMinutes"] = 1))).StatusCode);

        var ok = await c.Put("/api/settings/general", General(g => { g["timeZone"] = "Europe/London"; g["productName"] = "Renamed"; }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var row = app.Db(db => db.AuditLogs.Single(a => a.Action == "settings.general.update"));
        Assert.Contains("\"timeZone\":\"UTC\"", row.PreviousValue);
        Assert.Contains("\"timeZone\":\"Europe/London\"", row.NewValue);
        Assert.Equal("Renamed", (await c.Json(await c.Get("/api/settings/shell"))).GetProperty("productName").GetString());
        Assert.Equal("Renamed", (await c.Json(await c.Get("/api/settings/branding"))).GetProperty("productName").GetString());
    }

    // ---------------------------------------------------------------- banner

    [Fact]
    public void Banner_is_shown_only_between_its_start_and_end_in_the_configured_time_zone()
    {
        var g = new GeneralSettings
        {
            TimeZone = "Australia/Sydney",
            Banner = new BannerSettings { Enabled = true, Type = "Maintenance", Text = "Maintenance tonight 10pm-11pm", StartLocal = "2026-03-05T22:00", EndLocal = "2026-03-05T23:00" },
        };
        // 22:30 in Sydney (UTC+11 in March) is 11:30 UTC.
        Assert.NotNull(SettingsController_Banner(g, new DateTime(2026, 3, 5, 11, 30, 0, DateTimeKind.Utc)));
        Assert.Null(SettingsController_Banner(g, new DateTime(2026, 3, 5, 10, 30, 0, DateTimeKind.Utc))); // 21:30 local: before
        Assert.Null(SettingsController_Banner(g, new DateTime(2026, 3, 5, 12, 30, 0, DateTimeKind.Utc))); // 23:30 local: after, hidden automatically
        g.Banner.Enabled = false;
        Assert.Null(SettingsController_Banner(g, new DateTime(2026, 3, 5, 11, 30, 0, DateTimeKind.Utc)));
    }

    private static object? SettingsController_Banner(GeneralSettings g, DateTime nowUtc) => BannerLogic.ActiveBanner(g, nowUtc);

    [Fact]
    public async Task Banner_rules_information_is_dismissible_others_are_not_plain_text_only_and_changes_are_audited()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");

        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/general", General(banner: b => { b["enabled"] = true; b["text"] = "<b>Hi</b>"; }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/general", General(banner: b => { b["enabled"] = true; b["text"] = new string('x', 301); }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/general", General(banner: b => { b["enabled"] = true; b["text"] = ""; }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/general", General(banner: b => { b["enabled"] = true; b["text"] = "x"; b["type"] = "Alarm"; }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/general", General(banner: b => { b["enabled"] = true; b["text"] = "x"; b["startLocal"] = "2026-03-05T10:00"; b["endLocal"] = "2026-03-05T09:00"; }))).StatusCode);

        await c.Put("/api/settings/general", General(banner: b => { b["enabled"] = true; b["type"] = "Information"; b["text"] = "Welcome back"; }));
        var info = (await c.Json(await c.Get("/api/settings/shell"))).GetProperty("banner");
        Assert.Equal("Welcome back", info.GetProperty("text").GetString());
        Assert.True(info.GetProperty("dismissible").GetBoolean());

        await c.Put("/api/settings/general", General(banner: b => { b["enabled"] = true; b["type"] = "Warning"; b["text"] = "Slow logons today"; }));
        Assert.False((await c.Json(await c.Get("/api/settings/shell"))).GetProperty("banner").GetProperty("dismissible").GetBoolean());

        await c.Put("/api/settings/general", General(banner: b => { b["enabled"] = false; b["text"] = "Slow logons today"; }));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await c.Json(await c.Get("/api/settings/shell"))).GetProperty("banner").ValueKind);

        var actions = app.Db(db => db.AuditLogs.Where(a => a.Action.StartsWith("settings.banner")).OrderBy(a => a.Id).Select(a => a.Action).ToList());
        Assert.Equal(["settings.banner.create", "settings.banner.update", "settings.banner.remove"], actions);
    }

    // ---------------------------------------------------------------- modules

    [Fact]
    public async Task A_disabled_module_answers_every_route_with_the_same_error_and_disappears_from_search_and_the_dashboard()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        var id = FakeDirectoryData.Id("user:alice.smith");

        Assert.Equal(HttpStatusCode.OK, (await c.Put("/api/settings/modules/ad", new { enabled = false })).StatusCode);
        foreach (var url in new[] { "/api/modules/ad/users", $"/api/modules/ad/users/{id}", "/api/modules/ad/computers", "/api/modules/ad/groups", "/api/modules/ad/ous", "/api/modules/ad/settings" })
        {
            var res = await c.Get(url);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            var body = await c.Json(res);
            Assert.Equal("module_disabled", body.GetProperty("code").GetString());
            Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Post($"/api/modules/ad/users/{id}/unlock", new { justification = "long enough justification" })).StatusCode);

        Assert.Equal(0, (await c.Json(await c.Get("/api/search?q=alice"))).GetProperty("modules").GetArrayLength());
        var cards = (await c.Json(await c.Get("/api/dashboard"))).GetProperty("cards").EnumerateArray().Select(x => x.GetProperty("key").GetString()).ToList();
        Assert.DoesNotContain("lockedUsers", cards);
        Assert.Equal("Disabled", (await c.Json(await c.Get("/api/settings/modules"))).EnumerateArray().First(m => m.GetProperty("id").GetString() == "ad").GetProperty("status").GetString());

        await c.Put("/api/settings/modules/ad", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, (await c.Get("/api/modules/ad/users")).StatusCode);
        Assert.Equal(2, app.Db(db => db.AuditLogs.Count(a => a.Action == "settings.modules.update")));
    }

    [Fact]
    public async Task Coming_soon_modules_cannot_be_enabled()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        foreach (var id in new[] { "okta", "m365", "mimecast", "citrix" })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.Put($"/api/settings/modules/{id}", new { enabled = true })).StatusCode);
        var list = await c.Json(await c.Get("/api/settings/modules"));
        Assert.All(list.EnumerateArray().Where(m => m.GetProperty("id").GetString() != "ad"), m => Assert.Equal("Coming Soon", m.GetProperty("status").GetString()));
    }

    // ---------------------------------------------------------------- action policies

    [Fact]
    public async Task Action_policies_are_validated_and_take_effect_immediately()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        var policies = await c.Json(await c.Get("/api/settings/action-policies"));
        var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(policies.GetRawText())!;

        object Build(Func<Dictionary<string, Dictionary<string, object?>>, int> tweak, int length = 16)
        {
            var actions = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object?>>>(policies.GetProperty("actions").GetRawText())!;
            tweak(actions);
            return new { actions, mustChangePasswordDefault = true, generatedPasswordLength = length };
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/action-policies", Build(a => { a["unlock"]["ticketPattern"] = "([unclosed"; return 0; }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/action-policies", Build(a => 0, length: 4))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/action-policies", Build(a => { a["unlock"]["justificationRequired"] = true; a["unlock"]["justificationMinLength"] = 0; return 0; }))).StatusCode);

        var ok = await c.Put("/api/settings/action-policies", Build(a => { a["unlock"]["ticketRequired"] = true; a["unlock"]["ticketPattern"] = "^INC-\\d+$"; return 0; }, length: 20));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(20, (await c.Json(await c.Get("/api/settings/shell"))).GetProperty("actionPolicies").GetProperty("generatedPasswordLength").GetInt32());

        var dave = FakeDirectoryData.Id("user:dave.locked");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Post($"/api/modules/ad/users/{dave}/unlock", new { justification = "long enough justification" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.Post($"/api/modules/ad/users/{dave}/unlock", new { justification = "long enough justification", ticketNumber = "INC-9" })).StatusCode);
        Assert.Contains(app.Db(db => db.AuditLogs.ToList()), a => a.Action == "settings.actionPolicies.update" && a.PreviousValue!.Contains("\"ticketRequired\":false"));
        _ = dict;
    }

    // ---------------------------------------------------------------- AD integration settings

    [Fact]
    public async Task AD_settings_show_the_connection_read_only_validate_dns_and_are_audited()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        var body = await c.Json(await c.Get("/api/modules/ad/settings"));
        Assert.Equal("Fake", body.GetProperty("connection").GetProperty("provider").GetString());
        var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(body.GetProperty("settings").GetRawText())!;

        settings["manageableUserOus"] = new[] { "not a dn" };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/modules/ad/settings", settings)).StatusCode);
        settings["manageableUserOus"] = new[] { "OU=Elsewhere,DC=other,DC=domain" };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/modules/ad/settings", settings)).StatusCode);
        settings["manageableUserOus"] = new[] { FakeDirectoryData.Contractors };
        settings["employeeIdAttribute"] = "employeeID=*)(x";
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/modules/ad/settings", settings)).StatusCode);
        settings["employeeIdAttribute"] = "employeeNumber";
        settings["searchResultLimit"] = 10;
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/modules/ad/settings", settings)).StatusCode);
        settings["searchResultLimit"] = 500;
        Assert.Equal(HttpStatusCode.OK, (await c.Put("/api/modules/ad/settings", settings)).StatusCode);

        // The allowlist really changed: Staff is no longer manageable, so the change is denied.
        var alice = FakeDirectoryData.Id("user:alice.smith");
        var res = await c.Post($"/api/modules/ad/users/{alice}/unlock", new { justification = "long enough justification" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains(app.Db(db => db.AuditLogs.ToList()), a => a.Action == "settings.ad.update" && a.NewValue!.Contains("employeeNumber"));

        var test = await c.Json(await c.Post("/api/modules/ad/settings/test-connection"));
        Assert.True(test.GetProperty("success").GetBoolean());
        Assert.Equal(4, test.GetProperty("steps").GetArrayLength());
    }

    // ---------------------------------------------------------------- personalization

    private static Dictionary<string, object?> Colors(string primary) => new()
    {
        ["primary"] = primary, ["topBarBackground"] = "#ffffff", ["navBackground"] = "#101820", ["navText"] = "#ffffff", ["navSelected"] = "#203040",
        ["pageBackground"] = "#f0f0f0", ["cardBackground"] = "#ffffff", ["sectionHeader"] = "#101820", ["success"] = "#106020", ["warning"] = "#805000", ["error"] = "#a02020",
    };

    [Fact]
    public async Task Colours_are_validated_saved_audited_and_can_be_reset()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/personalization", new { productName = "P", light = Colors("red"), dark = Colors("#000000") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/personalization", new { productName = "P", light = Colors("#12345"), dark = Colors("#000000") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/personalization", new { productName = "P", light = Colors("url(x)"), dark = Colors("#000000") })).StatusCode); // no CSS injection

        Assert.Equal(HttpStatusCode.OK, (await c.Put("/api/settings/personalization", new { productName = "Brand", light = Colors("#aa0000"), dark = Colors("#00aa00") })).StatusCode);
        var branding = await c.Json(await c.Get("/api/settings/branding"));
        Assert.Equal("#aa0000", branding.GetProperty("light").GetProperty("primary").GetString());
        Assert.Equal("Brand", branding.GetProperty("productName").GetString());
        Assert.Contains(app.Db(db => db.AuditLogs.ToList()), a => a.Action == "settings.personalization.update" && a.NewValue!.Contains("#aa0000") && a.PreviousValue!.Contains("#1f5fbf"));

        await c.Post("/api/settings/personalization/reset-colors");
        Assert.Equal("#1f5fbf", (await c.Json(await c.Get("/api/settings/branding"))).GetProperty("light").GetProperty("primary").GetString());
    }

    [Fact]
    public async Task Logos_accept_png_jpeg_webp_by_content_and_refuse_svg_and_oversized_files()
    {
        using var app = new TestApp();
        app.Extra["App:LogoMaxBytes"] = "2048";
        var c = await app.NewClient().SignInAsync("dev.admin");
        var url = "/api/settings/personalization/logo/light";

        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Upload(url, svg, "logo.svg", "image/svg+xml")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Upload(url, svg, "logo.png", "image/png")).StatusCode); // disguised as a PNG
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Upload(url, [.. Png, .. new byte[5000]], "big.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Upload("/api/settings/personalization/logo/..%2F..%2Fevil", Png, "x.png")).StatusCode);

        var ok = await c.Upload(url, Png, "../../weird name.png");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var paths = app.Services.GetRequiredService<ServiceDashboard.Data.AppPaths>();
        Assert.Equal(["logoLight.png"], Directory.GetFiles(paths.LogoDirectory).Select(Path.GetFileName).ToArray()); // the upload name never reaches disk

        var anon = app.NewClient();
        var served = await anon.Get(url);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await served.Content.ReadAsByteArrayAsync());
        var branding = await anon.Json(await anon.Get("/api/settings/branding"));
        Assert.True(branding.GetProperty("hasLogoLight").GetBoolean());
        Assert.False(branding.GetProperty("hasLogoDark").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await c.Delete(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.Get(url)).StatusCode);
        Assert.Empty(Directory.GetFiles(paths.LogoDirectory));
        Assert.Equal(2, app.Db(db => db.AuditLogs.Count(a => a.Action == "settings.personalization.logo")));
    }

    [Fact]
    public async Task The_environment_label_colour_is_validated_saved_and_shown_in_the_shell()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        foreach (var bad in new[] { "red", "#12345", "#gggggg", "#1234567", "rgb(1,2,3)", "url(x)" })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.Put("/api/settings/general", General(g => g["environmentLabelColor"] = bad))).StatusCode);
        Assert.Equal("", (await c.Json(await c.Get("/api/settings/shell"))).GetProperty("environmentLabelColor").GetString()); // automatic by default

        Assert.Equal(HttpStatusCode.OK, (await c.Put("/api/settings/general", General(g => g["environmentLabelColor"] = "#AA3355"))).StatusCode);
        Assert.Equal("#aa3355", (await c.Json(await c.Get("/api/settings/shell"))).GetProperty("environmentLabelColor").GetString());
        Assert.Equal("#aa3355", (await c.Json(await c.Get("/api/settings/general"))).GetProperty("environmentLabelColor").GetString());

        Assert.Equal(HttpStatusCode.OK, (await c.Put("/api/settings/general", General(g => g["environmentLabelColor"] = ""))).StatusCode); // back to automatic
        Assert.Equal("", (await c.Json(await c.Get("/api/settings/shell"))).GetProperty("environmentLabelColor").GetString());
        Assert.Contains(app.Db(db => db.AuditLogs.ToList()), a => a.Action == "settings.general.update" && a.NewValue!.Contains("aa3355"));
    }

    [Fact]
    public async Task A_client_that_leaves_the_environment_colour_out_does_not_reset_it()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        Assert.Equal(HttpStatusCode.OK, (await c.Put("/api/settings/general", General(g => g["environmentLabelColor"] = "#aa3355"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.Put("/api/settings/general", General(g => g.Remove("environmentLabelColor")))).StatusCode); // an older client
        Assert.Equal("#aa3355", (await c.Json(await c.Get("/api/settings/shell"))).GetProperty("environmentLabelColor").GetString());
    }
}
