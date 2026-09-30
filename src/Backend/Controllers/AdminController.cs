using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDashboard.Data;
using ServiceDashboard.Models;
using ServiceDashboard.Services;

namespace ServiceDashboard.Controllers;

/// <summary>In-app access management: who can use this application and what they can do. It never touches AD or Okta.</summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminController(AppDbContext db, AccessService access, AccessManagementService manage, IAuditService audit) : ControllerBase
{
    // ---------- permissions and roles (read) ----------

    [HttpGet("permissions"), Authorize(Policy = PermissionPolicies.AdminAccess)]
    public async Task<IActionResult> GetPermissions()
    {
        var roles = await manage.ListRolesAsync();
        return Ok(Permissions.All.Select(p => new
        {
            p.Id, p.Group, p.Description,
            roles = roles.Where(r => r.Permissions.Contains(p.Id)).Select(r => r.Name).ToArray(),
        }));
    }

    [HttpGet("roles"), Authorize(Policy = PermissionPolicies.AdminAccess)]
    public async Task<IActionResult> GetRoles() => Ok(await manage.ListRolesAsync());

    /// <summary>The OUs and groups a role's Active Directory scope can be chosen from (the global manageable lists). Empty when AD is not available.</summary>
    [HttpGet("roles/ad-scope-options"), Authorize(Policy = PermissionPolicies.AdminAccess)]
    public async Task<IActionResult> GetAdScopeOptions([FromServices] IServiceProvider sp)
    {
        var catalog = sp.GetService<IAdScopeCatalog>();
        return Ok(catalog == null ? new AdScopeOptions([], [], []) : await catalog.GetOptionsAsync());
    }

    /// <summary>The OU tree (lazy) for ticking the OUs a role may manage. Only OUs inside the manageable lists can be chosen.</summary>
    [HttpGet("roles/ad-ou-tree"), Authorize(Policy = PermissionPolicies.AdminAccess)]
    public async Task<IActionResult> GetAdOuTree([FromQuery] string kind, [FromQuery] string? parent, [FromQuery] string? q, [FromServices] IServiceProvider sp)
    {
        var catalog = sp.GetService<IAdScopeCatalog>();
        return Ok(catalog == null ? Array.Empty<AdScopeOuNode>() : await catalog.BrowseOusAsync(kind, parent, q));
    }

    // ---------- roles (write) ----------

    public sealed record RoleRequest(string Name, string? Description, string[] Permissions, AdScope? AdScope = null);
    public sealed record CloneRequest(string Name);

    [HttpPost("roles"), Authorize(Policy = Permissions.AdminRolesManage)]
    public async Task<IActionResult> CreateRole([FromBody] RoleRequest r) =>
        Ok(await manage.CreateRoleAsync(r.Name, r.Description, r.Permissions ?? [], r.AdScope));

    [HttpPost("roles/{id:guid}/clone"), Authorize(Policy = Permissions.AdminRolesManage)]
    public async Task<IActionResult> CloneRole(Guid id, [FromBody] CloneRequest r) => Ok(await manage.CloneRoleAsync(id, r.Name));

    [HttpPut("roles/{id:guid}"), Authorize(Policy = Permissions.AdminRolesManage)]
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] RoleRequest r) =>
        Ok(await manage.UpdateRoleAsync(id, r.Name, r.Description, r.Permissions ?? [], r.AdScope));

    [HttpDelete("roles/{id:guid}"), Authorize(Policy = Permissions.AdminRolesManage)]
    public async Task<IActionResult> DeleteRole(Guid id)
    {
        await manage.DeleteRoleAsync(id);
        return NoContent();
    }

    // ---------- app users ----------

    public sealed record AppUserDto(Guid Id, string DisplayName, string Email, bool IsEnabled, DateTime CreatedUtc, DateTime? LastSignInUtc,
        IReadOnlyList<RoleRef> Roles, IReadOnlyList<string> Permissions, IReadOnlyList<string> OktaGroups);

    private async Task<(List<AppUserDto> Items, int Total)> QueryUsers(string? q, int skip, int take)
    {
        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim().ToLower();
            query = query.Where(u => u.DisplayName.ToLower().Contains(t) || u.Email.ToLower().Contains(t));
        }
        var total = await query.CountAsync();
        var users = await query.OrderBy(u => u.DisplayName).Skip(skip).Take(take).ToListAsync();
        var resolved = await access.ResolveManyAsync(users);
        var items = users.Select(u => new AppUserDto(u.Id, u.DisplayName, u.Email, u.IsEnabled, u.CreatedUtc, u.LastSignInUtc,
            resolved[u.Id].Roles, resolved[u.Id].Permissions.Order().ToList(), AccessService.ParseGroups(u.OktaGroupsJson).Order().ToList())).ToList();
        return (items, total);
    }

    [HttpGet("users"), Authorize(Policy = Permissions.AdminUsersManage)]
    public async Task<IActionResult> GetUsers([FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);
        var (items, total) = await QueryUsers(q, (page - 1) * pageSize, pageSize);
        return Ok(new { items, total, page, pageSize, totalIsCapped = false });
    }

    [HttpGet("users/export"), Authorize(Policy = Permissions.AdminUsersManage)]
    public async Task ExportUsers([FromQuery] string? q)
    {
        var (items, total) = await QueryUsers(q, 0, 100_000);
        await audit.WriteAsync(new AuditEntry
        {
            Action = "admin.users.export", Module = "core", Target = "App users", NewValue = $"{total} rows" + (string.IsNullOrWhiteSpace(q) ? "" : $", filter '{q}'"),
        });
        await CsvWriter.WriteAsync(Response, $"app-users-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv",
            ["Name", "Email", "Status", "Created (UTC)", "Last sign-in (UTC)", "Roles", "Effective permissions"],
            ToAsync(items.Select(u => (IEnumerable<string?>)
            [
                u.DisplayName, u.Email, u.IsEnabled ? "Enabled" : "Disabled", u.CreatedUtc.ToString("o"), u.LastSignInUtc?.ToString("o"),
                string.Join("; ", u.Roles.Select(r => r.Name)), string.Join("; ", u.Permissions),
            ])));
    }

    public sealed record StatusRequest(bool IsEnabled);
    public sealed record UserRolesRequest(Guid[] RoleIds);

    [HttpPut("users/{id:guid}/status"), Authorize(Policy = Permissions.AdminUsersManage)]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] StatusRequest r)
    {
        await manage.SetUserStatusAsync(id, r.IsEnabled);
        return NoContent();
    }

    [HttpPut("users/{id:guid}/roles"), Authorize(Policy = Permissions.AdminUsersManage)]
    public async Task<IActionResult> SetRoles(Guid id, [FromBody] UserRolesRequest r)
    {
        await manage.SetUserRolesAsync(id, r.RoleIds ?? []);
        return NoContent();
    }

    // ---------- Okta group mappings ----------

    public sealed record MappingRequest(string OktaGroup, Guid RoleId);

    [HttpGet("group-mappings"), Authorize(Policy = Permissions.AdminUsersManage)]
    public async Task<IActionResult> GetMappings() =>
        Ok(await db.GroupMappings.AsNoTracking().Include(m => m.Role).OrderBy(m => m.OktaGroup)
            .Select(m => new { m.Id, m.OktaGroup, m.RoleId, roleName = m.Role!.Name }).ToListAsync());

    [HttpPost("group-mappings"), Authorize(Policy = Permissions.AdminUsersManage)]
    public async Task<IActionResult> AddMapping([FromBody] MappingRequest r)
    {
        var m = await manage.AddMappingAsync(r.OktaGroup, r.RoleId);
        return Ok(new { m.Id, m.OktaGroup, m.RoleId });
    }

    [HttpDelete("group-mappings/{id:guid}"), Authorize(Policy = Permissions.AdminUsersManage)]
    public async Task<IActionResult> DeleteMapping(Guid id)
    {
        await manage.DeleteMappingAsync(id);
        return NoContent();
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> src)
    {
        foreach (var x in src) { yield return x; await Task.CompletedTask; }
    }
}
