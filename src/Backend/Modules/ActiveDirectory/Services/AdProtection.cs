using System.Text.RegularExpressions;
using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers;

namespace ServiceDashboard.Modules.ActiveDirectory.Services;

/// <summary>
/// Allowlists and protected objects. The backend applies these to fresh data on every change,
/// so sending a request straight to the API cannot get around them.
/// </summary>
public static partial class AdProtection
{
    /// <summary>Groups that can never be changed in Version 1.</summary>
    public static readonly string[] BuiltInProtectedGroups =
    [
        "Domain Admins", "Enterprise Admins", "Schema Admins", "Administrators", "Account Operators",
        "Backup Operators", "Server Operators", "Print Operators",
    ];

    // OU names that always mean "admin" or "service" tiers. Deliberately broad: blocking too much is the safe failure.
    [GeneratedRegex(@"tier[\s_-]*0|admin|service[\s_-]*account|^svc\b|privileged", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveOuName();

    // ---------- groups ----------

    public static bool IsGroupProtected(DirectoryGroup g, AdSettings s) =>
        g.AdminCount
        || BuiltInProtectedGroups.Contains(g.Name, StringComparer.OrdinalIgnoreCase)
        || s.ProtectedGroups.Any(p => string.Equals(p, g.Name, StringComparison.OrdinalIgnoreCase) || DnText.Equal(p, g.Dn));

    public static bool IsGroupOnAllowlist(DirectoryGroup g, AdSettings s) => s.ManageableGroups.Any(a => DnText.Equal(a, g.Dn));

    /// <summary>Null when the group may be added to or removed from; otherwise the reason it may not.</summary>
    public static string? GroupBlockReason(DirectoryGroup g, AdSettings s)
    {
        if (IsGroupProtected(g, s)) return $"'{g.Name}' is a protected group and cannot be changed in this application.";
        if (!IsGroupOnAllowlist(g, s)) return $"'{g.Name}' is not on the manageable groups allowlist.";
        return null;
    }

    // ---------- OUs ----------

    /// <summary>Why an OU (or the OU an object sits in) is always off limits, or null.</summary>
    public static string? OuBlockReason(string ouDn, AdSettings s, string baseDn)
    {
        if (string.IsNullOrWhiteSpace(ouDn)) return "The OU is unknown.";
        var rdns = DnText.SplitRdns(ouDn);

        if (rdns.Any(r => r.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) && r[3..].Trim().Equals("Domain Controllers", StringComparison.OrdinalIgnoreCase)))
            return "Domain Controllers cannot be changed or moved.";
        if (!rdns[0].StartsWith("OU=", StringComparison.OrdinalIgnoreCase) && !rdns.Any(r => r.StartsWith("OU=", StringComparison.OrdinalIgnoreCase)))
            return "Objects in built-in containers (CN=...) cannot be changed or moved.";
        if (rdns.Any(r => r.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) && SensitiveOuName().IsMatch(r[3..])))
            return "Admin, Tier 0 and service account OUs are always blocked.";
        if (s.ProtectedOus.Any(p => DnText.IsUnderOrEqual(ouDn, p)))
            return "This OU is on the protected OUs list.";
        return null;
    }

    /// <summary>Null when objects in this OU may be changed or moved into it; otherwise the reason.</summary>
    public static string? OuUseReason(string ouDn, IEnumerable<string> allowlist, AdSettings s, string baseDn)
    {
        var blocked = OuBlockReason(ouDn, s, baseDn);
        if (blocked != null) return blocked;
        return allowlist.Any(a => DnText.IsUnderOrEqual(ouDn, a)) ? null : "This OU is not on the manageable OU allowlist.";
    }

    public static IEnumerable<string> AllowlistFor(DirectoryObjectKind kind, AdSettings s) =>
        kind == DirectoryObjectKind.Computer ? s.ManageableComputerOus : s.ManageableUserOus;
}

/// <summary>
/// The extra limit a role puts on what its holders may manage, on top of the global allowlists and protected objects
/// (which always still apply). A null list in the scope means the role adds no limit.
/// </summary>
public static class AdScopeRules
{
    public static string? OuReason(AdScope scope, DirectoryObjectKind kind, string ouDn)
    {
        var allowed = kind == DirectoryObjectKind.Computer ? scope.ComputerOus : scope.UserOus;
        if (allowed == null) return null;
        return allowed.Any(a => DnText.IsUnderOrEqual(ouDn, a)) ? null : "Your role is not allowed to manage objects in this OU.";
    }

    public static string? GroupReason(AdScope scope, string groupDn) =>
        scope.Groups == null || scope.Groups.Any(g => DnText.Equal(g, groupDn)) ? null : "Your role is not allowed to manage this group.";
}
