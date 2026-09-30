using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ServiceDashboard.Data;
using ServiceDashboard.Models;

namespace ServiceDashboard.Services;

public sealed record RoleRef(Guid Id, string Name, string Source);

public sealed record ResolvedAccess(IReadOnlyList<RoleRef> Roles, IReadOnlySet<string> Permissions);

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
            result[u.Id] = new ResolvedAccess(refs.Values.OrderBy(r => r.Name).ToList(), perms);
        }
        return result;
    }

    public static HashSet<string> ParseGroups(string json)
    {
        try { return (JsonSerializer.Deserialize<string[]>(json) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch (JsonException) { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }
}
