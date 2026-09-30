using System.Net;
using Microsoft.Extensions.Options;
using ServiceDashboard.Configuration;
using ServiceDashboard.Modules.ActiveDirectory.Providers;
using ServiceDashboard.Modules.ActiveDirectory.Providers.Fake;
using ServiceDashboard.Modules.ActiveDirectory.Providers.Ldap;

namespace ServiceDashboard.Tests;

public class UserStatusTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Account_expiry_understands_never_future_and_past()
    {
        Assert.Equal("Never", UserStatus.AccountExpiry(0, Now).Kind);
        Assert.Equal("Never", UserStatus.AccountExpiry(long.MaxValue, Now).Kind);
        Assert.Equal("Expired", UserStatus.AccountExpiry(Now.AddDays(-1).ToFileTimeUtc(), Now).Kind);
        var (kind, date) = UserStatus.AccountExpiry(Now.AddDays(5).ToFileTimeUtc(), Now);
        Assert.Equal("Expires", kind);
        Assert.Equal(Now.AddDays(5), date);
    }

    [Fact]
    public void Password_status_follows_flags_and_the_computed_expiry_time()
    {
        var set = Now.AddDays(-10).ToFileTimeUtc();
        Assert.Equal("NeverExpires", UserStatus.Password(UserStatus.DontExpirePassword, 0, set, null, Now).Status);
        Assert.Equal("MustChange", UserStatus.Password(0, 0, 0, null, Now).Status);
        Assert.Equal("Expired", UserStatus.Password(0, UserStatus.ComputedPasswordExpired, set, Now.AddDays(-1).ToFileTimeUtc(), Now).Status);
        var (status, when) = UserStatus.Password(0, 0, set, Now.AddDays(20).ToFileTimeUtc(), Now);
        Assert.Equal("Expires", status);
        Assert.Equal(Now.AddDays(20), when);
        Assert.Equal("NeverExpires", UserStatus.Password(0, 0, set, long.MaxValue, Now).Status);
    }

    [Fact]
    public void An_old_lockout_time_is_not_enough_only_the_computed_bit_counts()
    {
        Assert.False(UserStatus.IsLockedOut(0));
        Assert.True(UserStatus.IsLockedOut(UserStatus.ComputedLockout));
    }

    [Fact]
    public void Flags_are_explained_in_plain_language()
    {
        var flags = UserStatus.DescribeFlags(UserStatus.NormalAccount | UserStatus.AccountDisable | UserStatus.DontExpirePassword);
        Assert.Contains(flags, f => f.Name == "ACCOUNTDISABLE" && f.Meaning.Contains("disabled"));
        Assert.Contains(flags, f => f.Name == "DONT_EXPIRE_PASSWORD");
        Assert.DoesNotContain(flags, f => f.Name == "LOCKOUT");
    }
}

public class LdapTextTests
{
    [Theory]
    [InlineData("a*b", "a\\2ab")]
    [InlineData("(x)", "\\28x\\29")]
    [InlineData("back\\slash", "back\\5cslash")]
    [InlineData("nul\0", "nul\\00")]
    public void Filter_values_are_escaped_per_RFC4515(string input, string expected) =>
        Assert.Equal(expected, LdapText.EscapeFilterValue(input));

    [Fact]
    public void Injection_attempts_cannot_add_filter_clauses()
    {
        const string attack = "*)(objectClass=*))(|(objectClass=*";
        foreach (var f in new[]
        {
            LdapFilters.Users(attack, UserFilter.All, "employeeID", DateTime.UtcNow),
            LdapFilters.Computers(attack, ComputerFilter.All),
            LdapFilters.Groups(attack),
            LdapFilters.Members("CN=g,DC=x", attack, null),
        })
        {
            Assert.DoesNotContain("(objectClass=*)", f);
            Assert.Contains("\\2a\\29\\28objectClass=\\2a\\29\\29\\28|\\28objectClass=\\2a", f);
            // Parentheses stay balanced: the attack text added none.
            Assert.Equal(f.Count(c => c == '('), f.Count(c => c == ')'));
        }
    }

    [Fact]
    public void A_malicious_group_DN_is_escaped_inside_the_member_filter()
    {
        var f = LdapFilters.Members("CN=x)(objectClass=*", null, MemberKind.User);
        Assert.Contains("(memberOf=CN=x\\29\\28objectClass=\\2a)", f);
    }

    [Fact]
    public void The_employee_id_attribute_setting_cannot_inject_filter_syntax()
    {
        var f = LdapFilters.Users("bob", UserFilter.All, "employeeID=*)(objectClass=*", DateTime.UtcNow);
        Assert.Contains("(employeeID=*bob*)", f);
        Assert.DoesNotContain("objectClass=*)", f.Replace("(objectCategory=person)(objectClass=user)", ""));
    }

    [Fact]
    public void Guid_filters_use_escaped_bytes()
    {
        var id = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");
        Assert.Equal("(objectGUID=\\44\\33\\22\\11\\66\\55\\88\\77\\99\\aa\\bb\\cc\\dd\\ee\\ff\\00)", LdapFilters.ByGuid(id));
    }

    [Theory]
    [InlineData("OU=Staff,OU=Corp,DC=example,DC=test", true)]
    [InlineData("CN=Smith\\, John,OU=Staff,DC=example,DC=test", true)]
    [InlineData("CN=Hex\\2cName,OU=A,DC=x", true)]
    [InlineData("", false)]
    [InlineData("not a dn", false)]
    [InlineData("OU=A,,DC=x", false)]
    [InlineData("OU=A\0,DC=x", false)]
    [InlineData("OU=A;evil,DC=x", false)]
    [InlineData("OU=A<x>,DC=x", false)]
    [InlineData("OU=trailing\\", false)]
    public void Distinguished_names_are_validated(string dn, bool valid) => Assert.Equal(valid, DnText.IsValidDn(dn));

    [Fact]
    public void Dn_comparison_ignores_case_and_spacing_and_understands_ancestry()
    {
        Assert.True(DnText.Equal("ou=Staff, ou=Corp,dc=X", "OU=Staff,OU=Corp,DC=x"));
        Assert.True(DnText.IsUnderOrEqual("OU=Sales,OU=Staff,OU=Corp,DC=x", "ou=staff,ou=corp,dc=x"));
        Assert.False(DnText.IsUnderOrEqual("OU=Staff,OU=Corp,DC=x", "OU=Sales,OU=Staff,OU=Corp,DC=x"));
        Assert.False(DnText.IsUnderOrEqual("OU=Other,DC=x", "OU=Staff,DC=x"));
    }
}

public class FakeProviderTests
{
    private static FakeDirectoryProvider New() => new(Options.Create(new ActiveDirectoryOptions()));
    private static readonly DirectoryReadOptions Opts = new();
    private static UserSearch Users(string? text = null, UserFilter f = UserFilter.All, int page = 1, int size = 25) =>
        new(text, f, page, size, null, 5000, Opts);

    private static async Task<DirectoryUser> User(FakeDirectoryProvider p, string sam) =>
        (await p.SearchUsersAsync(Users(sam))).Items.First(u => u.SamAccountName == sam);

    [Fact]
    public async Task Seeded_users_cover_every_account_state()
    {
        var p = New();
        Assert.True((await User(p, "dave.locked")).LockedOut);
        Assert.False((await User(p, "erin.disabled")).Enabled);
        Assert.Equal("Expired", (await User(p, "frank.expired")).AccountExpiry);
        Assert.Equal("Expires", (await User(p, "gina.expiring")).AccountExpiry);
        Assert.Equal("Expired", (await User(p, "grace.pwdexpired")).PasswordStatus);
        Assert.Equal("NeverExpires", (await User(p, "henry.neverexpires")).PasswordStatus);
        Assert.Equal("MustChange", (await User(p, "irene.mustchange")).PasswordStatus);
        Assert.Equal("Privileged-Users-PSO", (await User(p, "jack.pso")).ResultantPso);
        Assert.Null((await User(p, "alice.smith")).ResultantPso);
        var alice = await p.GetUserAsync((await User(p, "alice.smith")).Id, Opts);
        Assert.Equal("Bob Jones", alice!.Manager!.Name);
        var kim = await User(p, "kim.sparse");
        Assert.Null(kim.Email); Assert.Null(kim.Title); Assert.Null(kim.Phone);
    }

    [Fact]
    public async Task Filters_and_counts_agree()
    {
        var p = New();
        var locked = await p.SearchUsersAsync(Users(f: UserFilter.Locked, size: 200));
        Assert.All(locked.Items, u => Assert.True(u.LockedOut));
        Assert.Equal(locked.Total, await p.CountUsersAsync(UserFilter.Locked));
        Assert.True(await p.CountUsersAsync(UserFilter.Disabled) > 0);
        Assert.True(await p.CountComputersAsync(ComputerFilter.Disabled) >= 2);
    }

    [Fact]
    public async Task User_search_matches_username_upn_name_email_and_employee_id()
    {
        var p = New();
        foreach (var term in new[] { "alice.smith", "alice.smith@fake.local", "Alice", "Smith", "alice.smith@fake.example", "E1003" })
            Assert.Contains((await p.SearchUsersAsync(Users(term))).Items, u => u.SamAccountName == "alice.smith");
    }

    [Fact]
    public async Task A_group_with_500_plus_members_is_searched_and_paged_by_the_provider()
    {
        var p = New();
        var big = (await p.SearchGroupsAsync(new GroupSearch("GG-All-Company", 1, 10, 100))).Items.Single();
        var group = await p.GetGroupAsync(big.Id);
        Assert.True(group!.MemberCount > 500);

        var page1 = await p.SearchGroupMembersAsync(big.Id, new MemberSearch(null, null, 1, 50));
        Assert.Equal(50, page1.Items.Count);           // only one page comes back...
        Assert.True(page1.Total > 500);                // ...while the total reflects the whole group
        var page2 = await p.SearchGroupMembersAsync(big.Id, new MemberSearch(null, null, 2, 50));
        Assert.Empty(page1.Items.Select(m => m.Id).Intersect(page2.Items.Select(m => m.Id)));

        var filtered = await p.SearchGroupMembersAsync(big.Id, new MemberSearch("alex", null, 1, 25));
        Assert.All(filtered.Items, m => Assert.Contains("alex", m.Name + m.SamAccountName + m.Email, StringComparison.OrdinalIgnoreCase));
        Assert.True(filtered.Total > 0 && filtered.Total < page1.Total);

        var computers = await p.SearchGroupMembersAsync(big.Id, new MemberSearch(null, MemberKind.Computer, 1, 100));
        Assert.All(computers.Items, m => Assert.Equal(MemberKind.Computer, m.Kind));
        Assert.Equal(40, computers.Total);
    }

    [Fact]
    public async Task Nested_and_primary_memberships_are_reported()
    {
        var p = New();
        var alice = await User(p, "alice.smith");
        var m = (await p.GetMembershipsAsync(alice.Id, DirectoryObjectKind.User))!;
        Assert.Contains(m.Direct, g => g.Name == "GG-Engineering");
        Assert.DoesNotContain(m.Direct, g => g.Name == "Domain Users");
        Assert.Equal("Domain Users", m.Primary!.Name);
        var allStaff = m.Nested.Single(n => n.Group.Name == "GG-All-Staff");
        Assert.Equal("GG-Engineering", allStaff.Via);
        Assert.Contains(m.Nested, n => n.Group.Name == "GG-Intranet");
    }

    [Fact]
    public async Task Dry_run_changes_nothing()
    {
        var p = New();
        var dave = await User(p, "dave.locked");
        var r = await p.UnlockAsync(dave.Id, dryRun: true);
        Assert.True(r.Success);
        Assert.True(r.DryRun);
        Assert.Contains(r.Changes, c => c.Field == "Locked out" && c.From == "Yes" && c.To == "No");
        Assert.True((await User(p, "dave.locked")).LockedOut);

        var pw = new System.Security.SecureString();
        foreach (var ch in "Correct-Horse-9") pw.AppendChar(ch);
        Assert.True((await p.ResetPasswordAsync(dave.Id, pw, new ResetPasswordOptions(true, true), dryRun: true)).Success);
        Assert.NotEqual("MustChange", (await User(p, "dave.locked")).PasswordStatus);

        Assert.True((await p.SetEnabledAsync(dave.Id, DirectoryObjectKind.User, false, dryRun: true)).Success);
        Assert.True((await User(p, "dave.locked")).Enabled);
        Assert.True((await p.MoveAsync(dave.Id, DirectoryObjectKind.User, FakeDirectoryData.Contractors, dryRun: true)).Success);
        Assert.Contains("OU=Sales", (await User(p, "dave.locked")).Ou);
    }

    [Fact]
    public async Task Simulated_failures_behave_like_real_AD()
    {
        var p = New();
        var pw = new System.Security.SecureString();
        foreach (var ch in "weak") pw.AppendChar(ch);
        var alice = await User(p, "alice.smith");

        var rejected = await p.ResetPasswordAsync(alice.Id, pw, new ResetPasswordOptions(false, false), dryRun: false);
        Assert.False(rejected.Success);
        Assert.Equal(DirectoryErrors.PasswordRejected, rejected.ErrorCode);
        Assert.DoesNotContain("weak", rejected.Message!);

        Assert.Equal(DirectoryErrors.NotFound, (await p.UnlockAsync(Guid.NewGuid(), dryRun: false)).ErrorCode);

        var noPerm = await User(p, "svc.noperm");
        var dry = await p.SetEnabledAsync(noPerm.Id, DirectoryObjectKind.User, false, dryRun: true);
        Assert.False(dry.Success);
        Assert.Equal(DirectoryErrors.PermissionDenied, dry.ErrorCode);

        p.Simulation.ServerUnavailable = true;
        await Assert.ThrowsAsync<DirectoryUnavailableException>(() => p.SearchUsersAsync(Users()));
    }

    [Fact]
    public async Task The_OU_tree_lazy_loads()
    {
        var p = New();
        var roots = await p.BrowseOusAsync(null);
        Assert.Contains(roots, o => o.Name == "Corp" && o.HasChildren);
        Assert.Contains(roots, o => o.Name == "Domain Controllers");
        var corp = await p.BrowseOusAsync(FakeDirectoryData.Corp);
        Assert.Contains(corp, o => o.Name == "Tier 0");
        Assert.Contains(await p.SearchOusAsync("sales", 10), o => o.Name == "Sales");
    }
}

public class DirectoryApiTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    public DirectoryApiTests(TestApp app) => _app = app;

    [Fact]
    public async Task Read_endpoints_require_their_permission()
    {
        var noaccess = await _app.NewClient().SignInAsync("dev.noaccess");
        foreach (var url in new[]
        {
            "/api/modules/ad/users", "/api/modules/ad/computers", "/api/modules/ad/groups", $"/api/modules/ad/users/{Guid.NewGuid()}",
            $"/api/modules/ad/groups/{Guid.NewGuid()}/members", "/api/modules/ad/ous", "/api/modules/ad/settings",
        })
            Assert.Equal(HttpStatusCode.Forbidden, (await noaccess.Get(url)).StatusCode);

        // The sample helpdesk role can read users and groups but has no computer permission... it has computers.read, so use dev.user.
        var basic = await _app.NewClient().SignInAsync("dev.user");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic.Get("/api/modules/ad/users")).StatusCode);
    }

    [Fact]
    public async Task Helpdesk_can_search_and_open_users_and_sees_protected_tags_on_groups()
    {
        var c = await _app.NewClient().SignInAsync("dev.helpdesk");
        var list = await c.Json(await c.Get("/api/modules/ad/users?q=alice.smith"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var id = list.GetProperty("items")[0].GetProperty("id").GetGuid();

        var detail = await c.Json(await c.Get($"/api/modules/ad/users/{id}"));
        Assert.Equal("alice.smith", detail.GetProperty("user").GetProperty("samAccountName").GetString());
        Assert.True(detail.GetProperty("ouManageable").GetBoolean());

        var groups = await c.Json(await c.Get("/api/modules/ad/groups?q=Domain Admins"));
        var da = groups.GetProperty("items").EnumerateArray().First(g => g.GetProperty("name").GetString() == "Domain Admins");
        Assert.True(da.GetProperty("isProtected").GetBoolean());
        Assert.False(da.GetProperty("isManageable").GetBoolean());
    }

    [Fact]
    public async Task Directory_outage_returns_a_safe_503_with_a_correlation_id()
    {
        using var app = new TestApp();
        var c = await app.NewClient().SignInAsync("dev.admin");
        app.Services.GetRequiredService<FakeDirectoryProvider>().Simulation.ServerUnavailable = true;
        var res = await c.Get("/api/modules/ad/users");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("correlationId", body);
        Assert.DoesNotContain("Simulated", body); // technical detail stays in the logs
    }

    [Fact]
    public async Task Group_member_export_needs_its_own_permission_is_audited_and_protects_against_formulas()
    {
        using var app = new TestApp();
        var helpdesk = await app.NewClient().SignInAsync("dev.helpdesk");
        var big = Guid.Parse(FirstGroupId(await helpdesk.Json(await helpdesk.Get("/api/modules/ad/groups?q=GG-All-Company"))));
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.Get($"/api/modules/ad/groups/{big}/members/export")).StatusCode);

        var auditor = await app.NewClient().SignInAsync("dev.auditor");
        Assert.Equal(HttpStatusCode.OK, (await auditor.Get($"/api/modules/ad/groups/{big}/members/export?q=alex")).StatusCode); // part of the default Auditors role

        var admin = await app.NewClient().SignInAsync("dev.admin");
        var ok = await admin.Get($"/api/modules/ad/groups/{big}/members/export?q=alex");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var csv = await ok.Content.ReadAsStringAsync();
        Assert.StartsWith("﻿Name,Username,Email,Type,Enabled,Distinguished name", csv);
        Assert.Contains(app.Db(db => db.AuditLogs.ToList()), a => a.Action == "ad.groups.member.export" && a.Result == "Success");
    }

    private static string FirstGroupId(System.Text.Json.JsonElement page) => page.GetProperty("items")[0].GetProperty("id").GetString()!;
}
