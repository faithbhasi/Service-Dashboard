using System.Net;

namespace ServiceDashboard.Tests;

public class SearchDashboardTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    public SearchDashboardTests(TestApp app) => _app = app;

    private static string[] Categories(System.Text.Json.JsonElement body) =>
        body.GetProperty("modules").EnumerateArray().SelectMany(m => m.GetProperty("categories").EnumerateArray())
            .Select(c => c.GetProperty("key").GetString()!).ToArray();

    [Fact]
    public async Task Search_needs_two_characters_and_a_session()
    {
        var anon = _app.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Get("/api/search?q=alice")).StatusCode);
        var c = await _app.NewClient().SignInAsync("dev.admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Get("/api/search?q=a")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Get("/api/search")).StatusCode);
    }

    [Fact]
    public async Task Search_returns_users_computers_and_groups_capped_at_five_each_with_a_total()
    {
        var c = await _app.NewClient().SignInAsync("dev.admin");
        var body = await c.Json(await c.Get("/api/search?q=an"));
        Assert.Equal(["users", "computers", "groups"], Categories(body));
        foreach (var cat in body.GetProperty("modules")[0].GetProperty("categories").EnumerateArray())
            Assert.True(cat.GetProperty("items").GetArrayLength() <= 5);
        var users = body.GetProperty("modules")[0].GetProperty("categories")[0];
        Assert.True(users.GetProperty("total").GetInt32() > 5);
        Assert.StartsWith("/ad/users?q=", users.GetProperty("seeAllRoute").GetString());
        Assert.StartsWith("/ad/users/", users.GetProperty("items")[0].GetProperty("route").GetString());
    }

    [Fact]
    public async Task Only_categories_the_user_can_read_are_searched()
    {
        var c = await _app.NewClient().SignInAsync("dev.helpdesk"); // users, computers, groups read
        Assert.Equal(["users", "computers", "groups"], Categories(await c.Json(await c.Get("/api/search?q=an"))));

        var admin = await _app.NewClient().SignInAsync("dev.admin");
        var role = await admin.Json(await admin.Post("/api/admin/roles", new { name = "Groups only", permissions = new[] { "ad.groups.read" } }));
        await admin.Put($"/api/admin/users/{_app.UserId("dev.user")}/roles", new { roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var limited = await _app.NewClient().SignInAsync("dev.user");
        Assert.Equal(["groups"], Categories(await limited.Json(await limited.Get("/api/search?q=gg"))));

        var none = await _app.NewClient().SignInAsync("dev.noaccess");
        var empty = await none.Json(await none.Get("/api/search?q=alice"));
        Assert.Equal(0, empty.GetProperty("modules").GetArrayLength());
    }

    [Fact]
    public async Task Ldap_metacharacters_in_the_search_box_are_harmless()
    {
        var c = await _app.NewClient().SignInAsync("dev.admin");
        var res = await c.Get("/api/search?q=" + Uri.EscapeDataString("*)(objectClass=*"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var cats = (await c.Json(res)).GetProperty("modules")[0].GetProperty("categories").EnumerateArray();
        Assert.All(cats, cat => Assert.Equal(0, cat.GetProperty("total").GetInt32())); // matched literally, so nothing
    }

    [Fact]
    public async Task Search_results_say_when_an_account_is_disabled_or_locked()
    {
        var c = await _app.NewClient().SignInAsync("dev.admin");
        var body = await c.Json(await c.Get("/api/search?q=erin.disabled"));
        var item = body.GetProperty("modules")[0].GetProperty("categories")[0].GetProperty("items")[0];
        Assert.Contains("Disabled", item.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Dashboard_shows_only_the_cards_the_user_may_read()
    {
        static string[] Keys(System.Text.Json.JsonElement b) => b.GetProperty("cards").EnumerateArray().Select(x => x.GetProperty("key").GetString()!).ToArray();

        var admin = await _app.NewClient().SignInAsync("dev.admin");
        var all = Keys(await admin.Json(await admin.Get("/api/dashboard")));
        Assert.Equal(["lockedUsers", "disabledUsers", "expiredUsers", "disabledComputers", "actionsToday", "failedToday"], all);

        var user = await _app.NewClient().SignInAsync("dev.helpdesk");
        Assert.Equal(["lockedUsers", "disabledUsers", "expiredUsers", "disabledComputers", "actionsToday", "failedToday"], Keys(await user.Json(await user.Get("/api/dashboard"))));

        var basic = await _app.NewClient().SignInAsync("dev.user"); // dashboard.read + logs.read.own only
        var basicBody = await basic.Json(await basic.Get("/api/dashboard"));
        Assert.Equal(["actionsToday", "failedToday"], Keys(basicBody));
        Assert.Contains("Your", basicBody.GetProperty("cards")[0].GetProperty("title").GetString());

        var noDash = await _app.NewClient().SignInAsync("dev.noaccess");
        Assert.Equal(HttpStatusCode.Forbidden, (await noDash.Get("/api/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Dashboard_counts_match_the_directory_and_link_to_filtered_lists()
    {
        var c = await _app.NewClient().SignInAsync("dev.admin");
        var cards = (await c.Json(await c.Get("/api/dashboard"))).GetProperty("cards").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!);
        var locked = await c.Json(await c.Get("/api/modules/ad/users?filter=Locked&pageSize=1"));
        Assert.Equal(locked.GetProperty("total").GetInt32(), cards["lockedUsers"].GetProperty("count").GetInt32());
        Assert.Equal("/ad/users?filter=Locked", cards["lockedUsers"].GetProperty("route").GetString());
        Assert.True(cards["lockedUsers"].TryGetProperty("updatedUtc", out _));
    }
}
