using System.Net;
using Microsoft.Data.Sqlite;
using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers.Fake;
using ServiceDashboard.Services;

namespace ServiceDashboard.Tests;

public class LogsTests
{
    private static void AddRows(TestApp app, params AuditLog[] rows) => app.Db(db => { db.AuditLogs.AddRange(rows); db.SaveChanges(); return 0; });

    private static AuditLog Row(string action, Guid? user, string userName, string result = AuditResult.Success, AuditCategory cat = AuditCategory.Admin,
        string? target = null, string? ticket = null, DateTime? at = null) =>
        new() { Action = action, UserId = user, UserName = userName, Result = result, Category = cat, Target = target, TicketNumber = ticket, TimeUtc = at ?? DateTime.UtcNow };

    private static async Task<int> Total(TestClient c, string url) => (await c.Json(await c.Get(url))).GetProperty("total").GetInt32();

    // ---------------------------------------------------------------- permissions and scope

    [Fact]
    public async Task Log_endpoints_reject_users_without_a_logs_permission()
    {
        using var app = new TestApp();
        var none = await app.NewClient().SignInAsync("dev.noaccess");
        foreach (var url in new[] { "/api/logs/logons", "/api/logs/access", "/api/logs/admin", "/api/logs/user-activity", "/api/logs/entry/1", "/api/logs/users", "/api/logs/users-by", "/api/logs/admin/export" })
            Assert.Equal(HttpStatusCode.Forbidden, (await none.Get(url)).StatusCode);
    }

    [Fact]
    public async Task People_with_only_logs_read_own_see_only_their_own_records_whatever_they_ask_for()
    {
        using var app = new TestApp();
        var admin = await app.NewClient().SignInAsync("dev.admin");
        var helpdesk = await app.NewClient().SignInAsync("dev.helpdesk");
        var adminId = app.UserId("dev.admin");
        AddRows(app, Row("ad.user.unlock", adminId, "Dev Admin", target: "someone"), Row("ad.user.unlock", app.UserId("dev.helpdesk"), "Dev Helpdesk", target: "mine"));

        var mine = await helpdesk.Json(await helpdesk.Get($"/api/logs/admin?userId={adminId}")); // asking for someone else's records
        Assert.All(mine.GetProperty("items").EnumerateArray(), r => Assert.Equal(app.UserId("dev.helpdesk"), r.GetProperty("userId").GetGuid()));
        Assert.True(await Total(admin, "/api/logs/admin") > await Total(helpdesk, "/api/logs/admin"));

        // Cannot open another person's entry, pick users, or use the cross-user reports.
        var others = app.Db(db => db.AuditLogs.First(a => a.UserId == adminId).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await helpdesk.Get($"/api/logs/entry/{others}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Get($"/api/logs/entry/{others}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.Get("/api/logs/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.Get("/api/logs/users-by")).StatusCode);
    }

    // ---------------------------------------------------------------- filters and paging

    [Fact]
    public async Task Logs_are_split_by_tab_and_filter_by_date_user_action_result_target_and_ticket()
    {
        using var app = new TestApp();
        var auditor = await app.NewClient().SignInAsync("dev.auditor");
        var uid = app.UserId("dev.admin");
        var old = DateTime.UtcNow.AddDays(-10);
        AddRows(app,
            Row("logon.signin", uid, "Dev Admin", cat: AuditCategory.Logon),
            Row("page.view", uid, "Dev Admin", cat: AuditCategory.Access, target: "settings"),
            Row("ad.user.disable", uid, "Dev Admin", AuditResult.Failure, target: "alice.smith (Alice Smith)", ticket: "INC-777"),
            Row("ad.user.enable", uid, "Dev Admin", target: "bob.jones", ticket: "INC-888"),
            Row("ad.user.enable", uid, "Dev Admin", target: "old.one", at: old));

        Assert.True(await Total(auditor, "/api/logs/logons") >= 2); // includes the auditor's own sign-in
        Assert.True(await Total(auditor, "/api/logs/access") >= 1);
        Assert.Equal(1, await Total(auditor, "/api/logs/admin?result=Failure&action=ad.user.disable"));
        Assert.Equal(1, await Total(auditor, "/api/logs/admin?target=alice"));
        Assert.Equal(1, await Total(auditor, "/api/logs/admin?ticket=INC-888"));
        Assert.Equal(2, await Total(auditor, "/api/logs/admin?action=ad.user.enable"));
        var recent = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("o"));
        Assert.Equal(1, await Total(auditor, $"/api/logs/admin?action=ad.user.enable&from={recent}"));
        Assert.Equal(0, await Total(auditor, "/api/logs/admin?target=100%25")); // LIKE wildcards in user text are escaped
        Assert.True(await Total(auditor, $"/api/logs/user-activity?userId={uid}") >= 5); // every category

        var page = await auditor.Json(await auditor.Get("/api/logs/admin?pageSize=1&page=2"));
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        Assert.Equal(2, page.GetProperty("page").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await auditor.Get("/api/logs/nonsense")).StatusCode);

        var by = await auditor.Json(await auditor.Get("/api/logs/users-by?action=ad.user.enable"));
        Assert.Equal(2, by[0].GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Admin_actions_show_before_and_after_values_and_a_detail_record()
    {
        using var app = new TestApp();
        var admin = await app.NewClient().SignInAsync("dev.admin");
        await admin.Post($"/api/modules/ad/users/{FakeDirectoryData.Id("user:dave.locked")}/unlock", new { justification = "Verified caller identity by phone" });
        var list = await admin.Json(await admin.Get("/api/logs/admin?action=ad.user.unlock&result=Success"));
        var row = list.GetProperty("items")[0];
        Assert.Contains("Locked out: Yes", row.GetProperty("previousValue").GetString());
        var detail = await admin.Json(await admin.Get($"/api/logs/entry/{row.GetProperty("id").GetInt64()}"));
        Assert.Equal("Verified caller identity by phone", detail.GetProperty("justification").GetString());
        Assert.False(string.IsNullOrEmpty(detail.GetProperty("correlationId").GetString()));
    }

    // ---------------------------------------------------------------- export

    [Fact]
    public async Task Export_needs_logs_export_is_audited_and_neutralises_spreadsheet_formulas()
    {
        using var app = new TestApp();
        var helpdesk = await app.NewClient().SignInAsync("dev.helpdesk");
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.Get("/api/logs/admin/export")).StatusCode);

        var uid = app.UserId("dev.admin");
        AddRows(app, Row("ad.user.disable", uid, "=cmd|' /C calc'!A1", target: "+SUM(1+1)", ticket: "@evil"));
        var auditor = await app.NewClient().SignInAsync("dev.auditor");
        var res = await auditor.Get("/api/logs/admin/export?action=ad.user.disable");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("attachment", res.Content.Headers.ContentDisposition!.ToString());
        var csv = await res.Content.ReadAsStringAsync();
        Assert.Contains("'=cmd|", csv);
        Assert.Contains("'+SUM(1+1)", csv);
        Assert.Contains("'@evil", csv);
        Assert.DoesNotContain(",=cmd", csv);
        Assert.Contains("Time (UTC),Category,User,Action", csv);

        var export = app.Db(db => db.AuditLogs.Single(a => a.Action == "logs.export"));
        Assert.Equal(AuditResult.Success, export.Result);
        Assert.Contains("action 'ad.user.disable'", export.NewValue);
        Assert.Equal(app.UserId("dev.auditor"), export.UserId);
    }

    [Fact]
    public async Task Export_over_the_row_limit_asks_the_user_to_narrow_the_filter()
    {
        using var app = new TestApp();
        app.Extra["App:ExportRowLimit"] = "3";
        var uid = app.UserId("dev.admin");
        AddRows(app, Enumerable.Range(0, 5).Select(i => Row("ad.user.enable", uid, "Dev Admin", target: "t" + i)).ToArray());
        var auditor = await app.NewClient().SignInAsync("dev.auditor");
        var res = await auditor.Get("/api/logs/admin/export");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Narrow the filter", await res.Content.ReadAsStringAsync());
        Assert.Contains(app.Db(db => db.AuditLogs.ToList()), a => a.Action == "logs.export" && a.Result == AuditResult.Failure);
        Assert.Equal(HttpStatusCode.OK, (await auditor.Get("/api/logs/admin/export?action=ad.user.enable&target=t1")).StatusCode);
    }

    // ---------------------------------------------------------------- object history

    [Fact]
    public async Task Object_activity_history_lists_changes_made_through_the_app()
    {
        using var app = new TestApp();
        var helpdesk = await app.NewClient().SignInAsync("dev.helpdesk");
        var dave = FakeDirectoryData.Id("user:dave.locked");
        await helpdesk.Post($"/api/modules/ad/users/{dave}/unlock", new { justification = "Caller verified by phone" });
        var hist = await helpdesk.Json(await helpdesk.Get($"/api/modules/ad/users/{dave}/activity"));
        Assert.Contains(hist.GetProperty("items").EnumerateArray(), r => r.GetProperty("action").GetString() == "ad.user.unlock");

        var none = await app.NewClient().SignInAsync("dev.user"); // no ad.users.read
        Assert.Equal(HttpStatusCode.Forbidden, (await none.Get($"/api/modules/ad/users/{dave}/activity")).StatusCode);
    }

    // ---------------------------------------------------------------- retention and backup

    [Fact]
    public async Task Retention_removes_old_audit_rows_and_logs_that_it_did()
    {
        using var app = new TestApp();
        app.Extra["App:AuditRetentionDays"] = "30";
        var uid = app.UserId("dev.admin");
        AddRows(app, Row("old.action", uid, "x", at: DateTime.UtcNow.AddDays(-90)), Row("old.action", uid, "x", at: DateTime.UtcNow.AddDays(-31)), Row("new.action", uid, "x"));
        using var scope = app.Services.CreateScope();
        var removed = await scope.ServiceProvider.GetRequiredService<MaintenanceTasks>().PurgeAuditAsync();
        Assert.Equal(2, removed);
        var rows = app.Db(db => db.AuditLogs.ToList());
        Assert.DoesNotContain(rows, a => a.Action == "old.action");
        Assert.Contains(rows, a => a.Action == "new.action");
        Assert.Contains(rows, a => a.Action == "maintenance.auditRetention" && a.NewValue!.Contains("Removed 2"));
    }

    [Fact]
    public async Task Online_backup_writes_a_consistent_copy_with_the_logo_folder_and_keeps_only_the_newest()
    {
        using var app = new TestApp();
        app.Extra["App:BackupRetentionCount"] = "2";
        await app.NewClient().SignInAsync("dev.admin"); // creates data
        var paths = app.Services.GetRequiredService<ServiceDashboard.Data.AppPaths>();
        File.WriteAllText(Path.Combine(paths.LogoDirectory, "logoLight.png"), "fake");
        var folders = new List<string>();
        using (var scope = app.Services.CreateScope())
        {
            var tasks = scope.ServiceProvider.GetRequiredService<MaintenanceTasks>();
            for (var i = 0; i < 3; i++) { folders.Add((await tasks.BackupAsync())!); await Task.Delay(30); }
        }
        Assert.All(folders, Assert.NotNull);
        Assert.False(Directory.Exists(folders[0])); // pruned
        Assert.True(Directory.Exists(folders[1]) && Directory.Exists(folders[2]));

        var copy = Path.Combine(folders[2], "service-dashboard.db");
        Assert.True(File.Exists(Path.Combine(folders[2], "assets", "logos", "logoLight.png")));
        using var conn = new SqliteConnection($"Data Source={copy};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AuditLogs WHERE Action = 'logon.signin'";
        Assert.True(Convert.ToInt32(cmd.ExecuteScalar()) >= 1);
        Assert.Contains(app.Db(db => db.AuditLogs.ToList()), a => a.Action == "maintenance.backup");
        SqliteConnection.ClearAllPools();
    }
}
