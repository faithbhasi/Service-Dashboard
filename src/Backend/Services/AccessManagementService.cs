using Microsoft.EntityFrameworkCore;
using ServiceDashboard.Data;
using ServiceDashboard.Models;

namespace ServiceDashboard.Services;

public sealed record RoleDto(Guid Id, string Name, string? Description, bool IsSystem, bool IsLocked, IReadOnlyList<string> Permissions, int UserCount, int MappingCount);

/// <summary>
/// Manages roles, role assignments and Okta group mappings, and enforces the safeguards:
/// nobody can raise their own access, nobody can manage a role more powerful than their own,
/// the last Admins assignment cannot be removed, and every change is audited (denied attempts too).
/// </summary>
public sealed class AccessManagementService(AppDbContext db, ICurrentUser currentUser, IAuditService audit, AccessService access)
{
    private const string Module = "core";

    // ---------- roles ----------

    public async Task<List<RoleDto>> ListRolesAsync()
    {
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.Name).ToListAsync();
        var userCounts = await db.UserRoles.GroupBy(x => x.RoleId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var mapCounts = await db.GroupMappings.GroupBy(x => x.RoleId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        return roles.Select(r => ToDto(r, userCounts.GetValueOrDefault(r.Id), mapCounts.GetValueOrDefault(r.Id))).ToList();
    }

    private static RoleDto ToDto(Role r, int users, int maps) =>
        new(r.Id, r.Name, r.Description, r.IsSystem, r.Id == DefaultRoles.AdminsId,
            r.Permissions.Select(p => p.Permission).Order().ToList(), users, maps);

    public async Task<RoleDto> CreateRoleAsync(string name, string? description, IEnumerable<string> permissions, string auditAction = "admin.role.create")
    {
        var caller = await Caller();
        var perms = ValidatePermissions(permissions);
        name = await ValidateNameAsync(name, null);
        await RequireCanGrantAsync(caller, perms, auditAction, name);

        var role = new Role { Name = name, Description = description?.Trim() };
        DatabaseSeeder.SetPermissions(role, perms);
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        await audit.WriteAsync(new AuditEntry
        {
            Action = auditAction, Module = Module, Target = "Role: " + name, TargetId = role.Id.ToString(),
            NewValue = Describe(role.Name, perms),
        });
        return ToDto(role, 0, 0);
    }

    public async Task<RoleDto> CloneRoleAsync(Guid id, string name)
    {
        var source = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id) ?? throw NotFound("Role");
        return await CreateRoleAsync(name, source.Description, source.Permissions.Select(p => p.Permission), "admin.role.clone");
    }

    public async Task<RoleDto> UpdateRoleAsync(Guid id, string name, string? description, IEnumerable<string> permissions)
    {
        var caller = await Caller();
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id) ?? throw NotFound("Role");
        var perms = ValidatePermissions(permissions);
        var before = Describe(role.Name, role.Permissions.Select(p => p.Permission));

        if (role.Id == DefaultRoles.AdminsId)
            throw await Deny("admin.role.update", role, "The Admins role always has every permission and cannot be edited.");
        if (role.IsSystem && !string.Equals(name.Trim(), role.Name, StringComparison.Ordinal))
            throw await Deny("admin.role.update", role, "Default roles cannot be renamed.");

        name = await ValidateNameAsync(name, role.Id);
        var added = perms.Except(role.Permissions.Select(p => p.Permission)).ToList();
        await RequireCanGrantAsync(caller, added, "admin.role.update", role.Name, role);

        role.Name = name;
        role.Description = description?.Trim();
        DatabaseSeeder.SetPermissions(role, perms);
        await db.SaveChangesAsync();
        await audit.WriteAsync(new AuditEntry
        {
            Action = "admin.role.update", Module = Module, Target = "Role: " + name, TargetId = role.Id.ToString(),
            PreviousValue = before, NewValue = Describe(name, perms),
        });
        return ToDto(role, await db.UserRoles.CountAsync(x => x.RoleId == id), await db.GroupMappings.CountAsync(x => x.RoleId == id));
    }

    public async Task DeleteRoleAsync(Guid id)
    {
        await Caller();
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id) ?? throw NotFound("Role");
        if (role.IsSystem) throw await Deny("admin.role.delete", role, "The default roles cannot be deleted.");
        if (await db.UserRoles.AnyAsync(x => x.RoleId == id) || await db.GroupMappings.AnyAsync(x => x.RoleId == id))
            throw await Deny("admin.role.delete", role, "This role is still assigned to users or mapped to Okta groups. Remove those first.", 409);

        db.Roles.Remove(role);
        await db.SaveChangesAsync();
        await audit.WriteAsync(new AuditEntry
        {
            Action = "admin.role.delete", Module = Module, Target = "Role: " + role.Name, TargetId = id.ToString(),
            PreviousValue = Describe(role.Name, role.Permissions.Select(p => p.Permission)),
        });
    }

    // ---------- app users ----------

    public async Task SetUserStatusAsync(Guid userId, bool enabled)
    {
        var caller = await Caller();
        var user = await db.Users.Include(u => u.UserRoles).FirstOrDefaultAsync(u => u.Id == userId) ?? throw NotFound("User");
        var action = enabled ? "admin.user.enable" : "admin.user.disable";
        if (user.Id == caller.Id) throw await Deny(action, user, "You cannot change your own access to this application.");
        if (user.IsEnabled == enabled) return;

        var target = await access.ResolveAsync(user);
        await RequireHoldsAsync(caller, target.Permissions, action, user);

        if (!enabled && user.UserRoles.Any(r => r.RoleId == DefaultRoles.AdminsId) && await AdminAssignmentsAsync(excludeUser: user.Id) == 0)
            throw await Deny(action, user, "This is the last Admins assignment and cannot be removed.", 409);

        var was = user.IsEnabled;
        user.IsEnabled = enabled;
        await db.SaveChangesAsync();
        await audit.WriteAsync(new AuditEntry
        {
            Action = action, Module = Module, Target = "App user: " + user.DisplayName, TargetId = user.Id.ToString(),
            PreviousValue = was ? "Enabled" : "Disabled", NewValue = enabled ? "Enabled" : "Disabled",
        });
    }

    public async Task SetUserRolesAsync(Guid userId, IReadOnlyCollection<Guid> roleIds)
    {
        var caller = await Caller();
        var user = await db.Users.Include(u => u.UserRoles).FirstOrDefaultAsync(u => u.Id == userId) ?? throw NotFound("User");
        if (user.Id == caller.Id) throw await Deny("admin.user.roles", user, "You cannot change your own roles.");

        var roles = await db.Roles.Include(r => r.Permissions).ToDictionaryAsync(r => r.Id);
        var wanted = roleIds.Distinct().ToList();
        if (wanted.Any(r => !roles.ContainsKey(r))) throw new ApiException(400, "Unknown role", "One of the selected roles does not exist.", "validation");

        var current = user.UserRoles.Select(r => r.RoleId).ToHashSet();
        var changed = wanted.Except(current).Concat(current.Except(wanted)).ToList();
        foreach (var rid in changed)
            await RequireHoldsAsync(caller, roles[rid].Permissions.Select(p => p.Permission).ToHashSet(), "admin.user.roles", user);

        if (current.Contains(DefaultRoles.AdminsId) && !wanted.Contains(DefaultRoles.AdminsId)
            && user.IsEnabled && await AdminAssignmentsAsync(excludeUser: user.Id) == 0)
            throw await Deny("admin.user.roles", user, "This is the last Admins assignment and cannot be removed.", 409);

        var before = string.Join(", ", current.Select(r => roles[r].Name).Order());
        db.UserRoles.RemoveRange(user.UserRoles.Where(r => !wanted.Contains(r.RoleId)));
        foreach (var rid in wanted.Except(current)) db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = rid });
        await db.SaveChangesAsync();
        await audit.WriteAsync(new AuditEntry
        {
            Action = "admin.user.roles", Module = Module, Target = "App user: " + user.DisplayName, TargetId = user.Id.ToString(),
            PreviousValue = before, NewValue = string.Join(", ", wanted.Select(r => roles[r].Name).Order()),
        });
    }

    // ---------- Okta group mappings ----------

    public async Task<GroupMapping> AddMappingAsync(string oktaGroup, Guid roleId)
    {
        var caller = await Caller();
        oktaGroup = (oktaGroup ?? "").Trim();
        if (oktaGroup.Length is 0 or > 256) throw new ApiException(400, "Invalid group", "Enter the Okta group name (up to 256 characters).", "validation");
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == roleId) ?? throw NotFound("Role");
        await RequireHoldsAsync(caller, role.Permissions.Select(p => p.Permission).ToHashSet(), "admin.mapping.create", role);
        if (await db.GroupMappings.AnyAsync(m => m.OktaGroup == oktaGroup && m.RoleId == roleId))
            throw new ApiException(409, "Already mapped", "That Okta group is already mapped to this role.", "conflict");

        var m = new GroupMapping { OktaGroup = oktaGroup, RoleId = roleId };
        db.GroupMappings.Add(m);
        await db.SaveChangesAsync();
        await audit.WriteAsync(new AuditEntry
        {
            Action = "admin.mapping.create", Module = Module, Target = "Okta group: " + oktaGroup, TargetId = m.Id.ToString(),
            NewValue = $"{oktaGroup} -> {role.Name}",
        });
        return m;
    }

    public async Task DeleteMappingAsync(Guid id)
    {
        var caller = await Caller();
        var m = await db.GroupMappings.Include(x => x.Role).ThenInclude(r => r!.Permissions).FirstOrDefaultAsync(x => x.Id == id) ?? throw NotFound("Mapping");
        await RequireHoldsAsync(caller, m.Role!.Permissions.Select(p => p.Permission).ToHashSet(), "admin.mapping.delete", m.Role);
        if (m.RoleId == DefaultRoles.AdminsId && await AdminAssignmentsAsync(excludeMapping: id) == 0)
            throw await Deny("admin.mapping.delete", m.Role, "This is the last Admins assignment and cannot be removed.", 409);

        db.GroupMappings.Remove(m);
        await db.SaveChangesAsync();
        await audit.WriteAsync(new AuditEntry
        {
            Action = "admin.mapping.delete", Module = Module, Target = "Okta group: " + m.OktaGroup, TargetId = id.ToString(),
            PreviousValue = $"{m.OktaGroup} -> {m.Role.Name}",
        });
    }

    // ---------- helpers ----------

    private async Task<CurrentUserInfo> Caller() =>
        await currentUser.GetAsync() ?? throw new ApiException(401, "Not signed in", "Sign in to continue.", "unauthenticated");

    /// <summary>Admins assignments that would remain: enabled users holding the role directly plus Okta group mappings to it.</summary>
    private async Task<int> AdminAssignmentsAsync(Guid? excludeUser = null, Guid? excludeMapping = null)
    {
        var direct = await db.UserRoles.CountAsync(ur => ur.RoleId == DefaultRoles.AdminsId && ur.User!.IsEnabled && ur.UserId != excludeUser);
        var mapped = await db.GroupMappings.CountAsync(m => m.RoleId == DefaultRoles.AdminsId && m.Id != excludeMapping);
        return direct + mapped;
    }

    private static List<string> ValidatePermissions(IEnumerable<string> permissions)
    {
        var list = permissions.Distinct().ToList();
        var unknown = list.Where(p => !Permissions.AllIds.Contains(p)).ToList();
        if (unknown.Count > 0) throw new ApiException(400, "Unknown permission", "Unknown permission: " + string.Join(", ", unknown), "validation");
        return list;
    }

    private async Task<string> ValidateNameAsync(string name, Guid? ownId)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 100) throw new ApiException(400, "Invalid name", "Enter a role name (up to 100 characters).", "validation");
        if (await db.Roles.AnyAsync(r => r.Name == name && r.Id != ownId))
            throw new ApiException(409, "Name in use", "A role with that name already exists.", "conflict");
        return name;
    }

    private async Task RequireCanGrantAsync(CurrentUserInfo caller, IEnumerable<string> perms, string action, string targetName, Role? role = null)
    {
        var missing = perms.Where(p => !caller.Permissions.Contains(p)).ToList();
        if (missing.Count == 0) return;
        await audit.WriteAsync(new AuditEntry
        {
            Action = action, Module = Module, Target = "Role: " + targetName, TargetId = role?.Id.ToString(),
            Result = AuditResult.Denied, Error = "Cannot grant permissions you do not hold: " + string.Join(", ", missing),
        });
        throw new ApiException(403, "Not allowed", "You cannot grant permissions you do not hold yourself: " + string.Join(", ", missing), "escalation");
    }

    private async Task RequireHoldsAsync(CurrentUserInfo caller, IReadOnlySet<string> needed, string action, object target)
    {
        var missing = needed.Where(p => !caller.Permissions.Contains(p)).ToList();
        if (missing.Count == 0) return;
        throw await Deny(action, target, "You can only manage access that is no more powerful than your own. You lack: " + string.Join(", ", missing), 403);
    }

    private async Task<ApiException> Deny(string action, object target, string message, int status = 403)
    {
        var (name, id) = target switch
        {
            Role r => ("Role: " + r.Name, r.Id.ToString()),
            AppUser u => ("App user: " + u.DisplayName, u.Id.ToString()),
            _ => (target.ToString() ?? "", (string?)null),
        };
        await audit.WriteAsync(new AuditEntry
        {
            Action = action, Module = Module, Target = name, TargetId = id, Result = AuditResult.Denied, Error = message,
        });
        return new ApiException(status, status == 409 ? "Conflict" : "Not allowed", message, status == 409 ? "safeguard" : "forbidden");
    }

    private static ApiException NotFound(string what) => new(404, "Not found", $"{what} not found.", "not_found");

    private static string Describe(string name, IEnumerable<string> perms) => $"{name}: {string.Join(", ", perms.Order())}";
}
