namespace ServiceDashboard.Models;

public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Okta "sub" claim (or "dev|name" for development sign-in).</summary>
    public string Subject { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSignInUtc { get; set; }
    /// <summary>Okta groups seen at the last sign-in (JSON array). Kept out of the cookie to keep it small.</summary>
    public string OktaGroupsJson { get; set; } = "[]";
    public string ThemePreference { get; set; } = "system"; // light | dark | system
    public bool NavCollapsed { get; set; }
    public List<UserRole> UserRoles { get; set; } = [];
}

public class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<RolePermission> Permissions { get; set; } = [];
}

public class RolePermission
{
    public Guid RoleId { get; set; }
    public string Permission { get; set; } = "";
}

public class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public AppUser? User { get; set; }
    public Role? Role { get; set; }
}

public class GroupMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OktaGroup { get; set; } = "";
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}

/// <summary>Runtime settings edited in the UI: one JSON document per key.</summary>
public class SettingEntry
{
    public string Key { get; set; } = "";
    public string Json { get; set; } = "{}";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}

public class LogoAsset
{
    /// <summary>logoLight, logoDark or favicon.</summary>
    public string Kind { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public enum AuditCategory { Logon = 1, Access = 2, Admin = 3 }

public static class AuditResult
{
    public const string Success = "Success";
    public const string Failure = "Failure";
    public const string Denied = "Denied";
    public const string Validated = "Validated (no change made)";
}

/// <summary>Append-only. The application never updates rows and only deletes them through the retention job.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public AuditCategory Category { get; set; }
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public string Action { get; set; } = "";
    public string? Module { get; set; }
    public string? Target { get; set; }
    /// <summary>Stable identifier of the target (for AD objects the objectGUID).</summary>
    public string? TargetId { get; set; }
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
    public string Result { get; set; } = AuditResult.Success;
    public string? Error { get; set; }
    public string? Justification { get; set; }
    public string? TicketNumber { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? CorrelationId { get; set; }
}

/// <summary>
/// Thrown by a module when the system it talks to (for example a domain controller) cannot be reached.
/// SafeMessage is shown to the user; the inner exception is only logged.
/// </summary>
public class ModuleUnavailableException(string safeMessage, Exception? inner = null) : Exception(safeMessage, inner)
{
    public string SafeMessage { get; } = safeMessage;
}
