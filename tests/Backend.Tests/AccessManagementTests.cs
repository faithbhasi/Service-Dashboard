using System.Net;
using ServiceDashboard.Models;
using ServiceDashboard.Services;

namespace ServiceDashboard.Tests;

public class AccessManagementTests
{
    private static string[] All => Permissions.All.Select(p => p.Id).ToArray();

    /// <summary>Gives dev.user a custom role and returns a signed-in client for them.</summary>
    private static async Task<TestClient> UserWithRole(TestApp app, string roleName, params string[] permissions)
    {
        var admin = await app.NewClient().SignInAsync("dev.admin");
        var res = await admin.Post("/api/admin/roles", new { name = roleName, permissions });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var roleId = (await admin.Json(res)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Put($"/api/admin/users/{app.UserId("dev.user")}/roles", new { roleIds = new[] { roleId } })).StatusCode);
        return await app.NewClient().SignInAsync("dev.user");
    }

    [Fact]
    public async Task Users_without_admin_permissions_are_rejected()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.helpdesk");
        foreach (var url in new[] { "/api/admin/users", "/api/admin/roles", "/api/admin/permissions", "/api/admin/group-mappings" })
            Assert.Equal(HttpStatusCode.Forbidden, (await c.Get(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Post("/api/admin/roles", new { name = "x", permissions = Array.Empty<string>() })).StatusCode);
    }

    [Fact]
    public async Task Denied_api_calls_are_written_to_the_audit_log()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.helpdesk");
        await c.Get("/api/admin/users");
        Assert.Contains(app.Db(db => db.AuditLogs.ToList()), a => a.Action == "api.access" && a.Result == AuditResult.Denied && a.Target!.Contains("/api/admin/users"));
    }

    [Fact]
    public async Task Users_cannot_change_their_own_roles_or_disable_themselves()
    {
        using var app = new TestApp();
        var admin = await app.NewClient().SignInAsync("dev.admin");
        var me = app.UserId("dev.admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Put($"/api/admin/users/{me}/roles", new { roleIds = new[] { app.RoleId("Users") } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Put($"/api/admin/users/{me}/status", new { isEnabled = false })).StatusCode);
    }

    [Fact]
    public async Task Users_cannot_grant_permissions_they_do_not_hold()
    {
        using var app = new TestApp();
        var mgr = await UserWithRole(app, "Access manager", Permissions.AdminUsersManage, Permissions.AdminRolesManage);

        var create = await mgr.Post("/api/admin/roles", new { name = "Sneaky", permissions = new[] { Permissions.AdUsersResetPassword } });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        // Cannot hand out the Admins role either, nor add permissions to the role they hold themselves.
        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.Put($"/api/admin/users/{app.UserId("dev.helpdesk")}/roles", new { roleIds = new[] { DefaultRoles.AdminsId } })).StatusCode);
        var own = app.RoleId("Access manager");
        var edit = await mgr.Put($"/api/admin/roles/{own}", new { name = "Access manager", permissions = new[] { Permissions.AdminUsersManage, Permissions.AdminRolesManage, Permissions.SettingsManage } });
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);

        Assert.Contains(app.Db(db => db.AuditLogs.ToList()), a => a.Action == "admin.role.create" && a.Result == AuditResult.Denied);
    }

    [Fact]
    public async Task The_last_admin_assignment_cannot_be_removed_or_disabled()
    {
        using var app = new TestApp();
        // A second person who holds every permission (but not the Admins role itself) tries to remove the only Admins assignment.
        var other = await UserWithRole(app, "Super ops", All);
        var target = app.UserId("dev.admin");

        var removeRole = await other.Put($"/api/admin/users/{target}/roles", new { roleIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Conflict, removeRole.StatusCode);
        var disable = await other.Put($"/api/admin/users/{target}/status", new { isEnabled = false });
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);

        // Once another Okta group mapping to Admins exists, the direct assignment may go.
        var admin = await app.NewClient().SignInAsync("dev.admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.Post("/api/admin/group-mappings", new { oktaGroup = "IT-Admins", roleId = DefaultRoles.AdminsId })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await other.Put($"/api/admin/users/{target}/roles", new { roleIds = Array.Empty<Guid>() })).StatusCode);

        // ...and now that mapping is the last one.
        var mappingId = app.Db(db => db.GroupMappings.Single(m => m.OktaGroup == "IT-Admins").Id);
        Assert.Equal(HttpStatusCode.Conflict, (await other.Delete($"/api/admin/group-mappings/{mappingId}")).StatusCode);
    }

    [Fact]
    public async Task Default_roles_and_assigned_roles_cannot_be_deleted_and_admins_cannot_be_edited()
    {
        using var app = new TestApp();
        var admin = await app.NewClient().SignInAsync("dev.admin");
        foreach (var name in new[] { "Admins", "Auditors and Security", "Users" })
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.Delete($"/api/admin/roles/{app.RoleId(name)}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Put($"/api/admin/roles/{DefaultRoles.AdminsId}", new { name = "Admins", permissions = new[] { Permissions.DashboardRead } })).StatusCode);

        var helpdesk = app.RoleId("Helpdesk (sample)"); // assigned to dev.helpdesk
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Delete($"/api/admin/roles/{helpdesk}")).StatusCode);

        var fresh = await admin.Post("/api/admin/roles", new { name = "Temp", permissions = new[] { Permissions.DashboardRead } });
        var id = (await admin.Json(fresh)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Delete($"/api/admin/roles/{id}")).StatusCode);
    }

    [Fact]
    public async Task Roles_can_be_created_cloned_and_edited_and_every_change_is_audited()
    {
        using var app = new TestApp();
        var admin = await app.NewClient().SignInAsync("dev.admin");
        var create = await admin.Json(await admin.Post("/api/admin/roles", new { name = "Reporting", description = "d", permissions = new[] { Permissions.DashboardRead } }));
        var id = create.GetProperty("id").GetGuid();

        var clone = await admin.Post($"/api/admin/roles/{id}/clone", new { name = "Reporting copy" });
        Assert.Equal(HttpStatusCode.OK, clone.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Post($"/api/admin/roles/{id}/clone", new { name = "Reporting copy" })).StatusCode);

        var edit = await admin.Put($"/api/admin/roles/{id}", new { name = "Reporting", permissions = new[] { Permissions.DashboardRead, Permissions.LogsRead } });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Put($"/api/admin/roles/{id}", new { name = "Reporting", permissions = new[] { "not.a.permission" } })).StatusCode);

        var actions = app.Db(db => db.AuditLogs.Select(a => a.Action).ToList());
        Assert.Contains("admin.role.create", actions);
        Assert.Contains("admin.role.clone", actions);
        Assert.Contains("admin.role.update", actions);
    }

    [Fact]
    public async Task User_export_is_audited_and_csv_cells_are_protected_against_formulas()
    {
        using var app = new TestApp();
        var admin = await app.NewClient().SignInAsync("dev.admin");
        var res = await admin.Get("/api/admin/users/export");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("text/csv", res.Content.Headers.ContentType!.ToString());
        Assert.Contains("Dev Admin", await res.Content.ReadAsStringAsync());
        Assert.Contains(app.Db(db => db.AuditLogs.ToList()), a => a.Action == "admin.users.export");
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-2", "'-2")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    public void Csv_cells_are_escaped(string input, string expected) => Assert.Equal(expected, CsvWriter.Cell(input));
}
