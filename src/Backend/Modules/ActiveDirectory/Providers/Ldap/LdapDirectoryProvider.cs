using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Options;
using ServiceDashboard.Configuration;

namespace ServiceDashboard.Modules.ActiveDirectory.Providers.Ldap;

/// <summary>
/// Active Directory over LDAPS using System.DirectoryServices.Protocols. Binds as the process identity (the IIS app pool's gMSA),
/// so no password is stored. One short-lived connection per operation keeps it simple and thread-safe.
/// Every write supports a dry run that only reads (including allowedAttributesEffective / allowedChildClassesEffective).
/// </summary>
public sealed class LdapDirectoryProvider(IOptions<ActiveDirectoryOptions> options, ILogger<LdapDirectoryProvider> log) : IDirectoryProvider
{
    private readonly ActiveDirectoryOptions _o = options.Value;

    public string ProviderName => "Ldap";
    public string BaseDn => _o.BaseDn;
    public SuggestedAllowlists? SuggestedDefaults => null; // a real directory starts with nothing manageable

    private const int ServerDown = 81, Timeout = 85, ConnectError = 91;
    private const int MaxVlvPage = 1000;

    // ------------------------------------------------------------------ plumbing

    private LdapConnection Connect()
    {
        var id = new LdapDirectoryIdentifier(_o.Server, _o.Port, fullyQualifiedDnsHostName: true, connectionless: false);
        var c = new LdapConnection(id) { Timeout = TimeSpan.FromSeconds(30), AuthType = AuthType.Negotiate };
        c.SessionOptions.ProtocolVersion = 3;
        c.SessionOptions.SecureSocketLayer = _o.UseLdaps;
        c.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        // Certificate checks stay on. The switch exists only for local troubleshooting and the app refuses to start with it off in Production.
        if (!_o.VerifyCertificate) c.SessionOptions.VerifyServerCertificate = (_, _) => true;
        if (!string.IsNullOrEmpty(_o.BindUsername))
        {
            // Local testing only (the startup checks refuse this outside Development). Basic is only used over LDAPS.
            c.AuthType = _o.UseLdaps ? AuthType.Basic : AuthType.Negotiate;
            c.Credential = new System.Net.NetworkCredential(_o.BindUsername, _o.BindPassword);
        }
        c.Bind(); // otherwise integrated: the app pool identity (the gMSA)
        return c;
    }

    private Task<T> Run<T>(Func<LdapConnection, T> work, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var c = Connect();
            return work(c);
        }
        catch (LdapException ex) when (ex.ErrorCode is ServerDown or Timeout or ConnectError)
        {
            throw new DirectoryUnavailableException($"LDAP error {ex.ErrorCode}: {ex.Message}", ex);
        }
        catch (LdapException ex)
        {
            log.LogError(ex, "LDAP bind or search failed with code {Code}", ex.ErrorCode);
            throw new DirectoryUnavailableException($"LDAP error {ex.ErrorCode}", ex);
        }
    }, ct);

    // ------------------------------------------------------------------ attribute helpers

    private static string? S(SearchResultEntry e, string attr) =>
        e.Attributes.Contains(attr) && e.Attributes[attr].Count > 0 ? e.Attributes[attr][0] as string : null;

    private static long L(SearchResultEntry e, string attr) =>
        long.TryParse(S(e, attr), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int I(SearchResultEntry e, string attr) => (int)Math.Clamp(L(e, attr), int.MinValue, int.MaxValue);

    private static Guid GuidOf(SearchResultEntry e) =>
        e.Attributes.Contains("objectGUID") && e.Attributes["objectGUID"][0] is byte[] b ? new Guid(b) : Guid.Empty;

    private static DateTime? GeneralizedTime(SearchResultEntry e, string attr) =>
        DateTime.TryParseExact(S(e, attr), ["yyyyMMddHHmmss.0Z", "yyyyMMddHHmmss.fZ"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    private static DateTime? FileTime(SearchResultEntry e, string attr) => UserStatus.FromFileTime(L(e, attr));

    private static List<string> Values(SearchResultEntry e, string attr) =>
        e.Attributes.Contains(attr) ? e.Attributes[attr].GetValues(typeof(string)).Cast<string>().ToList() : [];

    private static string OuOf(SearchResultEntry e) => UserStatus.ParentDn(e.DistinguishedName);

    private static string[] UserAttrs(DirectoryReadOptions o) =>
    [
        "objectGUID", "sAMAccountName", "userPrincipalName", "displayName", "givenName", "sn", "mail",
        LdapText.IsSafeAttributeName(o.EmployeeIdAttribute) ? o.EmployeeIdAttribute : "employeeID",
        "title", "department", "physicalDeliveryOfficeName", "telephoneNumber", "mobile", "description", "manager",
        "userAccountControl", "msDS-User-Account-Control-Computed", "accountExpires", "pwdLastSet",
        "msDS-UserPasswordExpiryTimeComputed", "msDS-ResultantPSO", "lastLogonTimestamp", "whenCreated", "whenChanged",
        "primaryGroupID", "adminCount",
    ];

    private static string[] ComputerAttrs(DirectoryReadOptions o)
    {
        var list = new List<string>
        {
            "objectGUID", "cn", "dNSHostName", "userAccountControl", "operatingSystem", "operatingSystemVersion", "lastLogonTimestamp",
            "pwdLastSet", "managedBy", "description", "whenChanged", "whenCreated", "adminCount",
        };
        if (LdapText.IsSafeAttributeName(o.ComputerLastUserAttribute) && !list.Contains(o.ComputerLastUserAttribute!, StringComparer.OrdinalIgnoreCase))
            list.Add(o.ComputerLastUserAttribute!);
        return [.. list];
    }

    private static readonly string[] GroupAttrs = ["objectGUID", "cn", "description", "groupType", "managedBy", "adminCount", "distinguishedName", "objectClass"];

    private DirectoryUser MapUser(LdapConnection c, SearchResultEntry e, DirectoryReadOptions o, bool resolveManager)
    {
        var now = DateTime.UtcNow;
        var uac = I(e, "userAccountControl");
        var computed = I(e, "msDS-User-Account-Control-Computed");
        var pwdSet = L(e, "pwdLastSet");
        long? expiryComputed = e.Attributes.Contains("msDS-UserPasswordExpiryTimeComputed") ? L(e, "msDS-UserPasswordExpiryTimeComputed") : null;
        var (aeKind, aeDate) = UserStatus.AccountExpiry(L(e, "accountExpires"), now);
        var (pwStatus, pwDate) = UserStatus.Password(uac, computed, pwdSet, expiryComputed, now);
        var emp = LdapText.IsSafeAttributeName(o.EmployeeIdAttribute) ? o.EmployeeIdAttribute : "employeeID";
        var pso = S(e, "msDS-ResultantPSO");
        var mgrDn = S(e, "manager");

        return new DirectoryUser
        {
            Id = GuidOf(e), Dn = e.DistinguishedName, Ou = OuOf(e), SamAccountName = S(e, "sAMAccountName") ?? "",
            UserPrincipalName = S(e, "userPrincipalName"), DisplayName = S(e, "displayName"), GivenName = S(e, "givenName"),
            Surname = S(e, "sn"), Email = S(e, "mail"), EmployeeId = S(e, emp), Title = S(e, "title"), Department = S(e, "department"),
            Office = S(e, "physicalDeliveryOfficeName"), Phone = S(e, "telephoneNumber"), Mobile = S(e, "mobile"), Description = S(e, "description"),
            Manager = resolveManager && mgrDn != null ? ResolveRef(c, mgrDn) : null,
            Enabled = UserStatus.IsEnabled(uac), LockedOut = UserStatus.IsLockedOut(computed),
            AccountExpiry = aeKind, AccountExpiresUtc = aeDate, PasswordStatus = pwStatus, PasswordExpiresUtc = pwDate,
            PasswordLastSetUtc = FileTime(e, "pwdLastSet"), LastLogonUtc = FileTime(e, "lastLogonTimestamp"),
            CreatedUtc = GeneralizedTime(e, "whenCreated"), ChangedUtc = GeneralizedTime(e, "whenChanged"),
            ResultantPso = pso == null ? null : ResolveCn(c, pso),
            UserAccountControl = uac | (UserStatus.IsLockedOut(computed) ? UserStatus.Lockout : 0),
            UacFlags = UserStatus.DescribeFlags(uac | (UserStatus.IsLockedOut(computed) ? UserStatus.Lockout : 0) | (computed & UserStatus.ComputedPasswordExpired)),
            PrimaryGroupId = I(e, "primaryGroupID"), AdminCount = L(e, "adminCount") > 0,
        };
    }

    private DirectoryComputer MapComputer(LdapConnection c, SearchResultEntry e, DirectoryReadOptions o, bool resolveManagedBy)
    {
        var mb = S(e, "managedBy");
        var lastUser = LdapText.IsSafeAttributeName(o.ComputerLastUserAttribute) ? S(e, o.ComputerLastUserAttribute!) : null;
        return new DirectoryComputer
        {
            Id = GuidOf(e), Dn = e.DistinguishedName, Ou = OuOf(e), Name = S(e, "cn") ?? "", DnsHostName = S(e, "dNSHostName"),
            Enabled = UserStatus.IsEnabled(I(e, "userAccountControl")), OperatingSystem = S(e, "operatingSystem"), OsVersion = S(e, "operatingSystemVersion"),
            LastLogonUtc = FileTime(e, "lastLogonTimestamp"), PasswordLastSetUtc = FileTime(e, "pwdLastSet"),
            ManagedBy = resolveManagedBy && mb != null ? ResolveRef(c, mb) : null,
            Description = S(e, "description"), ChangedUtc = GeneralizedTime(e, "whenChanged"), CreatedUtc = GeneralizedTime(e, "whenCreated"),
            LastLoggedInUser = lastUser, AdminCount = L(e, "adminCount") > 0,
        };
    }

    private static DirectoryGroup MapGroup(SearchResultEntry e, int? memberCount = null)
    {
        var gt = I(e, "groupType");
        var scope = (gt & 0x2) != 0 ? "Global" : (gt & 0x4) != 0 ? "DomainLocal" : (gt & 0x8) != 0 ? "Universal" : "Global";
        return new DirectoryGroup
        {
            Id = GuidOf(e), Dn = e.DistinguishedName, Ou = OuOf(e), Name = S(e, "cn") ?? "", Description = S(e, "description"),
            Scope = scope, Type = (gt & unchecked((int)0x80000000)) != 0 ? "Security" : "Distribution",
            AdminCount = L(e, "adminCount") > 0, MemberCount = memberCount,
        };
    }

    private DirectoryGroup WithManager(LdapConnection c, DirectoryGroup g, SearchResultEntry e)
    {
        var mb = S(e, "managedBy");
        if (mb == null) return g;
        return new DirectoryGroup
        {
            Id = g.Id, Dn = g.Dn, Ou = g.Ou, Name = g.Name, Description = g.Description, Scope = g.Scope, Type = g.Type,
            AdminCount = g.AdminCount, MemberCount = g.MemberCount, ManagedBy = ResolveRef(c, mb),
        };
    }

    // ------------------------------------------------------------------ low-level search

    private static SearchResultEntry? ReadOne(LdapConnection c, string baseDn, string filter, string[] attrs, SearchScope scope = SearchScope.Subtree)
    {
        var req = new SearchRequest(baseDn, filter, scope, attrs) { SizeLimit = 1 };
        try
        {
            var resp = (SearchResponse)c.SendRequest(req);
            return resp.Entries.Count > 0 ? resp.Entries[0] : null;
        }
        catch (DirectoryOperationException ex) when (ex.Response.ResultCode is ResultCode.NoSuchObject or ResultCode.SizeLimitExceeded)
        {
            return null;
        }
    }

    private SearchResultEntry? ReadByGuid(LdapConnection c, Guid id, string[] attrs, string? categoryFilter = null) =>
        ReadOne(c, BaseDn, categoryFilter == null ? LdapFilters.ByGuid(id) : $"(&{LdapFilters.ByGuid(id)}{categoryFilter})", attrs);

    private static SearchResultEntry? ReadByDn(LdapConnection c, string dn, string[] attrs) =>
        LdapText.IsValidDn(dn) ? ReadOne(c, dn, "(objectClass=*)", attrs, SearchScope.Base) : null;

    private static string? ResolveCn(LdapConnection c, string dn) => ReadByDn(c, dn, ["cn"]) is { } e ? S(e, "cn") : null;

    private ObjectRef? ResolveRef(LdapConnection c, string dn)
    {
        var e = ReadByDn(c, dn, ["objectGUID", "displayName", "cn", "objectClass", "objectCategory"]);
        if (e == null) return null;
        var classes = Values(e, "objectClass");
        var kind = classes.Contains("computer", StringComparer.OrdinalIgnoreCase) ? DirectoryObjectKind.Computer
            : classes.Contains("group", StringComparer.OrdinalIgnoreCase) ? DirectoryObjectKind.Group : DirectoryObjectKind.User;
        return new ObjectRef(GuidOf(e), S(e, "displayName") ?? S(e, "cn") ?? dn, kind);
    }

    /// <summary>
    /// True server-side paging: sort control plus virtual list view, so page N of a large result is fetched without
    /// reading the pages before it. Falls back to scanning up to the limit if the server has no VLV support.
    /// </summary>
    private (List<SearchResultEntry> Items, int Total, bool Capped) SearchPage(
        LdapConnection c, string baseDn, string filter, string[] attrs, int page, int pageSize, int limit, Func<SearchResultEntry, bool>? postFilter = null)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxVlvPage);
        page = Math.Max(1, page);
        var offset = (page - 1) * pageSize + 1;

        if (postFilter == null)
        {
            try
            {
                var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, attrs);
                req.Controls.Add(new SortRequestControl("cn", false) { IsCritical = true });
                req.Controls.Add(new VlvRequestControl(0, pageSize - 1, offset) { IsCritical = true });
                var resp = (SearchResponse)c.SendRequest(req);
                var vlv = resp.Controls.OfType<VlvResponseControl>().FirstOrDefault();
                var total = vlv?.ContentCount ?? resp.Entries.Count;
                var capped = total > limit;
                var items = offset > limit ? [] : resp.Entries.Cast<SearchResultEntry>().Take(Math.Max(0, limit - offset + 1)).ToList();
                return (items, Math.Min(total, limit), capped);
            }
            catch (DirectoryOperationException ex) when (ex.Response.ResultCode is ResultCode.UnavailableCriticalExtension or ResultCode.UnwillingToPerform or ResultCode.SortControlMissing or ResultCode.OffsetRangeError)
            {
                log.LogWarning("Server-side paging (VLV) unavailable ({Code}); scanning instead", ex.Response.ResultCode);
            }
        }

        var all = Scan(c, baseDn, filter, attrs, limit + 1, postFilter);
        var cap = all.Count > limit;
        var ordered = all.Take(limit).OrderBy(e => S(e, "cn") ?? e.DistinguishedName, StringComparer.OrdinalIgnoreCase).ToList();
        return (ordered.Skip(offset - 1).Take(pageSize).ToList(), ordered.Count, cap);
    }

    /// <summary>Paged-results scan (page size 500) that stops after <paramref name="max"/> matches.</summary>
    private static List<SearchResultEntry> Scan(LdapConnection c, string baseDn, string filter, string[] attrs, int max, Func<SearchResultEntry, bool>? postFilter = null)
    {
        var found = new List<SearchResultEntry>();
        var paging = new PageResultRequestControl(500);
        while (true)
        {
            var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, attrs);
            req.Controls.Add(paging);
            var resp = (SearchResponse)c.SendRequest(req);
            foreach (SearchResultEntry e in resp.Entries)
            {
                if (postFilter != null && !postFilter(e)) continue;
                found.Add(e);
                if (found.Count >= max) return found;
            }
            var cookie = resp.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
            if (cookie == null || cookie.Length == 0) return found;
            paging.Cookie = cookie;
        }
    }

    private static long Count(LdapConnection c, string baseDn, string filter, string[] attrs, Func<SearchResultEntry, bool>? postFilter = null)
    {
        long n = 0;
        var paging = new PageResultRequestControl(1000);
        while (true)
        {
            var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, attrs);
            req.Controls.Add(paging);
            var resp = (SearchResponse)c.SendRequest(req);
            foreach (SearchResultEntry e in resp.Entries) if (postFilter == null || postFilter(e)) n++;
            var cookie = resp.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
            if (cookie == null || cookie.Length == 0) return n;
            paging.Cookie = cookie;
        }
    }

    private string SearchRoot(string? ouDn) => !string.IsNullOrEmpty(ouDn) && LdapText.IsValidDn(ouDn) ? ouDn : BaseDn;

    // ------------------------------------------------------------------ reads

    public Task<PagedResult<DirectoryUser>> SearchUsersAsync(UserSearch s, CancellationToken ct = default) => Run(c =>
    {
        var filter = LdapFilters.Users(s.Text, s.Filter, s.Options.EmployeeIdAttribute, DateTime.UtcNow);
        var attrs = UserAttrs(s.Options);
        // A lockoutTime alone does not prove the account is still locked, so confirm with the computed bit.
        Func<SearchResultEntry, bool>? post = s.Filter == UserFilter.Locked ? e => UserStatus.IsLockedOut(I(e, "msDS-User-Account-Control-Computed")) : null;
        var (items, total, capped) = SearchPage(c, SearchRoot(s.OuDn), filter, attrs, s.Page, s.PageSize, s.Limit, post);
        return new PagedResult<DirectoryUser>
        {
            Items = items.Select(e => MapUser(c, e, s.Options, resolveManager: false)).ToList(),
            Total = total, Page = s.Page, PageSize = s.PageSize, TotalIsCapped = capped,
        };
    }, ct);

    public Task<DirectoryUser?> GetUserAsync(Guid id, DirectoryReadOptions o, CancellationToken ct = default) => Run(c =>
        ReadByGuid(c, id, UserAttrs(o), LdapFilters.UserBase) is { } e ? MapUser(c, e, o, resolveManager: true) : null, ct);

    public Task<PagedResult<DirectoryComputer>> SearchComputersAsync(ComputerSearch s, CancellationToken ct = default) => Run(c =>
    {
        var (items, total, capped) = SearchPage(c, SearchRoot(s.OuDn), LdapFilters.Computers(s.Text, s.Filter), ComputerAttrs(s.Options), s.Page, s.PageSize, s.Limit);
        return new PagedResult<DirectoryComputer>
        {
            Items = items.Select(e => MapComputer(c, e, s.Options, resolveManagedBy: false)).ToList(),
            Total = total, Page = s.Page, PageSize = s.PageSize, TotalIsCapped = capped,
        };
    }, ct);

    public Task<DirectoryComputer?> GetComputerAsync(Guid id, DirectoryReadOptions o, CancellationToken ct = default) => Run(c =>
        ReadByGuid(c, id, ComputerAttrs(o), LdapFilters.ComputerBase) is { } e ? MapComputer(c, e, o, resolveManagedBy: true) : null, ct);

    public Task<PagedResult<DirectoryGroup>> SearchGroupsAsync(GroupSearch s, CancellationToken ct = default) => Run(c =>
    {
        var (items, total, capped) = SearchPage(c, BaseDn, LdapFilters.Groups(s.Text), GroupAttrs, s.Page, s.PageSize, s.Limit);
        return new PagedResult<DirectoryGroup>
        {
            Items = items.Select(e => MapGroup(e)).ToList(), Total = total, Page = s.Page, PageSize = s.PageSize, TotalIsCapped = capped,
        };
    }, ct);

    public Task<DirectoryGroup?> GetGroupAsync(Guid id, CancellationToken ct = default) => Run(c =>
    {
        var e = ReadByGuid(c, id, GroupAttrs, LdapFilters.GroupBase);
        if (e == null) return null;
        // The member count comes from the directory's own count for a one-row VLV query, not from reading members.
        var (_, total, _) = SearchPage(c, BaseDn, LdapFilters.Members(e.DistinguishedName, null, null), ["cn"], 1, 1, int.MaxValue);
        return WithManager(c, MapGroup(e, total), e);
    }, ct);

    public Task<DirectoryGroup?> GetGroupByDnAsync(string dn, CancellationToken ct = default) => Run(c =>
        ReadByDn(c, dn, GroupAttrs) is { } e && Values(e, "objectClass").Contains("group", StringComparer.OrdinalIgnoreCase) ? MapGroup(e) : null, ct);

    public Task<Memberships?> GetMembershipsAsync(Guid objectId, DirectoryObjectKind kind, CancellationToken ct = default) => Run(c =>
    {
        var target = ReadByGuid(c, objectId, ["distinguishedName", "memberOf", "primaryGroupID"]);
        if (target == null) return (Memberships?)null;

        var direct = Values(target, "memberOf")
            .Select(dn => ReadByDn(c, dn, GroupAttrs)).Where(e => e != null).Select(e => MapGroup(e!)).OrderBy(g => g.Name).ToList();
        var directDns = direct.Select(g => g.Dn).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Nested: every group reached through the chain; "via" is the first direct group that leads to it.
        var nested = new Dictionary<string, NestedMembership>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in direct.Take(50))
        {
            var req = new SearchRequest(BaseDn, LdapFilters.NestedGroupsOf(d.Dn), SearchScope.Subtree, GroupAttrs);
            foreach (SearchResultEntry e in ((SearchResponse)c.SendRequest(req)).Entries)
                if (!directDns.Contains(e.DistinguishedName) && !nested.ContainsKey(e.DistinguishedName))
                    nested[e.DistinguishedName] = new NestedMembership(MapGroup(e), d.Name);
        }

        return new Memberships { Direct = direct, Nested = nested.Values.OrderBy(n => n.Group.Name).ToList(), Primary = PrimaryGroup(c, I(target, "primaryGroupID")) };
    }, ct);

    private DirectoryGroup? PrimaryGroup(LdapConnection c, int rid)
    {
        if (rid <= 0) return null;
        var root = ReadOne(c, BaseDn, "(objectClass=domain)", ["objectSid"], SearchScope.Base);
        if (root == null || !root.Attributes.Contains("objectSid") || root.Attributes["objectSid"][0] is not byte[] sidBytes) return null;
        var groupSid = new SecurityIdentifier($"{new SecurityIdentifier(sidBytes, 0)}-{rid}");
        var bytes = new byte[groupSid.BinaryLength];
        groupSid.GetBinaryForm(bytes, 0);
        var e = ReadOne(c, BaseDn, $"(&{LdapFilters.GroupBase}(objectSid={LdapText.EscapeBytes(bytes)}))", GroupAttrs);
        return e == null ? null : MapGroup(e);
    }

    public Task<PagedResult<GroupMember>> SearchGroupMembersAsync(Guid groupId, MemberSearch s, CancellationToken ct = default) => Run(c =>
    {
        var group = ReadByGuid(c, groupId, ["distinguishedName"]);
        if (group == null) return new PagedResult<GroupMember> { PageSize = s.PageSize };

        // The directory filters by group, type and text and returns only the requested page.
        var attrs = new[] { "objectGUID", "cn", "sAMAccountName", "mail", "displayName", "objectClass", "userAccountControl" };
        var (items, total, _) = SearchPage(c, BaseDn, LdapFilters.Members(group.DistinguishedName, s.Text, s.Kind), attrs, s.Page, s.PageSize, int.MaxValue);
        return new PagedResult<GroupMember>
        {
            Items = items.Select(e =>
            {
                var classes = Values(e, "objectClass");
                var kind = classes.Contains("computer", StringComparer.OrdinalIgnoreCase) ? MemberKind.Computer
                    : classes.Contains("group", StringComparer.OrdinalIgnoreCase) ? MemberKind.Group : MemberKind.User;
                return new GroupMember
                {
                    Id = GuidOf(e), Kind = kind, Name = S(e, "displayName") ?? S(e, "cn") ?? "", SamAccountName = S(e, "sAMAccountName"),
                    Email = S(e, "mail"), Dn = e.DistinguishedName,
                    Enabled = kind == MemberKind.Group ? null : UserStatus.IsEnabled(I(e, "userAccountControl")),
                };
            }).ToList(),
            Total = total, Page = s.Page, PageSize = s.PageSize,
        };
    }, ct);

    public Task<IReadOnlyList<DirectoryOu>> BrowseOusAsync(string? parentDn, CancellationToken ct = default) => Run(c =>
    {
        var parent = string.IsNullOrWhiteSpace(parentDn) ? BaseDn : parentDn;
        if (!LdapText.IsValidDn(parent)) return (IReadOnlyList<DirectoryOu>)[];
        var req = new SearchRequest(parent, "(objectClass=organizationalUnit)", SearchScope.OneLevel, "ou", "distinguishedName");
        var resp = (SearchResponse)c.SendRequest(req);
        // HasChildren is assumed true: finding out would cost one extra query per OU. An empty expansion is harmless.
        return resp.Entries.Cast<SearchResultEntry>().OrderBy(e => S(e, "ou"), StringComparer.OrdinalIgnoreCase)
            .Select(e => new DirectoryOu(e.DistinguishedName, S(e, "ou") ?? e.DistinguishedName, true)).ToList();
    }, ct);

    public Task<IReadOnlyList<DirectoryOu>> SearchOusAsync(string text, int limit, CancellationToken ct = default) => Run(c =>
    {
        var req = new SearchRequest(BaseDn, $"(&(objectClass=organizationalUnit)(ou=*{LdapText.EscapeFilterValue(text)}*))", SearchScope.Subtree, "ou", "distinguishedName") { SizeLimit = limit };
        try
        {
            var resp = (SearchResponse)c.SendRequest(req);
            return (IReadOnlyList<DirectoryOu>)resp.Entries.Cast<SearchResultEntry>().Select(e => new DirectoryOu(e.DistinguishedName, S(e, "ou") ?? "", true)).ToList();
        }
        catch (DirectoryOperationException ex) when (ex.Response.ResultCode == ResultCode.SizeLimitExceeded)
        {
            return [];
        }
    }, ct);

    public Task<DirectoryOu?> GetOuAsync(string dn, CancellationToken ct = default) => Run(c =>
        ReadByDn(c, dn, ["ou", "objectClass"]) is { } e && Values(e, "objectClass").Contains("organizationalUnit", StringComparer.OrdinalIgnoreCase)
            ? new DirectoryOu(e.DistinguishedName, S(e, "ou") ?? "", true) : null, ct);

    public Task<long> CountUsersAsync(UserFilter filter, CancellationToken ct = default) => Run(c =>
        Count(c, BaseDn, LdapFilters.Users(null, filter, "employeeID", DateTime.UtcNow), ["msDS-User-Account-Control-Computed"],
            filter == UserFilter.Locked ? e => UserStatus.IsLockedOut(I(e, "msDS-User-Account-Control-Computed")) : null), ct);

    public Task<long> CountComputersAsync(ComputerFilter filter, CancellationToken ct = default) => Run(c =>
        Count(c, BaseDn, LdapFilters.Computers(null, filter), ["cn"]), ct);

    // ------------------------------------------------------------------ writes

    private static readonly string[] RightsAttrs = ["distinguishedName", "objectGUID", "userAccountControl", "allowedAttributesEffective", "cn"];

    private static bool CanWrite(SearchResultEntry e, string attribute) =>
        Values(e, "allowedAttributesEffective").Contains(attribute, StringComparer.OrdinalIgnoreCase);

    /// <summary>Common dry-run front half: find the target by objectGUID and check we may write the attribute.</summary>
    private DirectoryResult? Preflight(LdapConnection c, Guid id, bool dryRun, string attribute, out SearchResultEntry? entry, out List<DryRunCheck> checks)
    {
        checks = [];
        entry = ReadByGuid(c, id, RightsAttrs);
        if (entry == null)
        {
            checks.Add(new DryRunCheck("Target object found by objectGUID", false, "No object with that objectGUID"));
            return DirectoryResult.Fail(dryRun, DirectoryErrors.NotFound, "The object no longer exists in the directory.", checks);
        }
        checks.Add(new DryRunCheck("Target object found by objectGUID", true));
        var can = CanWrite(entry, attribute);
        checks.Add(new DryRunCheck($"Service account may write '{attribute}' on the target (allowedAttributesEffective)", can,
            can ? null : "The service account has no write access to this attribute"));
        return can ? null : DirectoryResult.Fail(dryRun, DirectoryErrors.PermissionDenied, "The service account does not have permission to make this change.", checks);
    }

    private static DirectoryResult Map(DirectoryOperationException ex, bool passwordChange = false)
    {
        var code = ex.Response.ResultCode;
        return code switch
        {
            ResultCode.InsufficientAccessRights => DirectoryResult.Fail(false, DirectoryErrors.PermissionDenied, "The service account does not have permission to make this change."),
            ResultCode.NoSuchObject => DirectoryResult.Fail(false, DirectoryErrors.NotFound, "The object no longer exists in the directory."),
            ResultCode.EntryAlreadyExists or ResultCode.AttributeOrValueExists => DirectoryResult.Fail(false, DirectoryErrors.AlreadyMember, "Already a member."),
            ResultCode.NoSuchAttribute => DirectoryResult.Fail(false, DirectoryErrors.NotMember, "Not a direct member."),
            ResultCode.ConstraintViolation or ResultCode.UnwillingToPerform when passwordChange =>
                DirectoryResult.Fail(false, DirectoryErrors.PasswordRejected, "The domain rejected the password (length, complexity, history or minimum age)."),
            _ => DirectoryResult.Fail(false, DirectoryErrors.Constraint, $"The directory refused the change ({code})."),
        };
    }

    public Task<DirectoryResult> ResetPasswordAsync(Guid userId, SecureString newPassword, ResetPasswordOptions options, bool dryRun, CancellationToken ct = default) => Run(c =>
    {
        if (Preflight(c, userId, dryRun, "unicodePwd", out var e, out var checks) is { } failed) return failed;
        var changes = new List<DirectoryChange> { new("Password", null, "Reset (value not shown)") };
        if (options.MustChangeAtNextSignIn) changes.Add(new("Must change password at next sign-in", null, "Yes"));
        if (options.UnlockAccount) changes.Add(new("Locked out", null, "No"));
        if (dryRun) return DirectoryResult.Ok(true, changes, checks); // no password is ever sent in a dry run

        var ptr = Marshal.SecureStringToGlobalAllocUnicode(newPassword);
        byte[]? quoted = null;
        try
        {
            var plain = Marshal.PtrToStringUni(ptr) ?? "";
            quoted = Encoding.Unicode.GetBytes("\"" + plain + "\"");
            var req = new ModifyRequest(e!.DistinguishedName);
            req.Modifications.Add(new DirectoryAttributeModification { Name = "unicodePwd", Operation = DirectoryAttributeOperation.Replace, }.With(quoted));
            if (options.MustChangeAtNextSignIn) req.Modifications.Add(new DirectoryAttributeModification { Name = "pwdLastSet", Operation = DirectoryAttributeOperation.Replace }.With("0"));
            if (options.UnlockAccount) req.Modifications.Add(new DirectoryAttributeModification { Name = "lockoutTime", Operation = DirectoryAttributeOperation.Replace }.With("0"));
            c.SendRequest(req);
            return DirectoryResult.Ok(false, changes);
        }
        catch (DirectoryOperationException ex) { return Map(ex, passwordChange: true); }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(ptr);
            if (quoted != null) Array.Clear(quoted);
        }
    }, ct);

    public Task<DirectoryResult> UnlockAsync(Guid userId, bool dryRun, CancellationToken ct = default) => Run(c =>
    {
        if (Preflight(c, userId, dryRun, "lockoutTime", out var e, out var checks) is { } failed) return failed;
        var changes = new[] { new DirectoryChange("Locked out", null, "No") };
        if (dryRun) return DirectoryResult.Ok(true, changes, checks);
        try
        {
            c.SendRequest(new ModifyRequest(e!.DistinguishedName, DirectoryAttributeOperation.Replace, "lockoutTime", "0"));
            return DirectoryResult.Ok(false, changes);
        }
        catch (DirectoryOperationException ex) { return Map(ex); }
    }, ct);

    public Task<DirectoryResult> SetEnabledAsync(Guid objectId, DirectoryObjectKind kind, bool enabled, bool dryRun, CancellationToken ct = default) => Run(c =>
    {
        if (kind == DirectoryObjectKind.Group) return DirectoryResult.Fail(dryRun, DirectoryErrors.Constraint, "Groups cannot be enabled or disabled.");
        if (Preflight(c, objectId, dryRun, "userAccountControl", out var e, out var checks) is { } failed) return failed;
        var uac = I(e!, "userAccountControl");
        var wasEnabled = UserStatus.IsEnabled(uac);
        var changes = new[] { new DirectoryChange("Enabled", wasEnabled ? "Yes" : "No", enabled ? "Yes" : "No") };
        if (dryRun || wasEnabled == enabled) return DirectoryResult.Ok(dryRun, changes, checks);
        var next = enabled ? uac & ~UserStatus.AccountDisable : uac | UserStatus.AccountDisable;
        try
        {
            c.SendRequest(new ModifyRequest(e!.DistinguishedName, DirectoryAttributeOperation.Replace, "userAccountControl", next.ToString(CultureInfo.InvariantCulture)));
            return DirectoryResult.Ok(false, changes);
        }
        catch (DirectoryOperationException ex) { return Map(ex); }
    }, ct);

    public Task<DirectoryResult> MoveAsync(Guid objectId, DirectoryObjectKind kind, string targetOuDn, bool dryRun, CancellationToken ct = default) => Run(c =>
    {
        if (kind == DirectoryObjectKind.Group) return DirectoryResult.Fail(dryRun, DirectoryErrors.Constraint, "Groups cannot be moved in Version 1.");
        var entry = ReadByGuid(c, objectId, ["distinguishedName", "objectGUID"]);
        var checks = new List<DryRunCheck> { new("Target object found by objectGUID", entry != null) };
        if (entry == null) return DirectoryResult.Fail(dryRun, DirectoryErrors.NotFound, "The object no longer exists in the directory.", checks);

        if (!LdapText.IsValidDn(targetOuDn)) return DirectoryResult.Fail(dryRun, DirectoryErrors.Constraint, "The target OU is not a valid distinguished name.", checks);
        var target = ReadOne(c, targetOuDn, "(objectClass=organizationalUnit)", ["distinguishedName", "allowedChildClassesEffective"], SearchScope.Base);
        checks.Add(new DryRunCheck("Target OU exists", target != null));
        if (target == null) return DirectoryResult.Fail(dryRun, DirectoryErrors.NotFound, "The target OU does not exist.", checks);

        var cls = kind == DirectoryObjectKind.User ? "user" : "computer";
        var canCreate = Values(target, "allowedChildClassesEffective").Contains(cls, StringComparer.OrdinalIgnoreCase);
        checks.Add(new DryRunCheck($"Service account may create '{cls}' objects in the target OU (allowedChildClassesEffective)", canCreate));
        if (!canCreate) return DirectoryResult.Fail(dryRun, DirectoryErrors.PermissionDenied, "The service account cannot create this object type in the target OU.", checks);

        var changes = new[] { new DirectoryChange("OU", UserStatus.ParentDn(entry.DistinguishedName), target.DistinguishedName) };
        if (dryRun) return DirectoryResult.Ok(true, changes, checks);
        try
        {
            var rdn = DnText.SplitRdns(entry.DistinguishedName)[0];
            c.SendRequest(new ModifyDNRequest(entry.DistinguishedName, target.DistinguishedName, rdn));
            return DirectoryResult.Ok(false, changes);
        }
        catch (DirectoryOperationException ex) { return Map(ex); }
    }, ct);

    private Task<DirectoryResult> ChangeMembership(Guid memberId, IReadOnlyList<Guid> groupIds, bool add, bool dryRun, CancellationToken ct) => Run(c =>
    {
        var member = ReadByGuid(c, memberId, ["distinguishedName", "memberOf"]);
        var checks = new List<DryRunCheck> { new("Target object found by objectGUID", member != null) };
        if (member == null) return DirectoryResult.Fail(dryRun, DirectoryErrors.NotFound, "The object no longer exists in the directory.", checks);

        var memberOf = Values(member, "memberOf");
        var groups = new List<SearchResultEntry>();
        var changes = new List<DirectoryChange>();
        foreach (var gid in groupIds)
        {
            var g = ReadByGuid(c, gid, ["distinguishedName", "cn", "allowedAttributesEffective"]);
            if (g == null)
            {
                checks.Add(new DryRunCheck("Group found by objectGUID", false));
                return DirectoryResult.Fail(dryRun, DirectoryErrors.NotFound, "The group no longer exists.", checks);
            }
            var name = S(g, "cn") ?? g.DistinguishedName;
            checks.Add(new DryRunCheck($"Group '{name}' found", true));
            var isMember = memberOf.Contains(g.DistinguishedName, StringComparer.OrdinalIgnoreCase);
            if (add && isMember) return DirectoryResult.Fail(dryRun, DirectoryErrors.AlreadyMember, $"Already a member of {name}.", checks);
            if (!add && !isMember) return DirectoryResult.Fail(dryRun, DirectoryErrors.NotMember, $"Not a direct member of {name}.", checks);
            var can = CanWrite(g, "member");
            checks.Add(new DryRunCheck($"Service account may modify 'member' on '{name}' (allowedAttributesEffective)", can));
            if (!can) return DirectoryResult.Fail(dryRun, DirectoryErrors.PermissionDenied, "The service account cannot change this group.", checks);
            groups.Add(g);
            changes.Add(new DirectoryChange("Group membership: " + name, isMember ? "Member" : "Not a member", add ? "Member" : "Not a member"));
        }
        if (dryRun) return DirectoryResult.Ok(true, changes, checks);

        try
        {
            foreach (var g in groups)
                c.SendRequest(new ModifyRequest(g.DistinguishedName, add ? DirectoryAttributeOperation.Add : DirectoryAttributeOperation.Delete, "member", member.DistinguishedName));
            return DirectoryResult.Ok(false, changes);
        }
        catch (DirectoryOperationException ex) { return Map(ex); }
    }, ct);

    public Task<DirectoryResult> AddToGroupsAsync(Guid memberId, IReadOnlyList<Guid> groupIds, bool dryRun, CancellationToken ct = default) =>
        ChangeMembership(memberId, groupIds, add: true, dryRun, ct);

    public Task<DirectoryResult> RemoveFromGroupsAsync(Guid memberId, IReadOnlyList<Guid> groupIds, bool dryRun, CancellationToken ct = default) =>
        ChangeMembership(memberId, groupIds, add: false, dryRun, ct);

    // ------------------------------------------------------------------ connection test

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        var steps = new List<ConnectionStep>();
        try
        {
            await Task.Run(() =>
            {
                using var c = Connect();
                steps.Add(new ConnectionStep("Bind", true, $"Bound to {_o.Server}:{_o.Port} as " + (string.IsNullOrEmpty(_o.BindUsername) ? "the application identity" : _o.BindUsername + " (local test credentials)")));

                var root = ReadOne(c, BaseDn, "(objectClass=*)", ["distinguishedName"], SearchScope.Base);
                steps.Add(new ConnectionStep("Search base", root != null, root != null ? $"Found {BaseDn}" : $"{BaseDn} was not found"));

                var secure = _o.UseLdaps && _o.VerifyCertificate;
                steps.Add(new ConnectionStep("Secure connection", secure,
                    secure ? "LDAPS with certificate validation" : "LDAPS with certificate validation is required (UseLdaps and VerifyCertificate must be true)"));

                var sample = ReadOne(c, BaseDn, LdapFilters.UserBase, ["sAMAccountName"]);
                steps.Add(new ConnectionStep("Sample search", sample != null, sample != null ? "A user search returned a result" : "A user search returned nothing"));
            }, ct);
        }
        catch (LdapException ex)
        {
            steps.Add(new ConnectionStep("Bind", false, $"LDAP error {ex.ErrorCode}"));
        }
        catch (Exception ex) when (ex is DirectoryOperationException or DirectoryUnavailableException)
        {
            steps.Add(new ConnectionStep("Search base", false, "The directory refused the request"));
        }
        return new ConnectionTestResult(steps.Count > 0 && steps.All(s => s.Passed), steps);
    }
}

internal static class DirectoryAttributeModificationExtensions
{
    public static DirectoryAttributeModification With(this DirectoryAttributeModification m, byte[] value)
    {
        m.Add(value);
        return m;
    }

    public static DirectoryAttributeModification With(this DirectoryAttributeModification m, string value)
    {
        m.Add(value);
        return m;
    }
}
