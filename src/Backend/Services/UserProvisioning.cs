using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ServiceDashboard.Data;
using ServiceDashboard.Models;

namespace ServiceDashboard.Services;

public sealed record SignInOutcome(AppUser? User, string? DenialReason);

/// <summary>Creates or updates the app user at sign-in and builds the session principal (cookie contents).</summary>
public sealed class UserProvisioning(AppDbContext db, IAuditService audit)
{
    public const string SignInClaim = "sd_signin";
    public const string ActiveClaim = "sd_active";

    public async Task<SignInOutcome> SignInAsync(string subject, string? email, string? name, IEnumerable<string> oktaGroups)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Subject == subject);
        if (user == null)
        {
            user = new AppUser { Subject = subject, Email = email ?? "", DisplayName = name ?? email ?? subject };
            db.Users.Add(user);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(email)) user.Email = email;
            if (!string.IsNullOrWhiteSpace(name)) user.DisplayName = name;
        }
        user.OktaGroupsJson = JsonSerializer.Serialize(oktaGroups.Distinct(StringComparer.OrdinalIgnoreCase));

        if (!user.IsEnabled)
        {
            await db.SaveChangesAsync();
            await audit.WriteAsync(new AuditEntry
            {
                Category = AuditCategory.Logon, Action = "logon.denied", UserId = user.Id, UserName = user.DisplayName,
                Result = AuditResult.Denied, Error = "Access to this application is disabled for this user",
            });
            return new SignInOutcome(null, "disabled");
        }

        user.LastSignInUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.WriteAsync(new AuditEntry
        {
            Category = AuditCategory.Logon, Action = "logon.signin", UserId = user.Id, UserName = user.DisplayName,
        });
        return new SignInOutcome(user, null);
    }

    public static ClaimsPrincipal BuildPrincipal(AppUser user, string scheme)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var identity = new ClaimsIdentity(
        [
            new Claim(AuditService.UidClaim, user.Id.ToString()),
            new Claim("name", user.DisplayName),
            new Claim("email", user.Email),
            new Claim(SignInClaim, now),
            new Claim(ActiveClaim, now),
        ], scheme, "name", ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }
}
