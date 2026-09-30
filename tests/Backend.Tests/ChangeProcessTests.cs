using System.Net;
using System.Text.Json;
using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers.Fake;
using ServiceDashboard.Services;

namespace ServiceDashboard.Tests;

public class ChangeProcessTests
{
    private const string Why = "Ticket approved by the line manager";
    private static object Body(object? extra = null, string? typed = null, bool validateOnly = false) => new Dictionary<string, object?>
    {
        ["justification"] = Why, ["ticketNumber"] = "INC-1234", ["typedConfirmation"] = typed, ["validateOnly"] = validateOnly,
        ["targetOu"] = (extra as Dictionary<string, object?>)?.GetValueOrDefault("targetOu"),
        ["groupIds"] = (extra as Dictionary<string, object?>)?.GetValueOrDefault("groupIds"),
        ["newPassword"] = (extra as Dictionary<string, object?>)?.GetValueOrDefault("newPassword"),
        ["mustChangeAtNextSignIn"] = (extra as Dictionary<string, object?>)?.GetValueOrDefault("mustChangeAtNextSignIn") ?? false,
        ["unlockAccount"] = (extra as Dictionary<string, object?>)?.GetValueOrDefault("unlockAccount") ?? false,
    };
    private static Dictionary<string, object?> With(string key, object? value) => new() { [key] = value };

    private static Guid UserGuid(string sam) => FakeDirectoryData.Id("user:" + sam);
    private static Guid GroupGuid(string name) => FakeDirectoryData.Id("group:" + name);
    private static Guid ComputerGuid(string name) => FakeDirectoryData.Id("computer:" + name);
    private static string Url(Guid id, string action) => $"/api/modules/ad/users/{id}/{action}";

    private static async Task<JsonElement> UserOf(TestClient c, string sam) =>
        (await c.Json(await c.Get($"/api/modules/ad/users/{UserGuid(sam)}"))).GetProperty("user");

    // ---------------------------------------------------------------- permissions

    [Fact]
    public async Task Every_change_endpoint_rejects_users_without_its_permission()
    {
        using var app = new TestApp();
        var helpdesk = await app.NewClient().SignInAsync("dev.helpdesk"); // read, unlock, reset password, add groups only
        var id = UserGuid("alice.smith");
        foreach (var action in new[] { "enable", "disable", "move", "groups/remove" })
            Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.Post(Url(id, action), Body())).StatusCode);
        foreach (var action in new[] { "enable", "disable", "move" })
            Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.Post($"/api/modules/ad/computers/{ComputerGuid("WS-001")}/{action}", Body())).StatusCode);

        var none = await app.NewClient().SignInAsync("dev.noaccess");
        foreach (var action in new[] { "reset-password", "unlock", "enable", "disable", "move", "groups/add", "groups/remove" })
            Assert.Equal(HttpStatusCode.Forbidden, (await none.Post(Url(id, action), Body())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await none.Get($"/api/modules/ad/users/{id}/addable-groups")).StatusCode);

        var auditor = await app.NewClient().SignInAsync("dev.auditor"); // read-only
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.Post(Url(id, "unlock"), Body())).StatusCode);
    }

    // ---------------------------------------------------------------- the pipeline

    [Fact]
    public async Task Unlock_runs_the_full_pipeline_and_is_audited_with_justification_and_ticket()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.helpdesk");
        var dave = UserGuid("dave.locked");
        Assert.True((await UserOf(c, "dave.locked")).GetProperty("lockedOut").GetBoolean());

        var res = await c.Post(Url(dave, "unlock"), Body());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await c.Json(res);
        Assert.Equal("Success", body.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
        Assert.False((await UserOf(c, "dave.locked")).GetProperty("lockedOut").GetBoolean());

        var rows = app.Db(db => db.AuditLogs.Where(a => a.Action == "ad.user.unlock").OrderBy(a => a.Id).ToList());
        Assert.Equal([AuditResult.Validated, AuditResult.Success], rows.Select(r => r.Result));
        var done = rows[1];
        Assert.Equal(Why, done.Justification);
        Assert.Equal("INC-1234", done.TicketNumber);
        Assert.Equal(dave.ToString(), done.TargetId);
        Assert.Contains("Locked out: Yes", done.PreviousValue);
        Assert.Contains("Locked out: No", done.NewValue);
        Assert.Equal(body.GetProperty("correlationId").GetString(), done.CorrelationId);
    }

    [Fact]
    public async Task Unlocking_an_account_that_is_no_longer_locked_makes_no_change_and_says_so()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.helpdesk");
        var body = await c.Json(await c.Post(Url(UserGuid("alice.smith"), "unlock"), Body()));
        Assert.Equal("NoChange", body.GetProperty("status").GetString());
        Assert.Contains("no longer locked", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Justification_and_typed_confirmation_are_enforced_from_the_action_policies()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        var id = UserGuid("alice.smith");

        var none = await c.Post(Url(id, "disable"), new { justification = "", ticketNumber = "" });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("justification", (await none.Content.ReadAsStringAsync()).ToLower());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Post(Url(id, "disable"), new { justification = "short" })).StatusCode);

        // Disabling asks for the typed account name by default.
        var wrongName = await c.Post(Url(id, "disable"), Body(typed: "nope"));
        Assert.Equal(HttpStatusCode.BadRequest, wrongName.StatusCode);
        Assert.True((await UserOf(c, "alice.smith")).GetProperty("enabled").GetBoolean());

        var ok = await c.Post(Url(id, "disable"), Body(typed: "alice.smith"));
        Assert.Equal("Success", (await c.Json(ok)).GetProperty("status").GetString());
        Assert.False((await UserOf(c, "alice.smith")).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Ticket_number_format_from_the_action_policy_is_enforced()
    {
        using var app = new TestApp();
        using (var scope = app.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var policies = new ActionPoliciesSettings();
            policies.Actions[ActionKeys.Unlock].TicketRequired = true;
            policies.Actions[ActionKeys.Unlock].TicketPattern = "^INC-\\d{4}$";
            await settings.SaveAsync(SettingKeys.ActionPolicies, policies, "test", () => new ActionPoliciesSettings());
        }
        var c = await app.NewClient().SignInAsync("dev.helpdesk");
        var dave = UserGuid("dave.locked");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Post(Url(dave, "unlock"), new { justification = Why })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Post(Url(dave, "unlock"), new { justification = Why, ticketNumber = "bad" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.Post(Url(dave, "unlock"), new { justification = Why, ticketNumber = "INC-1234" })).StatusCode);
    }

    // ---------------------------------------------------------------- dry run

    [Fact]
    public async Task Validate_only_never_changes_the_directory_and_is_audited_as_validated()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.helpdesk");
        var dave = UserGuid("dave.locked");
        var res = await c.Json(await c.Post(Url(dave, "unlock"), Body(validateOnly: true)));
        Assert.Equal("Validated", res.GetProperty("status").GetString());
        Assert.True(res.GetProperty("dryRun").GetBoolean());
        Assert.Equal("Locked out", res.GetProperty("changes")[0].GetProperty("field").GetString());
        Assert.True(res.GetProperty("checks").GetArrayLength() >= 2);
        Assert.True((await UserOf(c, "dave.locked")).GetProperty("lockedOut").GetBoolean()); // still locked

        var rows = app.Db(db => db.AuditLogs.Where(a => a.Action == "ad.user.unlock").ToList());
        Assert.Single(rows);
        Assert.Equal(AuditResult.Validated, rows[0].Result);
        Assert.Equal("Validated (no change made)", rows[0].Result);
    }

    [Fact]
    public async Task A_failed_dry_run_blocks_the_real_change()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        var noPerm = UserGuid("svc.noperm"); // the simulated service account has no rights on this one
        var res = await c.Post(Url(noPerm, "disable"), Body(typed: "svc.noperm"));
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        var body = await c.Json(res);
        Assert.Equal("Failed", body.GetProperty("status").GetString());
        Assert.StartsWith("Validation failed", body.GetProperty("message").GetString());
        Assert.Contains(body.GetProperty("checks").EnumerateArray(), ch => !ch.GetProperty("passed").GetBoolean());
        Assert.True((await UserOf(c, "svc.noperm")).GetProperty("enabled").GetBoolean()); // nothing was written

        var rows = app.Db(db => db.AuditLogs.Where(a => a.Action == "ad.user.disable").ToList());
        Assert.DoesNotContain(rows, r => r.Result == AuditResult.Success);
        Assert.Contains(rows, r => r.Result == AuditResult.Failure);
    }

    // ---------------------------------------------------------------- protected groups and OUs, through the API directly

    [Fact]
    public async Task Protected_and_non_allowlisted_groups_cannot_be_changed_even_when_sent_directly_to_the_API()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        var alice = UserGuid("alice.smith");

        foreach (var g in new[] { "Domain Admins", "Enterprise Admins", "Administrators", "Backup Operators", "Custom-Protected-Admins", "Domain Users" })
        {
            var res = await c.Json(await c.Post(Url(alice, "groups/add"), Body(With("groupIds", new[] { GroupGuid(g) }))));
            var r = res.GetProperty("results")[0];
            Assert.Equal("Denied", r.GetProperty("status").GetString());
        }
        var m = await c.Json(await c.Get($"/api/modules/ad/users/{alice}/groups"));
        Assert.DoesNotContain(m.GetProperty("direct").EnumerateArray(), g => g.GetProperty("name").GetString() == "Domain Admins");

        // Removing from a protected group is blocked too (jack.pso is in Custom-Protected-Admins).
        var rem = await c.Json(await c.Post(Url(UserGuid("jack.pso"), "groups/remove"), Body(With("groupIds", new[] { GroupGuid("Custom-Protected-Admins") }))));
        Assert.Equal("Denied", rem.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Contains(app.Db(db => db.AuditLogs.ToList()), a => a.Result == AuditResult.Denied && a.Action == "ad.user.groups.add");
    }

    [Fact]
    public async Task Users_in_protected_or_admin_OUs_and_moves_into_them_are_blocked()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        var alice = UserGuid("alice.smith");

        foreach (var ou in new[] { FakeDirectoryData.Tier0, FakeDirectoryData.DomainControllers, FakeDirectoryData.ServiceAccounts, FakeDirectoryData.Servers })
        {
            var res = await c.Post(Url(alice, "move"), Body(With("targetOu", ou)));
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            Assert.Equal("Denied", (await c.Json(res)).GetProperty("status").GetString());
        }
        Assert.Contains("Engineering", (await UserOf(c, "alice.smith")).GetProperty("ou").GetString());

        // Objects already sitting in a blocked OU cannot be changed at all.
        var tier0User = UserGuid("adm.tier0");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Post(Url(tier0User, "unlock"), Body())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Post(Url(tier0User, "move"), Body(With("targetOu", FakeDirectoryData.Contractors)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Post(Url(UserGuid("svc.backup"), "disable"), Body(typed: "svc.backup"))).StatusCode);

        // A valid move to an allowed OU works.
        var ok = await c.Post(Url(alice, "move"), Body(With("targetOu", FakeDirectoryData.Contractors)));
        Assert.Equal("Success", (await c.Json(ok)).GetProperty("status").GetString());
        Assert.StartsWith("OU=Contractors", (await UserOf(c, "alice.smith")).GetProperty("ou").GetString());
    }

    [Fact]
    public async Task Malformed_or_outside_domain_OUs_are_rejected()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        foreach (var bad in new[] { "not a dn", "OU=X,DC=other,DC=domain", "" })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.Post(Url(UserGuid("alice.smith"), "move"), Body(With("targetOu", bad)))).StatusCode);
    }

    [Fact]
    public async Task Computers_can_be_disabled_and_moved_within_allowed_OUs_only()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        var ws = ComputerGuid("WS-001");
        var url = (string a) => $"/api/modules/ad/computers/{ws}/{a}";

        var dis = await c.Json(await c.Post(url("disable"), Body(typed: "WS-001")));
        Assert.Equal("Success", dis.GetProperty("status").GetString());
        Assert.False((await c.Json(await c.Get($"/api/modules/ad/computers/{ws}"))).GetProperty("computer").GetProperty("enabled").GetBoolean());
        Assert.Equal("Success", (await c.Json(await c.Post(url("enable"), Body()))).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await c.Post(url("move"), Body(With("targetOu", FakeDirectoryData.Servers)))).StatusCode);
        Assert.Equal("Success", (await c.Json(await c.Post(url("move"), Body(With("targetOu", FakeDirectoryData.Laptops))))).GetProperty("status").GetString());

        // Domain controllers are always blocked.
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Post($"/api/modules/ad/computers/{ComputerGuid("DC01")}/disable", Body(typed: "DC01"))).StatusCode);
    }

    // ---------------------------------------------------------------- group membership

    [Fact]
    public async Task Adding_to_several_groups_reports_each_one_and_the_primary_group_cannot_be_removed()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        var alice = UserGuid("alice.smith"); // already in GG-VPN-Users, not in GG-Helpdesk

        var addable = await c.Json(await c.Get($"/api/modules/ad/users/{alice}/addable-groups"));
        var names = addable.EnumerateArray().Select(g => g.GetProperty("name").GetString()).ToList();
        Assert.Contains("GG-Helpdesk", names);
        Assert.DoesNotContain("Domain Admins", names);
        Assert.DoesNotContain("Domain Users", names);
        Assert.True(addable.EnumerateArray().First(g => g.GetProperty("name").GetString() == "GG-VPN-Users").GetProperty("alreadyMember").GetBoolean());

        var res = await c.Json(await c.Post(Url(alice, "groups/add"), Body(With("groupIds", new[] { GroupGuid("GG-Helpdesk"), GroupGuid("GG-VPN-Users"), GroupGuid("APP-CRM-Users") }))));
        var byGroup = res.GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("groupName").GetString()!);
        Assert.Equal("Success", byGroup["GG-Helpdesk"].GetProperty("status").GetString());
        Assert.Equal("Failed", byGroup["GG-VPN-Users"].GetProperty("status").GetString());
        Assert.Contains("Already a member", byGroup["GG-VPN-Users"].GetProperty("message").GetString());
        Assert.Equal("Success", byGroup["APP-CRM-Users"].GetProperty("status").GetString());

        var rem = await c.Json(await c.Post(Url(alice, "groups/remove"), Body(With("groupIds", new[] { GroupGuid("GG-Helpdesk") }))));
        Assert.Equal("Success", rem.GetProperty("results")[0].GetProperty("status").GetString());
        var primary = await c.Json(await c.Post(Url(alice, "groups/remove"), Body(With("groupIds", new[] { GroupGuid("Domain Users") }))));
        Assert.Equal("Denied", primary.GetProperty("results")[0].GetProperty("status").GetString());
    }

    // ---------------------------------------------------------------- password reset

    [Fact]
    public async Task Password_reset_works_and_the_password_never_appears_in_logs_audit_or_responses()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.helpdesk");
        var dave = UserGuid("dave.locked");
        const string Secret = "Zq7!Vault-Unique-Secret-9";

        var preview = await c.Post(Url(dave, "reset-password"), Body(validateOnly: true));
        var previewBody = await preview.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

        var res = await c.Post(Url(dave, "reset-password"), Body(new Dictionary<string, object?> { ["newPassword"] = Secret, ["mustChangeAtNextSignIn"] = true, ["unlockAccount"] = true }, typed: "dave.locked"));
        var resBody = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Success", resBody);

        var user = await UserOf(c, "dave.locked");
        Assert.Equal("MustChange", user.GetProperty("passwordStatus").GetString());
        Assert.False(user.GetProperty("lockedOut").GetBoolean());

        // A rejected password (policy) must not be echoed either.
        const string Weak = "weakpw-Echo-Check";
        var rejected = await c.Post(Url(UserGuid("kim.sparse"), "reset-password"), Body(new Dictionary<string, object?> { ["newPassword"] = "aB1" }, typed: "kim.sparse"));
        var rejectedBody = await rejected.Content.ReadAsStringAsync();
        Assert.Equal((HttpStatusCode)422, rejected.StatusCode);
        Assert.Contains("password policy", rejectedBody);
        Assert.DoesNotContain("aB1\"", rejectedBody);

        // A bad request shape must not echo it.
        var bad = await c.Post(Url(dave, "reset-password"), new { newPassword = Secret, justification = "x" });
        var badBody = await bad.Content.ReadAsStringAsync();

        var audit = System.Text.Json.JsonSerializer.Serialize(app.Db(db => db.AuditLogs.ToList()));
        var logs = app.Logs.AllText();
        Assert.Contains("reset-password", logs); // the capture works, so the absence below means something
        foreach (var (name, text) in new[] { ("preview", previewBody), ("response", resBody), ("rejected", rejectedBody), ("bad request", badBody), ("audit", audit), ("logs", logs) })
        {
            Assert.False(text.Contains(Secret), $"the password leaked into the {name}");
            Assert.False(text.Contains(Weak), $"the password leaked into the {name}");
        }
        Assert.Contains(app.Db(db => db.AuditLogs.ToList()), a => a.Action == "ad.user.resetPassword" && a.Result == AuditResult.Success && a.NewValue!.Contains("not shown"));
    }

    [Fact]
    public async Task Password_reset_requires_a_password_unless_only_validating()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.helpdesk");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Post(Url(UserGuid("dave.locked"), "reset-password"), Body(typed: "dave.locked"))).StatusCode);
    }

    // ---------------------------------------------------------------- failures

    [Fact]
    public async Task A_directory_outage_is_audited_as_a_failure_and_returns_503()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.helpdesk");
        app.Services.GetRequiredService<FakeDirectoryProvider>().Simulation.ServerUnavailable = true;
        var res = await c.Post(Url(UserGuid("dave.locked"), "unlock"), Body());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains(app.Db(db => db.AuditLogs.ToList()), a => a.Action == "ad.user.unlock" && a.Result == AuditResult.Failure);
    }

    [Fact]
    public async Task An_unknown_object_is_reported_and_audited()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.helpdesk");
        var res = await c.Post(Url(Guid.NewGuid(), "unlock"), Body());
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        Assert.Equal("NotFound", (await c.Json(res)).GetProperty("errorCode").GetString());
        Assert.Contains(app.Db(db => db.AuditLogs.ToList()), a => a.Action == "ad.user.unlock" && a.Result == AuditResult.Failure);
    }
}
