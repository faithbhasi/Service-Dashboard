using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ServiceDashboard.Data;

namespace ServiceDashboard.Services;

public sealed record CurrentUserInfo(
    Guid Id, string Subject, string DisplayName, string Email, bool IsEnabled,
    string ThemePreference, bool NavCollapsed,
    IReadOnlyList<RoleRef> Roles, IReadOnlySet<string> Permissions)
{
    public bool HasAccess => Roles.Count > 0;
    public bool Has(string permission) => IsEnabled && Permissions.Contains(permission);
    public bool HasAny(params string[] permissions) => IsEnabled && permissions.Any(Permissions.Contains);
    public string Initials
    {
        get
        {
            var parts = DisplayName.Split([' ', '.', '@'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                0 => "?",
                1 => parts[0][..1].ToUpperInvariant(),
                _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant(),
            };
        }
    }
}

public interface ICurrentUser
{
    /// <summary>The signed-in app user with roles and permissions, read from the database once per request.</summary>
    Task<CurrentUserInfo?> GetAsync();
}

public sealed class CurrentUser(IHttpContextAccessor accessor, AppDbContext db, AccessService access) : ICurrentUser
{
    private Task<CurrentUserInfo?>? _cached;

    public Task<CurrentUserInfo?> GetAsync() => _cached ??= LoadAsync();

    private async Task<CurrentUserInfo?> LoadAsync()
    {
        var principal = accessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true) return null;
        if (!Guid.TryParse(principal.FindFirstValue(AuditService.UidClaim), out var id)) return null;

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return null;
        var resolved = await access.ResolveAsync(user);
        return new CurrentUserInfo(user.Id, user.Subject, user.DisplayName, user.Email, user.IsEnabled,
            user.ThemePreference, user.NavCollapsed, resolved.Roles, resolved.Permissions);
    }
}
