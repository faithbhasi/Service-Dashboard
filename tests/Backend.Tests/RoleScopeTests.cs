using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ServiceDashboard.Data;
using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers.Fake;

namespace ServiceDashboard.Tests;

/// <summary>Which OUs and groups each role may manage, inside the global allowlists.</summary>
public class RoleScopeTests
{
    private const string Why = "Ticket approved by the line manager";
    private const string Contractors = FakeDirectoryData.Contractors;
    private static Guid UserGuid(string sam) => FakeDirectoryData.Id("user:" + sam);
    private static Guid GroupGuid(string name) => FakeDirectoryData.Id("group:" + name);
    private static string GroupDn(string name) => $"CN={name},{FakeDirectoryData.GroupsOu}";
    private static object Body(bool validateOnly = false) => new { justification = Why, ticketNumber = "INC-1", validateOnly, typedConfirmation = (string?)null };
    private static object GroupsBody(Guid group) => new { justification = Why, ticketNumber = "INC-1", groupIds = new[] { group } };

    private static async Task<JsonElement> RoleOf(TestClient admin, string name) =>
        (await admin.Json(await admin.Get("/api/admin/roles"))).EnumerateArray().First(r => r.GetProperty("name").GetString() == name);

    private static Task<HttpResponseMessage> SetScope(TestClient admin, TestApp app, string role, object scope)
    {
        var r = app.Db(db => db.Roles.Include(x => x.Permissions).Single(x => x.Name == role));
        return admin.Put($"/api/admin/roles/{r.Id}", new { name = r.Name, description = r.Description, permissions = r.Permissions.Select(p => p.Permission).ToArray(), adScope = scope });
    }

    [Fact]
    public async Task The_scope_options_are_the_global_manageable_lists()
    {
        using var app = new TestApp();
        var admin = await app.NewClient().SignInAsync("dev.admin");
        var o = await admin.Json(await admin.Get("/api/admin/roles/ad-scope-options"));
        Assert.Contains(o.GetProperty("userOus").EnumerateArray(), x => x.GetProperty("dn").GetString() == Contractors && x.GetProperty("label").GetString() == "Corp / Contractors");
        Assert.Contains(o.GetProperty("computerOus").EnumerateArray(), x => x.GetProperty("dn").GetString() == FakeDirectoryData.Laptops);
        Assert.Contains(o.GetProperty("groups").EnumerateArray(), x => x.GetProperty("label").GetString() == "GG-Sales");
        var help = await app.NewClient().SignInAsync("dev.helpdesk");
        Assert.Equal(HttpStatusCode.Forbidden, (await help.Get("/api/admin/roles/ad-scope-options")).StatusCode);
    }

    [Fact]
    public async Task A_role_limited_to_some_OUs_and_groups_can_only_change_those()
    {
        using var app = new TestApp();
        var admin = await app.NewClient().SignInAsync("dev.admin");
        var saved = await SetScope(admin, app, "Helpdesk (sample)", new { userOus = new[] { Contractors }, computerOus = (string[]?)null, groups = new[] { GroupDn("GG-Sales") } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(new[] { Contractors }, (await RoleOf(admin, "Helpdesk (sample)")).GetProperty("adScope").GetProperty("userOus").EnumerateArray().Select(x => x.GetString()).ToArray());

        var help = await app.NewClient().SignInAsync("dev.helpdesk");
        // dave.locked is in Staff: the global list allows it, the role does not.
        var denied = await help.Post($"/api/modules/ad/users/{UserGuid("dave.locked")}/unlock", Body());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("Your role is not allowed to manage objects in this OU", await denied.Content.ReadAsStringAsync());
        var dave = await help.Json(await help.Get($"/api/modules/ad/users/{UserGuid("dave.locked")}"));
        Assert.False(dave.GetProperty("ouManageable").GetBoolean());
        Assert.Contains("Your role", dave.GetProperty("ouReason").GetString());
        Assert.True((await help.Json(await help.Get($"/api/modules/ad/users/{UserGuid("dave.locked")}"))).GetProperty("user").GetProperty("lockedOut").GetBoolean()); // nothing changed

        // kim.sparse is a contractor: allowed. Groups are limited too.
        var kim = UserGuid("kim.sparse");
        Assert.True((await help.Json(await help.Get($"/api/modules/ad/users/{kim}"))).GetProperty("ouManageable").GetBoolean());
        var ok = await help.Json(await help.Post($"/api/modules/ad/users/{kim}/groups/add", GroupsBody(GroupGuid("GG-Sales"))));
        Assert.Equal("Success", ok.GetProperty("results")[0].GetProperty("status").GetString());
        var blocked = await help.Json(await help.Post($"/api/modules/ad/users/{kim}/groups/add", GroupsBody(GroupGuid("GG-Finance"))));
        Assert.Equal("Denied", blocked.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Contains("Your role is not allowed to manage this group", blocked.GetProperty("results")[0].GetProperty("message").GetString());

        // The groups the role may add to, and what the group list says is manageable, follow the scope.
        var addable = await help.Json(await help.Get($"/api/modules/ad/users/{kim}/addable-groups"));
        Assert.Equal(["GG-Sales"], addable.EnumerateArray().Select(g => g.GetProperty("name").GetString()).ToArray());
        var finance = await help.Json(await help.Get($"/api/modules/ad/groups/{GroupGuid("GG-Finance")}"));
        Assert.False(finance.GetProperty("isManageable").GetBoolean());
        Assert.True((await help.Json(await help.Get($"/api/modules/ad/groups/{GroupGuid("GG-Sales")}"))).GetProperty("isManageable").GetBoolean());

        // Adding members from the group's own page is held to the same scope.
        var viaGroup = await help.Json(await help.Post($"/api/modules/ad/groups/{GroupGuid("GG-Finance")}/members/add", new { justification = Why, ticketNumber = "INC-1", userIds = new[] { kim } }));
        Assert.Equal("Denied", viaGroup.GetProperty("results")[0].GetProperty("status").GetString());

        // Admins are never limited, and the audit trail shows the denial.
        Assert.Equal(HttpStatusCode.OK, (await admin.Post($"/api/modules/ad/users/{UserGuid("dave.locked")}/unlock", Body())).StatusCode);
        Assert.Contains(app.Db(db => db.AuditLogs.ToList()), a => a.Action == "ad.user.unlock" && a.Result == AuditResult.Denied && a.Error!.Contains("Your role"));
    }

    [Fact]
    public async Task A_role_with_no_scope_keeps_working_as_before_and_an_empty_list_means_nothing()
    {
        using var app = new TestApp();
        var admin = await app.NewClient().SignInAsync("dev.admin");
        var help = await app.NewClient().SignInAsync("dev.helpdesk");
        Assert.Equal(HttpStatusCode.OK, (await help.Post($"/api/modules/ad/users/{UserGuid("dave.locked")}/unlock", Body())).StatusCode);

        await SetScope(admin, app, "Helpdesk (sample)", new { userOus = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Forbidden, (await help.Post($"/api/modules/ad/users/{UserGuid("kim.sparse")}/unlock", Body())).StatusCode);

        // Putting it back to "all allowed" (null) lifts the limit.
        await SetScope(admin, app, "Helpdesk (sample)", new { userOus = (string[]?)null });
        Assert.Equal(HttpStatusCode.OK, (await help.Post($"/api/modules/ad/users/{UserGuid("kim.sparse")}/unlock", Body())).StatusCode);
    }

    [Fact]
    public async Task A_second_role_that_can_change_things_widens_the_reach_but_a_read_only_role_does_not()
    {
        using var app = new TestApp();
        var admin = await app.NewClient().SignInAsync("dev.admin");
        await SetScope(admin, app, "Helpdesk (sample)", new { userOus = new[] { Contractors } });
        var help = app.UserId("dev.helpdesk");

        // Auditors are read-only: having that role as well must not lift the helpdesk limit.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Put($"/api/admin/users/{help}/roles", new { roleIds = new[] { app.RoleId("Helpdesk (sample)"), app.RoleId(DefaultRoles.Auditors) } })).StatusCode);
        var c = await app.NewClient().SignInAsync("dev.helpdesk");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Post($"/api/modules/ad/users/{UserGuid("dave.locked")}/unlock", Body())).StatusCode);

        // A second helpdesk-style role limited to Staff adds Staff to what this person can manage.
        var created = await admin.Post("/api/admin/roles", new { name = "Staff helpdesk", permissions = new[] { Permissions.AdUsersRead, Permissions.AdUsersUnlock }, adScope = new { userOus = new[] { FakeDirectoryData.Staff } } });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        await admin.Put($"/api/admin/users/{help}/roles", new { roleIds = new[] { app.RoleId("Helpdesk (sample)"), app.RoleId("Staff helpdesk") } });
        var c2 = await app.NewClient().SignInAsync("dev.helpdesk");
        Assert.Equal(HttpStatusCode.OK, (await c2.Post($"/api/modules/ad/users/{UserGuid("dave.locked")}/unlock", Body())).StatusCode);
    }

    [Fact]
    public async Task Scope_entries_must_be_on_the_global_list_and_the_change_is_audited()
    {
        using var app = new TestApp();
        var admin = await app.NewClient().SignInAsync("dev.admin");
        var bad = await SetScope(admin, app, "Helpdesk (sample)", new { userOus = new[] { FakeDirectoryData.Servers } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("not on the manageable list", await bad.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await SetScope(admin, app, "Helpdesk (sample)", new { userOus = new[] { Contractors } })).StatusCode);
        var row = app.Db(db => db.AuditLogs.Where(a => a.Action == "admin.role.update").OrderByDescending(a => a.Id).First());
        Assert.Contains("AD scope", row.NewValue);
        Assert.Contains("Corp", row.NewValue);
        Assert.DoesNotContain("AD scope", row.PreviousValue ?? "");

        // Cloning keeps the scope.
        var clone = await admin.Json(await admin.Post($"/api/admin/roles/{app.RoleId("Helpdesk (sample)")}/clone", new { name = "Helpdesk copy" }));
        Assert.Equal(Contractors, clone.GetProperty("adScope").GetProperty("userOus")[0].GetString());
    }

    [Fact]
    public async Task Nobody_can_widen_a_scope_beyond_their_own_reach()
    {
        using var app = new TestApp();
        var admin = await app.NewClient().SignInAsync("dev.admin");
        // A manager who can change users in Contractors only, and who may edit roles.
        var created = await admin.Post("/api/admin/roles", new
        {
            name = "Limited manager", adScope = new { userOus = new[] { Contractors } },
            permissions = new[] { Permissions.AdUsersRead, Permissions.AdUsersUnlock, Permissions.AdminUsersManage, Permissions.AdminRolesManage },
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        await admin.Put($"/api/admin/users/{app.UserId("dev.user")}/roles", new { roleIds = new[] { app.RoleId("Limited manager") } });
        var mgr = await app.NewClient().SignInAsync("dev.user");

        // Giving a role the Staff OU (which they cannot manage) is refused; giving one their own OU is fine.
        var wide = await mgr.Post("/api/admin/roles", new { name = "Wide", permissions = new[] { Permissions.AdUsersUnlock }, adScope = new { userOus = new[] { FakeDirectoryData.Staff } } });
        Assert.Equal(HttpStatusCode.Forbidden, wide.StatusCode);
        var unrestricted = await mgr.Post("/api/admin/roles", new { name = "Everything", permissions = new[] { Permissions.AdUsersUnlock } });
        Assert.Equal(HttpStatusCode.Forbidden, unrestricted.StatusCode);
        var fine = await mgr.Post("/api/admin/roles", new { name = "Narrow", permissions = new[] { Permissions.AdUsersUnlock }, adScope = new { userOus = new[] { Contractors } } });
        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);

        // They cannot hand a broader existing role to someone either.
        await SetScope(admin, app, "Helpdesk (sample)", new { userOus = new[] { FakeDirectoryData.Staff } });
        var assign = await mgr.Put($"/api/admin/users/{app.UserId("dev.noaccess")}/roles", new { roleIds = new[] { app.RoleId("Helpdesk (sample)") } });
        Assert.Equal(HttpStatusCode.Forbidden, assign.StatusCode);
    }

    [Fact]
    public async Task Someone_who_cannot_change_computers_cannot_widen_what_a_role_may_manage_there()
    {
        using var app = new TestApp();
        var admin = await app.NewClient().SignInAsync("dev.admin");
        await admin.Post("/api/admin/roles", new
        {
            name = "Laptop team", adScope = new { computerOus = new[] { FakeDirectoryData.Laptops } },
            permissions = new[] { Permissions.AdComputersRead, Permissions.AdComputersEnable },
        });
        // Access managers who hold no computer permission: they may narrow the scope but not widen it.
        await admin.Post("/api/admin/roles", new { name = "Access only", permissions = new[] { Permissions.AdminRolesManage, Permissions.AdminUsersManage } });
        await admin.Put($"/api/admin/users/{app.UserId("dev.user")}/roles", new { roleIds = new[] { app.RoleId("Access only") } });
        var mgr = await app.NewClient().SignInAsync("dev.user");
        var laptop = app.Db(db => db.Roles.Include(x => x.Permissions).Single(x => x.Name == "Laptop team"));
        var body = (object? scope) => new { name = "Laptop team", permissions = laptop.Permissions.Select(p => p.Permission).ToArray(), adScope = scope };

        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.Put($"/api/admin/roles/{laptop.Id}", body(new { computerOus = new[] { FakeDirectoryData.Laptops, FakeDirectoryData.Workstations } }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.Put($"/api/admin/roles/{laptop.Id}", body(new { computerOus = (string[]?)null }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mgr.Put($"/api/admin/roles/{laptop.Id}", body(new { computerOus = Array.Empty<string>() }))).StatusCode); // narrowing is fine
    }
}
