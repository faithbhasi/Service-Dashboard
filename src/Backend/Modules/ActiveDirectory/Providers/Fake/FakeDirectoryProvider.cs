using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Extensions.Options;
using ServiceDashboard.Configuration;

namespace ServiceDashboard.Modules.ActiveDirectory.Providers.Fake;

/// <summary>Switches for the error simulations the Fake provider supports.</summary>
public sealed class FakeSimulation
{
    /// <summary>Every call throws DirectoryUnavailableException, as if the domain controller were down.</summary>
    public volatile bool ServerUnavailable;
}

/// <summary>
/// In-memory Active Directory for local development and tests. It implements the same interface as the LDAP provider,
/// including dry runs, and shares the account-status logic with it. Nothing is persisted: restart to reset.
/// </summary>
public sealed class FakeDirectoryProvider : IDirectoryProvider
{
    private readonly FakeDirectoryData _d = FakeDirectoryData.Seed();
    private readonly object _lock = new();

    public FakeSimulation Simulation { get; } = new();
    public string ProviderName => "Fake";
    public string BaseDn => FakeDirectoryData.Base;

    public SuggestedAllowlists? SuggestedDefaults => new(
        UserOus: [FakeDirectoryData.Staff, FakeDirectoryData.Contractors],
        ComputerOus: [FakeDirectoryData.Workstations, FakeDirectoryData.Laptops],
        ManageableGroups: _d.Groups.Where(g => g.Ou == FakeDirectoryData.GroupsOu && !g.AdminCount).Select(g => g.Dn).ToList(),
        ProtectedGroups: []);

    public FakeDirectoryProvider(IOptions<ActiveDirectoryOptions> options) =>
        Simulation.ServerUnavailable = options.Value.SimulateServerUnavailable;

    private void Guard()
    {
        if (Simulation.ServerUnavailable) throw new DirectoryUnavailableException("Simulated: domain controller unreachable");
    }

    // ------------------------------------------------------------------ mapping

    private ObjectRef? Ref(Guid? id)
    {
        if (id == null) return null;
        var u = _d.Users.FirstOrDefault(x => x.Id == id);
        if (u != null) return new ObjectRef(u.Id, u.Display ?? u.Sam, DirectoryObjectKind.User);
        var g = _d.Groups.FirstOrDefault(x => x.Id == id);
        if (g != null) return new ObjectRef(g.Id, g.Name, DirectoryObjectKind.Group);
        var c = _d.Computers.FirstOrDefault(x => x.Id == id);
        return c == null ? null : new ObjectRef(c.Id, c.Name, DirectoryObjectKind.Computer);
    }

    private DirectoryUser ToUser(FakeUser u, DirectoryReadOptions o)
    {
        var now = DateTime.UtcNow;
        var maxAge = TimeSpan.FromDays(u.PsoMaxAgeDays > 0 ? u.PsoMaxAgeDays : 90);
        long expiry = (u.Uac & UserStatus.DontExpirePassword) != 0 ? UserStatus.NeverFileTime
            : u.PwdLastSet == 0 ? 0
            : DateTime.FromFileTimeUtc(u.PwdLastSet).Add(maxAge).ToFileTimeUtc();
        var computed = (u.LockedOut ? UserStatus.ComputedLockout : 0)
            | (expiry is > 0 and < UserStatus.NeverFileTime && DateTime.FromFileTimeUtc(expiry) <= now && (u.Uac & UserStatus.DontExpirePassword) == 0 ? UserStatus.ComputedPasswordExpired : 0);
        var (aeKind, aeDate) = UserStatus.AccountExpiry(u.AccountExpires, now);
        var (pwStatus, pwDate) = UserStatus.Password(u.Uac, computed, u.PwdLastSet, expiry, now);
        var uac = u.Uac | (u.LockedOut ? UserStatus.Lockout : 0);

        return new DirectoryUser
        {
            Id = u.Id, Dn = u.Dn, Ou = u.Ou, SamAccountName = u.Sam, UserPrincipalName = u.Sam + "@fake.local",
            DisplayName = u.Display, GivenName = u.Given, Surname = u.Surname, Email = u.Email,
            EmployeeId = o.EmployeeIdAttribute.Equals("employeeID", StringComparison.OrdinalIgnoreCase) ? u.Employee : null,
            Title = u.Title, Department = u.Department, Office = u.Office, Phone = u.Phone, Mobile = u.Mobile, Description = u.Description,
            Manager = Ref(u.Manager),
            Enabled = UserStatus.IsEnabled(u.Uac), LockedOut = UserStatus.IsLockedOut(computed),
            AccountExpiry = aeKind, AccountExpiresUtc = aeDate, PasswordStatus = pwStatus, PasswordExpiresUtc = pwDate,
            PasswordLastSetUtc = u.PwdLastSet == 0 ? null : DateTime.FromFileTimeUtc(u.PwdLastSet),
            LastLogonUtc = u.LastLogon, CreatedUtc = u.Created, ChangedUtc = u.Changed,
            ResultantPso = u.Pso, UserAccountControl = uac, UacFlags = UserStatus.DescribeFlags(uac),
            PrimaryGroupId = 513, AdminCount = u.AdminCount,
        };
    }

    private DirectoryComputer ToComputer(FakeComputer c, DirectoryReadOptions o)
    {
        string? lastUser = null;
        if (!string.IsNullOrWhiteSpace(o.ComputerLastUserAttribute) && o.ComputerLastUserAttribute.Equals("description", StringComparison.OrdinalIgnoreCase))
            lastUser = c.Description;
        return new DirectoryComputer
        {
            Id = c.Id, Dn = c.Dn, Ou = c.Ou, Name = c.Name, DnsHostName = c.Name.ToLowerInvariant() + ".fake.local", Enabled = !c.Disabled,
            OperatingSystem = c.Os, OsVersion = c.OsVersion, LastLogonUtc = c.LastLogon, PasswordLastSetUtc = c.PwdLastSet,
            ManagedBy = Ref(c.ManagedBy), Description = c.Description, ChangedUtc = c.Changed, CreatedUtc = c.Created,
            LastLoggedInUser = lastUser,
        };
    }

    private DirectoryGroup ToGroup(FakeGroup g, bool withCount = false) => new()
    {
        Id = g.Id, Dn = g.Dn, Ou = g.Ou, Name = g.Name, Description = g.Description, Scope = g.Scope, Type = g.Type,
        ManagedBy = Ref(g.ManagedBy), AdminCount = g.AdminCount, PrimaryGroupToken = g.Token,
        MemberCount = withCount ? (_d.Members.TryGetValue(g.Id, out var m) ? m.Count : 0) : null,
    };

    private static PagedResult<T> Page<T>(List<T> all, int page, int pageSize, int limit)
    {
        var capped = all.Count > limit;
        var total = capped ? limit : all.Count;
        pageSize = Math.Max(1, pageSize);
        page = Math.Max(1, page);
        return new PagedResult<T>
        {
            Items = all.Take(limit).Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Total = total, Page = page, PageSize = pageSize, TotalIsCapped = capped,
        };
    }

    private static bool Has(string? value, string t) => value != null && value.Contains(t, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ reads

    public Task<PagedResult<DirectoryUser>> SearchUsersAsync(UserSearch s, CancellationToken ct = default)
    {
        Guard();
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var q = _d.Users.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(s.Text))
            {
                var t = s.Text.Trim();
                q = q.Where(u => Has(u.Sam, t) || Has(u.Sam + "@fake.local", t) || Has(u.Display, t) || Has(u.Given, t) || Has(u.Surname, t) || Has(u.Email, t) || Has(u.Employee, t));
            }
            if (!string.IsNullOrEmpty(s.OuDn)) q = q.Where(u => DnText.IsUnderOrEqual(u.Ou, s.OuDn));
            q = s.Filter switch
            {
                UserFilter.Locked => q.Where(u => u.LockedOut),
                UserFilter.Disabled => q.Where(u => !UserStatus.IsEnabled(u.Uac)),
                UserFilter.Enabled => q.Where(u => UserStatus.IsEnabled(u.Uac)),
                UserFilter.AccountExpired => q.Where(u => UserStatus.AccountExpiry(u.AccountExpires, now).Kind == "Expired"),
                _ => q,
            };
            var all = q.OrderBy(u => u.Display ?? u.Sam, StringComparer.OrdinalIgnoreCase).ToList();
            var page = Page(all, s.Page, s.PageSize, s.Limit);
            return Task.FromResult(new PagedResult<DirectoryUser>
            {
                Items = page.Items.Select(u => ToUser(u, s.Options)).ToList(),
                Total = page.Total, Page = page.Page, PageSize = page.PageSize, TotalIsCapped = page.TotalIsCapped,
            });
        }
    }

    public Task<DirectoryUser?> GetUserAsync(Guid id, DirectoryReadOptions o, CancellationToken ct = default)
    {
        Guard();
        lock (_lock) return Task.FromResult(_d.Users.FirstOrDefault(u => u.Id == id) is { } u ? ToUser(u, o) : null);
    }

    public Task<PagedResult<DirectoryComputer>> SearchComputersAsync(ComputerSearch s, CancellationToken ct = default)
    {
        Guard();
        lock (_lock)
        {
            var q = _d.Computers.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(s.Text))
            {
                var t = s.Text.Trim();
                q = q.Where(c => Has(c.Name, t) || Has(c.Name + ".fake.local", t));
            }
            if (!string.IsNullOrEmpty(s.OuDn)) q = q.Where(c => DnText.IsUnderOrEqual(c.Ou, s.OuDn));
            q = s.Filter switch
            {
                ComputerFilter.Disabled => q.Where(c => c.Disabled),
                ComputerFilter.Enabled => q.Where(c => !c.Disabled),
                _ => q,
            };
            var page = Page(q.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(), s.Page, s.PageSize, s.Limit);
            return Task.FromResult(new PagedResult<DirectoryComputer>
            {
                Items = page.Items.Select(c => ToComputer(c, s.Options)).ToList(),
                Total = page.Total, Page = page.Page, PageSize = page.PageSize, TotalIsCapped = page.TotalIsCapped,
            });
        }
    }

    public Task<DirectoryComputer?> GetComputerAsync(Guid id, DirectoryReadOptions o, CancellationToken ct = default)
    {
        Guard();
        lock (_lock) return Task.FromResult(_d.Computers.FirstOrDefault(c => c.Id == id) is { } c ? ToComputer(c, o) : null);
    }

    public Task<PagedResult<DirectoryGroup>> SearchGroupsAsync(GroupSearch s, CancellationToken ct = default)
    {
        Guard();
        lock (_lock)
        {
            var q = _d.Groups.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(s.Text))
            {
                var t = s.Text.Trim();
                q = q.Where(g => Has(g.Name, t) || Has(g.Description, t));
            }
            var page = Page(q.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(), s.Page, s.PageSize, s.Limit);
            return Task.FromResult(new PagedResult<DirectoryGroup>
            {
                Items = page.Items.Select(g => ToGroup(g)).ToList(),
                Total = page.Total, Page = page.Page, PageSize = page.PageSize, TotalIsCapped = page.TotalIsCapped,
            });
        }
    }

    public Task<DirectoryGroup?> GetGroupAsync(Guid id, CancellationToken ct = default)
    {
        Guard();
        lock (_lock) return Task.FromResult(_d.Groups.FirstOrDefault(g => g.Id == id) is { } g ? ToGroup(g, withCount: true) : null);
    }

    private IEnumerable<FakeGroup> GroupsOf(Guid memberId) =>
        _d.Groups.Where(g => _d.Members.TryGetValue(g.Id, out var m) && m.Contains(memberId));

    public Task<Memberships?> GetMembershipsAsync(Guid objectId, DirectoryObjectKind kind, CancellationToken ct = default)
    {
        Guard();
        lock (_lock)
        {
            var exists = kind == DirectoryObjectKind.User ? _d.Users.Any(u => u.Id == objectId) : _d.Computers.Any(c => c.Id == objectId);
            if (!exists) return Task.FromResult<Memberships?>(null);

            var primaryName = kind == DirectoryObjectKind.User ? "Domain Users" : "Domain Computers";
            var primary = _d.Group(primaryName);
            // Primary group membership is not stored in "member": show it once, as the primary group.
            var direct = GroupsOf(objectId).Where(g => g.Id != primary.Id).OrderBy(g => g.Name).ToList();
            var directIds = direct.Select(g => g.Id).ToHashSet();

            var nested = new Dictionary<Guid, NestedMembership>();
            foreach (var d in direct)
            {
                var queue = new Queue<FakeGroup>([d]);
                var seen = new HashSet<Guid> { d.Id };
                while (queue.Count > 0)
                    foreach (var parent in GroupsOf(queue.Dequeue().Id))
                    {
                        if (!seen.Add(parent.Id)) continue;
                        queue.Enqueue(parent);
                        if (!directIds.Contains(parent.Id) && !nested.ContainsKey(parent.Id))
                            nested[parent.Id] = new NestedMembership(ToGroup(parent), d.Name);
                    }
            }
            return Task.FromResult<Memberships?>(new Memberships
            {
                Direct = direct.Select(g => ToGroup(g)).ToList(),
                Nested = nested.Values.OrderBy(n => n.Group.Name).ToList(),
                Primary = ToGroup(primary),
            });
        }
    }

    public Task<PagedResult<GroupMember>> SearchGroupMembersAsync(Guid groupId, MemberSearch s, CancellationToken ct = default)
    {
        Guard();
        lock (_lock)
        {
            if (!_d.Members.TryGetValue(groupId, out var ids)) ids = [];
            var t = s.Text?.Trim();

            // Lazily map and filter so only the requested page is materialised, like a server-side filtered, paged query.
            IEnumerable<GroupMember> Resolve() =>
                ids.Select(id =>
                {
                    var u = _d.Users.FirstOrDefault(x => x.Id == id);
                    if (u != null) return new GroupMember { Id = u.Id, Kind = MemberKind.User, Name = u.Display ?? u.Sam, SamAccountName = u.Sam, Email = u.Email, Dn = u.Dn, Enabled = UserStatus.IsEnabled(u.Uac) };
                    var c = _d.Computers.FirstOrDefault(x => x.Id == id);
                    if (c != null) return new GroupMember { Id = c.Id, Kind = MemberKind.Computer, Name = c.Name, SamAccountName = c.Name + "$", Dn = c.Dn, Enabled = !c.Disabled };
                    var g = _d.Groups.FirstOrDefault(x => x.Id == id);
                    return g == null ? null : new GroupMember { Id = g.Id, Kind = MemberKind.Group, Name = g.Name, SamAccountName = g.Name, Dn = g.Dn };
                }).Where(m => m != null).Select(m => m!);

            var matches = Resolve()
                .Where(m => s.Kind == null || m.Kind == s.Kind)
                .Where(m => string.IsNullOrEmpty(t) || Has(m.Name, t) || Has(m.SamAccountName, t) || Has(m.Email, t))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var size = Math.Clamp(s.PageSize, 1, 1000);
            var page = Math.Max(1, s.Page);
            return Task.FromResult(new PagedResult<GroupMember>
            {
                Items = matches.Skip((page - 1) * size).Take(size).ToList(), Total = matches.Count, Page = page, PageSize = size,
            });
        }
    }

    private static string OuName(string dn)
    {
        var first = DnText.SplitRdns(dn)[0];
        return first[(first.IndexOf('=') + 1)..];
    }

    public Task<IReadOnlyList<DirectoryOu>> BrowseOusAsync(string? parentDn, CancellationToken ct = default)
    {
        Guard();
        var parent = string.IsNullOrWhiteSpace(parentDn) ? FakeDirectoryData.Base : parentDn;
        IReadOnlyList<DirectoryOu> list = FakeDirectoryData.OuList
            .Where(o => DnText.Equal(UserStatus.ParentDn(o), parent))
            .OrderBy(OuName, StringComparer.OrdinalIgnoreCase)
            .Select(o => new DirectoryOu(o, OuName(o), FakeDirectoryData.OuList.Any(x => DnText.Equal(UserStatus.ParentDn(x), o))))
            .ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<DirectoryOu>> SearchOusAsync(string text, int limit, CancellationToken ct = default)
    {
        Guard();
        IReadOnlyList<DirectoryOu> list = FakeDirectoryData.OuList
            .Where(o => Has(OuName(o), text))
            .Take(limit)
            .Select(o => new DirectoryOu(o, OuName(o), FakeDirectoryData.OuList.Any(x => DnText.Equal(UserStatus.ParentDn(x), o))))
            .ToList();
        return Task.FromResult(list);
    }

    public Task<DirectoryOu?> GetOuAsync(string dn, CancellationToken ct = default)
    {
        Guard();
        var ou = FakeDirectoryData.OuList.FirstOrDefault(o => DnText.Equal(o, dn));
        return Task.FromResult(ou == null ? null : new DirectoryOu(ou, OuName(ou), FakeDirectoryData.OuList.Any(x => DnText.Equal(UserStatus.ParentDn(x), ou))));
    }

    public async Task<long> CountUsersAsync(UserFilter filter, CancellationToken ct = default) =>
        (await SearchUsersAsync(new UserSearch(null, filter, 1, 1, null, int.MaxValue, new DirectoryReadOptions()), ct)).Total;

    public async Task<long> CountComputersAsync(ComputerFilter filter, CancellationToken ct = default) =>
        (await SearchComputersAsync(new ComputerSearch(null, filter, 1, 1, null, int.MaxValue, new DirectoryReadOptions()), ct)).Total;

    // ------------------------------------------------------------------ writes (all support dry-run)

    private static readonly DryRunCheck Found = new("Target object found by objectGUID", true);

    private DirectoryResult? Preflight(Guid id, DirectoryObjectKind kind, bool dryRun, out List<DryRunCheck> checks)
    {
        checks = [];
        var exists = kind switch
        {
            DirectoryObjectKind.User => _d.Users.Any(u => u.Id == id),
            DirectoryObjectKind.Computer => _d.Computers.Any(c => c.Id == id),
            _ => _d.Groups.Any(g => g.Id == id),
        };
        if (!exists)
        {
            checks.Add(new DryRunCheck("Target object found by objectGUID", false, "No object with that objectGUID"));
            return DirectoryResult.Fail(dryRun, DirectoryErrors.NotFound, "The object no longer exists in the directory.", checks);
        }
        checks.Add(Found);
        var allowed = !_d.DeniedTargets.Contains(id);
        checks.Add(new DryRunCheck("Service account has the rights to make this change (allowedAttributesEffective)", allowed,
            allowed ? null : "The service account has no write access to this object"));
        return allowed ? null : DirectoryResult.Fail(dryRun, DirectoryErrors.PermissionDenied, "The service account does not have permission to change this object.", checks);
    }

    public Task<DirectoryResult> ResetPasswordAsync(Guid userId, SecureString newPassword, ResetPasswordOptions options, bool dryRun, CancellationToken ct = default)
    {
        Guard();
        lock (_lock)
        {
            if (Preflight(userId, DirectoryObjectKind.User, dryRun, out var checks) is { } failed) return Task.FromResult(failed);
            var u = _d.Users.First(x => x.Id == userId);
            var changes = new List<DirectoryChange> { new("Password", null, "Reset (value not shown)") };
            if (options.MustChangeAtNextSignIn) changes.Add(new("Must change password at next sign-in", u.PwdLastSet == 0 ? "Yes" : "No", "Yes"));
            if (options.UnlockAccount) changes.Add(new("Locked out", u.LockedOut ? "Yes" : "No", "No"));

            // A dry run never looks at, or sends, the password.
            if (dryRun) return Task.FromResult(DirectoryResult.Ok(true, changes, checks));

            var problem = CheckPasswordPolicy(newPassword, u.Sam);
            if (problem != null) return Task.FromResult(DirectoryResult.Fail(false, DirectoryErrors.PasswordRejected, problem));

            u.PwdLastSet = options.MustChangeAtNextSignIn ? 0 : DateTime.UtcNow.ToFileTimeUtc();
            if (options.UnlockAccount) u.LockedOut = false;
            u.Changed = DateTime.UtcNow;
            return Task.FromResult(DirectoryResult.Ok(false, changes));
        }
    }

    /// <summary>A stand-in for the domain policy: 8+ characters, three of four character classes, not the account name.</summary>
    private static string? CheckPasswordPolicy(SecureString password, string sam)
    {
        var ptr = Marshal.SecureStringToGlobalAllocUnicode(password);
        try
        {
            var pw = Marshal.PtrToStringUni(ptr) ?? "";
            var classes = (pw.Any(char.IsLower) ? 1 : 0) + (pw.Any(char.IsUpper) ? 1 : 0) + (pw.Any(char.IsDigit) ? 1 : 0) + (pw.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
            if (pw.Length < 8 || classes < 3 || pw.Contains(sam, StringComparison.OrdinalIgnoreCase))
                return "The password does not meet the domain password policy (length, complexity or history).";
            return null;
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(ptr); }
    }

    public Task<DirectoryResult> UnlockAsync(Guid userId, bool dryRun, CancellationToken ct = default)
    {
        Guard();
        lock (_lock)
        {
            if (Preflight(userId, DirectoryObjectKind.User, dryRun, out var checks) is { } failed) return Task.FromResult(failed);
            var u = _d.Users.First(x => x.Id == userId);
            var changes = new[] { new DirectoryChange("Locked out", u.LockedOut ? "Yes" : "No", "No") };
            if (!dryRun) { u.LockedOut = false; u.Changed = DateTime.UtcNow; }
            return Task.FromResult(DirectoryResult.Ok(dryRun, changes, checks));
        }
    }

    public Task<DirectoryResult> SetEnabledAsync(Guid objectId, DirectoryObjectKind kind, bool enabled, bool dryRun, CancellationToken ct = default)
    {
        Guard();
        lock (_lock)
        {
            if (kind == DirectoryObjectKind.Group) return Task.FromResult(DirectoryResult.Fail(dryRun, DirectoryErrors.Constraint, "Groups cannot be enabled or disabled."));
            if (Preflight(objectId, kind, dryRun, out var checks) is { } failed) return Task.FromResult(failed);
            var was = kind == DirectoryObjectKind.User ? UserStatus.IsEnabled(_d.Users.First(x => x.Id == objectId).Uac) : !_d.Computers.First(x => x.Id == objectId).Disabled;
            var changes = new[] { new DirectoryChange("Enabled", was ? "Yes" : "No", enabled ? "Yes" : "No") };
            if (!dryRun)
            {
                if (kind == DirectoryObjectKind.User)
                {
                    var u = _d.Users.First(x => x.Id == objectId);
                    u.Uac = enabled ? u.Uac & ~UserStatus.AccountDisable : u.Uac | UserStatus.AccountDisable;
                    u.Changed = DateTime.UtcNow;
                }
                else
                {
                    var c = _d.Computers.First(x => x.Id == objectId);
                    c.Disabled = !enabled;
                    c.Changed = DateTime.UtcNow;
                }
            }
            return Task.FromResult(DirectoryResult.Ok(dryRun, changes, checks));
        }
    }

    public Task<DirectoryResult> MoveAsync(Guid objectId, DirectoryObjectKind kind, string targetOuDn, bool dryRun, CancellationToken ct = default)
    {
        Guard();
        lock (_lock)
        {
            if (kind == DirectoryObjectKind.Group) return Task.FromResult(DirectoryResult.Fail(dryRun, DirectoryErrors.Constraint, "Groups cannot be moved in Version 1."));
            if (Preflight(objectId, kind, dryRun, out var checks) is { } failed) return Task.FromResult(failed);
            var target = FakeDirectoryData.OuList.FirstOrDefault(o => DnText.Equal(o, targetOuDn));
            checks.Add(new DryRunCheck("Target OU exists", target != null, target == null ? "The target OU was not found" : null));
            if (target == null) return Task.FromResult(DirectoryResult.Fail(dryRun, DirectoryErrors.NotFound, "The target OU does not exist.", checks));
            checks.Add(new DryRunCheck("Service account may create this object type in the target OU (allowedChildClassesEffective)", true));

            var from = kind == DirectoryObjectKind.User ? _d.Users.First(x => x.Id == objectId).Ou : _d.Computers.First(x => x.Id == objectId).Ou;
            var changes = new[] { new DirectoryChange("OU", from, target) };
            if (!dryRun)
            {
                if (kind == DirectoryObjectKind.User) { var u = _d.Users.First(x => x.Id == objectId); u.Ou = target; u.Changed = DateTime.UtcNow; }
                else { var c = _d.Computers.First(x => x.Id == objectId); c.Ou = target; c.Changed = DateTime.UtcNow; }
            }
            return Task.FromResult(DirectoryResult.Ok(dryRun, changes, checks));
        }
    }

    private Task<DirectoryResult> ChangeMembership(Guid memberId, IReadOnlyList<Guid> groupIds, bool add, bool dryRun)
    {
        Guard();
        lock (_lock)
        {
            var isUser = _d.Users.Any(u => u.Id == memberId);
            var kind = isUser ? DirectoryObjectKind.User : DirectoryObjectKind.Computer;
            if (Preflight(memberId, kind, dryRun, out var checks) is { } failed) return Task.FromResult(failed);

            var changes = new List<DirectoryChange>();
            foreach (var gid in groupIds)
            {
                var g = _d.Groups.FirstOrDefault(x => x.Id == gid);
                if (g == null)
                {
                    checks.Add(new DryRunCheck("Group found by objectGUID", false));
                    return Task.FromResult(DirectoryResult.Fail(dryRun, DirectoryErrors.NotFound, "The group no longer exists.", checks));
                }
                checks.Add(new DryRunCheck($"Group '{g.Name}' found", true));
                var isMember = _d.Members.TryGetValue(gid, out var set) && set.Contains(memberId);
                if (add && isMember) return Task.FromResult(DirectoryResult.Fail(dryRun, DirectoryErrors.AlreadyMember, $"Already a member of {g.Name}.", checks));
                if (!add && !isMember) return Task.FromResult(DirectoryResult.Fail(dryRun, DirectoryErrors.NotMember, $"Not a direct member of {g.Name}.", checks));
                if (_d.DeniedTargets.Contains(gid))
                    return Task.FromResult(DirectoryResult.Fail(dryRun, DirectoryErrors.PermissionDenied, "The service account cannot change this group.", checks));
                checks.Add(new DryRunCheck($"Service account may modify 'member' on '{g.Name}' (allowedAttributesEffective)", true));
                changes.Add(new DirectoryChange("Group membership: " + g.Name, isMember ? "Member" : "Not a member", add ? "Member" : "Not a member"));
            }
            if (!dryRun)
                foreach (var gid in groupIds)
                {
                    if (!_d.Members.TryGetValue(gid, out var set)) _d.Members[gid] = set = [];
                    if (add) set.Add(memberId); else set.Remove(memberId);
                }
            return Task.FromResult(DirectoryResult.Ok(dryRun, changes, checks));
        }
    }

    public Task<DirectoryResult> AddToGroupsAsync(Guid memberId, IReadOnlyList<Guid> groupIds, bool dryRun, CancellationToken ct = default) =>
        ChangeMembership(memberId, groupIds, add: true, dryRun);

    public Task<DirectoryResult> RemoveFromGroupsAsync(Guid memberId, IReadOnlyList<Guid> groupIds, bool dryRun, CancellationToken ct = default) =>
        ChangeMembership(memberId, groupIds, add: false, dryRun);

    public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        Guard();
        return Task.FromResult(new ConnectionTestResult(true,
        [
            new ConnectionStep("Bind", true, "Fake in-memory directory (no network)"),
            new ConnectionStep("Search base", true, FakeDirectoryData.Base),
            new ConnectionStep("Secure connection", true, "Not applicable to the Fake provider"),
            new ConnectionStep("Sample search", true, $"{_d.Users.Count} users, {_d.Computers.Count} computers, {_d.Groups.Count} groups"),
        ]));
    }
}
