using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ServiceDashboard.Data;
using ServiceDashboard.Models;

namespace ServiceDashboard.Services;

public sealed record RoleRef(Guid Id, string Name, string Source);

public sealed record ResolvedAccess(IReadOnlyList<RoleRef> Roles, IReadOnlySet<string> Permissions, AdScope AdScope);

/// <summary>Works out which roles (direct assignment or Okta group mapping) and permissions a user has.</summary>
public sealed class AccessService(AppDbContext db)
{
    public async Task<ResolvedAccess> ResolveAsync(AppUser user) =>
        (await ResolveManyAsync([user]))[user.Id];

    public async Task<Dictionary<Guid, ResolvedAccess>> ResolveManyAsync(IReadOnlyCollection<AppUser> users)
    {
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).ToDictionaryAsync(r => r.Id);
        var mappings = await db.GroupMappings.AsNoTracking().ToListAsync();
        var userIds = users.Select(u => u.Id).ToList();
        var direct = await db.UserRoles.AsNoTracking().Where(ur => userIds.Contains(ur.UserId)).ToListAsync();

        var result = new Dictionary<Guid, ResolvedAccess>();
        foreach (var u in users)
        {
            var refs = new Dictionary<Guid, RoleRef>();
            foreach (var ur in direct.Where(d => d.UserId == u.Id))
                if (roles.TryGetValue(ur.RoleId, out var r)) refs[r.Id] = new RoleRef(r.Id, r.Name, "Assigned in this app");

            var groups = ParseGroups(u.OktaGroupsJson);
            foreach (var m in mappings.Where(m => groups.Contains(m.OktaGroup)))
                if (roles.TryGetValue(m.RoleId, out var r) && !refs.ContainsKey(r.Id))
                    refs[r.Id] = new RoleRef(r.Id, r.Name, $"Okta group: {m.OktaGroup}");

            var perms = refs.Values.SelectMany(rr => roles[rr.Id].Permissions.Select(p => p.Permission))
                .Where(Permissions.AllIds.Contains).ToHashSet(StringComparer.Ordinal);
            result[u.Id] = new ResolvedAccess(refs.Values.OrderBy(r => r.Name).ToList(), perms, EffectiveScope(refs.Keys.Select(id => roles[id])));
        }
        return result;
    }

    public static readonly string[] UserChangePermissions =
    [
        Permissions.AdUsersResetPassword, Permissions.AdUsersUnlock, Permissions.AdUsersEnable, Permissions.AdUsersDisable,
        Permissions.AdUsersMove, Permissions.AdUsersGroupsAdd, Permissions.AdUsersGroupsRemove,
    ];
    public static readonly string[] ComputerChangePermissions = [Permissions.AdComputersEnable, Permissions.AdComputersDisable, Permissions.AdComputersMove];
    public static readonly string[] GroupChangePermissions = [Permissions.AdUsersGroupsAdd, Permissions.AdUsersGroupsRemove];

    /// <summary>
    /// What all of a person's roles together may manage. Only roles that can change that kind of object count (a read-only role must not
    /// widen a helpdesk role), the Admins role is never limited, and a role with no limit makes the result unlimited for that kind.
    /// </summary>
    public static AdScope EffectiveScope(IEnumerable<Role> roles)
    {
        var all = roles.Select(r => (Role: r, Scope: AdScope.Parse(r.AdScopeJson), Perms: r.Permissions.Select(p => p.Permission).ToHashSet())).ToList();

        List<string>? Combine(string[] relevant, Func<AdScope, List<string>?> pick)
        {
            var counted = all.Where(x => x.Perms.Overlaps(relevant)).ToList();
            if (counted.Count == 0) return null; // nothing to limit: they cannot change this kind of object anyway
            if (counted.Any(x => x.Role.Id == DefaultRoles.AdminsId || pick(x.Scope) == null)) return null;
            return counted.SelectMany(x => pick(x.Scope)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return new AdScope
        {
            UserOus = Combine(UserChangePermissions, s => s.UserOus),
            ComputerOus = Combine(ComputerChangePermissions, s => s.ComputerOus),
            Groups = Combine(GroupChangePermissions, s => s.Groups),
        };
    }

    public static HashSet<string> ParseGroups(string json)
    {
        try { return (JsonSerializer.Deserialize<string[]>(json) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch (JsonException) { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }
}
