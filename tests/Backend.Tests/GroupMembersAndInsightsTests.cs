using System.Net;
using System.Text.Json;
using ServiceDashboard.Modules.ActiveDirectory.Providers;
using ServiceDashboard.Modules.ActiveDirectory.Providers.Fake;
using ServiceDashboard.Modules.ActiveDirectory.Providers.Ldap;

namespace ServiceDashboard.Tests;

public class GroupMembersAndInsightsTests
{
    private const string Why = "Ticket approved by the line manager";
    private static Guid UserGuid(string sam) => FakeDirectoryData.Id("user:" + sam);
    private static Guid GroupGuid(string name) => FakeDirectoryData.Id("group:" + name);
    private static object Body(Guid[] users, string? typed = null, bool validateOnly = false) =>
        new { userIds = users, justification = Why, ticketNumber = "INC-1234", typedConfirmation = typed, validateOnly };
    private static string Members(Guid group) => $"/api/modules/ad/groups/{group}/members";

    // ---------------------------------------------------------------- filters

    [Fact]
    public void Department_title_and_os_filters_are_escaped_and_added_to_the_ldap_filter()
    {
        var u = LdapFilters.Users(null, UserFilter.All, "employeeID", DateTime.UtcNow, "Sales)(objectClass=*", "Dir*ector");
        Assert.Contains("(department=*Sales\\29\\28objectClass=\\2a*)", u);
        Assert.Contains("(title=*Dir\\2aector*)", u);
        var c = LdapFilters.Computers(null, ComputerFilter.All, "windows11");
        Assert.Contains("(operatingSystem=*Windows 11*)", c);
        Assert.DoesNotContain("operatingSystem", LdapFilters.Computers(null, ComputerFilter.All, "not-a-real-key"));
    }

    [Fact]
    public async Task Users_can_be_filtered_by_department_and_title_and_computers_by_os()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");

        var sales = await c.Json(await c.Get("/api/modules/ad/users?department=Sales&pageSize=200"));
        Assert.All(sales.GetProperty("items").EnumerateArray(), u => Assert.Equal("Sales", u.GetProperty("department").GetString()));
        Assert.NotEmpty(sales.GetProperty("items").EnumerateArray());

        var directors = await c.Json(await c.Get("/api/modules/ad/users?title=Director&pageSize=200"));
        Assert.Contains(directors.GetProperty("items").EnumerateArray(), u => u.GetProperty("samAccountName").GetString() == "carol.white");

        var both = await c.Json(await c.Get("/api/modules/ad/users?department=Engineering&title=Director"));
        Assert.Equal(["carol.white"], both.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("samAccountName").GetString()).ToArray());

        var servers = await c.Json(await c.Get("/api/modules/ad/computers?os=windowsserver&pageSize=200"));
        Assert.NotEmpty(servers.GetProperty("items").EnumerateArray());
        Assert.All(servers.GetProperty("items").EnumerateArray(), x => Assert.Contains("Windows Server", x.GetProperty("operatingSystem").GetString()));

        var win11 = await c.Json(await c.Get("/api/modules/ad/computers?os=windows11&pageSize=200"));
        Assert.Contains(win11.GetProperty("items").EnumerateArray(), x => x.GetProperty("name").GetString() == "WS-NOPERM");

        // An unknown OS key is ignored rather than breaking the search.
        Assert.Equal(HttpStatusCode.OK, (await c.Get("/api/modules/ad/computers?os=%29%28objectClass%3D%2A")).StatusCode);
    }

    // ---------------------------------------------------------------- group members

    [Fact]
    public async Task Users_can_be_added_to_and_removed_from_a_group_with_a_result_per_user()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        var group = GroupGuid("GG-Helpdesk");
        var alice = UserGuid("alice.smith"); var bob = UserGuid("bob.jones");

        var preview = await c.Json(await c.Post($"{Members(group)}/add", Body([alice, bob], validateOnly: true)));
        Assert.All(preview.GetProperty("results").EnumerateArray(), r => Assert.Equal("Validated", r.GetProperty("status").GetString()));

        var add = await c.Json(await c.Post($"{Members(group)}/add", Body([alice, bob])));
        Assert.Equal(2, add.GetProperty("results").GetArrayLength());
        Assert.All(add.GetProperty("results").EnumerateArray(), r => Assert.Equal("Success", r.GetProperty("status").GetString()));

        var list = await c.Json(await c.Get($"{Members(group)}?pageSize=100"));
        var sams = list.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("samAccountName").GetString()).ToList();
        Assert.Contains("alice.smith", sams); Assert.Contains("bob.jones", sams);

        var again = await c.Json(await c.Post($"{Members(group)}/add", Body([alice])));
        Assert.Equal("Failed", again.GetProperty("results")[0].GetProperty("status").GetString()); // same outcome as from the user's own Groups tab
        Assert.Contains("Already a member", again.GetProperty("results")[0].GetProperty("message").GetString());

        var remove = await c.Json(await c.Post($"{Members(group)}/remove", Body([alice])));
        Assert.Equal("Success", remove.GetProperty("results")[0].GetProperty("status").GetString());
        var after = await c.Json(await c.Get($"{Members(group)}?pageSize=100"));
        Assert.DoesNotContain(after.GetProperty("items").EnumerateArray(), m => m.GetProperty("samAccountName").GetString() == "alice.smith");

        // Audited per user, with the group named, so it also shows in the user's own activity.
        var audit = app.Db(db => db.AuditLogs.Where(a => a.Action == "ad.user.groups.add").ToList());
        Assert.Contains(audit, a => a.Target!.Contains("alice.smith") && a.Target.Contains("GG-Helpdesk"));
    }

    [Fact]
    public async Task Group_member_changes_respect_permissions_protection_and_limits()
    {
        using var app = new TestApp();
        var help = await app.NewClient().SignInAsync("dev.helpdesk"); // can add, cannot remove
        var g = GroupGuid("GG-Helpdesk");
        Assert.Equal(HttpStatusCode.Forbidden, (await help.Post($"{Members(g)}/remove", Body([UserGuid("alice.smith")]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await help.Post($"{Members(g)}/add", Body([UserGuid("alice.smith")]))).StatusCode);

        var none = await app.NewClient().SignInAsync("dev.noaccess");
        Assert.Equal(HttpStatusCode.Forbidden, (await none.Post($"{Members(g)}/add", Body([UserGuid("alice.smith")]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await none.Get($"/api/modules/ad/groups/{g}/addable-users?q=al")).StatusCode);

        var admin = await app.NewClient().SignInAsync("dev.admin");
        var protectedGroup = await admin.Json(await admin.Post($"{Members(GroupGuid("Custom-Protected-Admins"))}/add", Body([UserGuid("alice.smith")])));
        Assert.Equal("Denied", protectedGroup.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post($"{Members(g)}/add", Body([]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post($"{Members(g)}/add", Body(Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray()))).StatusCode);
    }

    [Fact]
    public async Task Addable_users_are_found_by_search_and_flagged_when_already_members()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        var g = GroupGuid("GG-Sales");
        Assert.Empty((await c.Json(await c.Get($"/api/modules/ad/groups/{g}/addable-users?q=a"))).EnumerateArray()); // too short to search
        var found = await c.Json(await c.Get($"/api/modules/ad/groups/{g}/addable-users?q=alice"));
        Assert.Contains(found.EnumerateArray(), u => u.GetProperty("samAccountName").GetString() == "alice.smith");
    }

    // ---------------------------------------------------------------- hourly chart

    [Fact]
    public async Task Hourly_activity_counts_resets_unlocks_and_lockouts_in_the_right_hours()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        var dave = UserGuid("dave.locked");

        var before = await c.Json(await c.Get("/api/modules/ad/activity/hourly?hours=24"));
        Assert.Equal(24, before.GetProperty("points").GetArrayLength());
        Assert.True(before.GetProperty("totalLockouts").GetInt32() >= 1); // the seeded locked accounts
        Assert.Equal(0, before.GetProperty("totalPasswordResets").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await c.Post($"/api/modules/ad/users/{dave}/reset-password", new
        {
            newPassword = "Zq7!Vault-Unique-Secret-9", mustChangeAtNextSignIn = false, unlockAccount = false, justification = Why, ticketNumber = "INC-1", typedConfirmation = "dave.locked",
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.Post($"/api/modules/ad/users/{dave}/unlock", new { justification = Why, ticketNumber = "INC-1" })).StatusCode);

        var after = await c.Json(await c.Get("/api/modules/ad/activity/hourly?hours=24"));
        Assert.Equal(1, after.GetProperty("totalPasswordResets").GetInt32());
        Assert.Equal(1, after.GetProperty("totalUnlocks").GetInt32());
        Assert.Equal(before.GetProperty("totalLockouts").GetInt32() - 1, after.GetProperty("totalLockouts").GetInt32()); // the unlock cleared one
        var last = after.GetProperty("points").EnumerateArray().Last(); // the current hour is the last bar
        Assert.Equal(1, last.GetProperty("passwordResets").GetInt32());
        Assert.Equal(1, last.GetProperty("unlocks").GetInt32());

        Assert.Equal(6, (await c.Json(await c.Get("/api/modules/ad/activity/hourly?hours=1"))).GetProperty("points").GetArrayLength()); // clamped
        var none = await app.NewClient().SignInAsync("dev.noaccess");
        Assert.Equal(HttpStatusCode.Forbidden, (await none.Get("/api/modules/ad/activity/hourly")).StatusCode);
    }
}
