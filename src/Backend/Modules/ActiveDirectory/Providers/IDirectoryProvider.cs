using System.Security;

namespace ServiceDashboard.Modules.ActiveDirectory.Providers;

// The only contract the rest of the application uses to talk to a directory.
// Controllers and services must never reference LDAP or PowerShell types; an architecture test enforces that.

public enum DirectoryObjectKind { User, Computer, Group }
public enum UserFilter { All, Locked, Disabled, Enabled, AccountExpired }
public enum ComputerFilter { All, Enabled, Disabled }
public enum MemberKind { User, Computer, Group }

/// <summary>Attribute names that admins can configure in Settings.</summary>
public sealed record DirectoryReadOptions(string EmployeeIdAttribute = "employeeID", string? ComputerLastUserAttribute = null);

public sealed record ObjectRef(Guid Id, string Name, DirectoryObjectKind Kind);

public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public int Total { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; }
    /// <summary>True when the directory had more matches than the configured search limit, so Total is a lower bound.</summary>
    public bool TotalIsCapped { get; init; }
}

public sealed record UserSearch(string? Text, UserFilter Filter, int Page, int PageSize, string? OuDn, int Limit, DirectoryReadOptions Options);
public sealed record ComputerSearch(string? Text, ComputerFilter Filter, int Page, int PageSize, string? OuDn, int Limit, DirectoryReadOptions Options);
public sealed record GroupSearch(string? Text, int Page, int PageSize, int Limit);
public sealed record MemberSearch(string? Text, MemberKind? Kind, int Page, int PageSize);

public sealed class UacFlagInfo
{
    public required string Name { get; init; }
    public required string Meaning { get; init; }
}

public sealed class DirectoryUser
{
    public Guid Id { get; init; }
    public string Dn { get; init; } = "";
    public string Ou { get; init; } = "";
    public string SamAccountName { get; init; } = "";
    public string? UserPrincipalName { get; init; }
    public string? DisplayName { get; init; }
    public string? GivenName { get; init; }
    public string? Surname { get; init; }
    public string? Email { get; init; }
    public string? EmployeeId { get; init; }
    public string? Title { get; init; }
    public string? Department { get; init; }
    public string? Office { get; init; }
    public string? Phone { get; init; }
    public string? Mobile { get; init; }
    public string? Description { get; init; }
    public ObjectRef? Manager { get; init; }

    public bool Enabled { get; init; }
    public bool LockedOut { get; init; }
    /// <summary>Never, Expires or Expired.</summary>
    public string AccountExpiry { get; init; } = "Never";
    public DateTime? AccountExpiresUtc { get; init; }
    /// <summary>Expires, Expired, NeverExpires, MustChange or Unknown.</summary>
    public string PasswordStatus { get; init; } = "Unknown";
    public DateTime? PasswordExpiresUtc { get; init; }
    public DateTime? PasswordLastSetUtc { get; init; }
    /// <summary>lastLogonTimestamp: replicates with a delay of up to about 14 days.</summary>
    public DateTime? LastLogonUtc { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public DateTime? ChangedUtc { get; init; }
    /// <summary>Name of the fine-grained password policy from msDS-ResultantPSO, or null for the default domain policy.</summary>
    public string? ResultantPso { get; init; }
    public int UserAccountControl { get; init; }
    public IReadOnlyList<UacFlagInfo> UacFlags { get; init; } = [];
    public int PrimaryGroupId { get; init; }
    public bool AdminCount { get; init; }
}

public sealed class DirectoryComputer
{
    public Guid Id { get; init; }
    public string Dn { get; init; } = "";
    public string Ou { get; init; } = "";
    public string Name { get; init; } = "";
    public string? DnsHostName { get; init; }
    public bool Enabled { get; init; }
    public string? OperatingSystem { get; init; }
    public string? OsVersion { get; init; }
    public DateTime? LastLogonUtc { get; init; }
    public DateTime? PasswordLastSetUtc { get; init; }
    public ObjectRef? ManagedBy { get; init; }
    public string? Description { get; init; }
    public DateTime? ChangedUtc { get; init; }
    public DateTime? CreatedUtc { get; init; }
    /// <summary>Value of the attribute configured as "last logged-in user"; null when none is configured or it is empty.</summary>
    public string? LastLoggedInUser { get; init; }
    public bool AdminCount { get; init; }
}

public sealed class DirectoryGroup
{
    public Guid Id { get; init; }
    public string Dn { get; init; } = "";
    public string Ou { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    /// <summary>Global, DomainLocal or Universal.</summary>
    public string Scope { get; init; } = "Global";
    /// <summary>Security or Distribution.</summary>
    public string Type { get; init; } = "Security";
    public ObjectRef? ManagedBy { get; init; }
    public bool AdminCount { get; init; }
    public int? MemberCount { get; init; }
    public int? PrimaryGroupToken { get; init; }
}

public sealed record NestedMembership(DirectoryGroup Group, string? Via);

public sealed class Memberships
{
    public IReadOnlyList<DirectoryGroup> Direct { get; init; } = [];
    public IReadOnlyList<NestedMembership> Nested { get; init; } = [];
    public DirectoryGroup? Primary { get; init; }
}

public sealed class GroupMember
{
    public Guid Id { get; init; }
    public MemberKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string? SamAccountName { get; init; }
    public string? Email { get; init; }
    public string Dn { get; init; } = "";
    public bool? Enabled { get; init; }
}

public sealed record DirectoryOu(string Dn, string Name, bool HasChildren);

public sealed record DirectoryChange(string Field, string? From, string? To);
public sealed record DryRunCheck(string Name, bool Passed, string? Detail = null);

public static class DirectoryErrors
{
    public const string NotFound = "NotFound";
    public const string PermissionDenied = "PermissionDenied";
    public const string PasswordRejected = "PasswordRejected";
    public const string AlreadyMember = "AlreadyMember";
    public const string NotMember = "NotMember";
    public const string Constraint = "ConstraintViolation";
    public const string Unknown = "Unknown";
}

/// <summary>Outcome of a write or a dry-run. Messages never contain passwords.</summary>
public sealed class DirectoryResult
{
    public bool Success { get; init; }
    public bool DryRun { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<DirectoryChange> Changes { get; init; } = [];
    public IReadOnlyList<DryRunCheck> Checks { get; init; } = [];

    public static DirectoryResult Ok(bool dryRun, IEnumerable<DirectoryChange>? changes = null, IEnumerable<DryRunCheck>? checks = null, string? message = null) =>
        new() { Success = true, DryRun = dryRun, Changes = changes?.ToList() ?? [], Checks = checks?.ToList() ?? [], Message = message };

    public static DirectoryResult Fail(bool dryRun, string code, string message, IEnumerable<DryRunCheck>? checks = null) =>
        new() { Success = false, DryRun = dryRun, ErrorCode = code, Message = message, Checks = checks?.ToList() ?? [] };
}

public sealed record ResetPasswordOptions(bool MustChangeAtNextSignIn, bool UnlockAccount);

public sealed record ConnectionStep(string Name, bool Passed, string Detail);
public sealed record ConnectionTestResult(bool Success, IReadOnlyList<ConnectionStep> Steps);

/// <summary>The directory could not be reached. Mapped to a 503 by the global exception handler.</summary>
public sealed class DirectoryUnavailableException(string message, Exception? inner = null)
    : ServiceDashboard.Models.ModuleUnavailableException("The directory server could not be reached. Try again shortly.", inner)
{
    public string Technical { get; } = message;
}

/// <summary>Allowlists a provider can suggest for a brand new install. Only the Fake provider does, so local development works out of the box.</summary>
public sealed record SuggestedAllowlists(
    IReadOnlyList<string> UserOus, IReadOnlyList<string> ComputerOus, IReadOnlyList<string> ManageableGroups, IReadOnlyList<string> ProtectedGroups);

public interface IDirectoryProvider
{
    /// <summary>Starting values for the AD settings on a fresh database, or null (real directories start locked down).</summary>
    SuggestedAllowlists? SuggestedDefaults { get; }

    /// <summary>Name of the implementation ("Ldap" or "Fake"), shown in Settings.</summary>
    string ProviderName { get; }
    /// <summary>Distinguished name of the domain root, for example "DC=example,DC=test".</summary>
    string BaseDn { get; }

    // ---- reads ----
    Task<PagedResult<DirectoryUser>> SearchUsersAsync(UserSearch search, CancellationToken ct = default);
    Task<DirectoryUser?> GetUserAsync(Guid id, DirectoryReadOptions options, CancellationToken ct = default);
    Task<PagedResult<DirectoryComputer>> SearchComputersAsync(ComputerSearch search, CancellationToken ct = default);
    Task<DirectoryComputer?> GetComputerAsync(Guid id, DirectoryReadOptions options, CancellationToken ct = default);
    Task<PagedResult<DirectoryGroup>> SearchGroupsAsync(GroupSearch search, CancellationToken ct = default);
    Task<DirectoryGroup?> GetGroupAsync(Guid id, CancellationToken ct = default);
    /// <summary>Used to resolve the manageable-groups allowlist (which stores DNs) into groups.</summary>
    Task<DirectoryGroup?> GetGroupByDnAsync(string dn, CancellationToken ct = default);
    /// <summary>Direct, nested and primary group memberships of a user or computer.</summary>
    Task<Memberships?> GetMembershipsAsync(Guid objectId, DirectoryObjectKind kind, CancellationToken ct = default);
    /// <summary>Members of a group, filtered and paged by the directory itself (never loads the whole member list).</summary>
    Task<PagedResult<GroupMember>> SearchGroupMembersAsync(Guid groupId, MemberSearch search, CancellationToken ct = default);
    Task<IReadOnlyList<DirectoryOu>> BrowseOusAsync(string? parentDn, CancellationToken ct = default);
    Task<IReadOnlyList<DirectoryOu>> SearchOusAsync(string text, int limit, CancellationToken ct = default);
    Task<DirectoryOu?> GetOuAsync(string dn, CancellationToken ct = default);
    Task<long> CountUsersAsync(UserFilter filter, CancellationToken ct = default);
    Task<long> CountComputersAsync(ComputerFilter filter, CancellationToken ct = default);

    // ---- writes: every one supports a dry run that changes nothing ----
    Task<DirectoryResult> ResetPasswordAsync(Guid userId, SecureString newPassword, ResetPasswordOptions options, bool dryRun, CancellationToken ct = default);
    Task<DirectoryResult> UnlockAsync(Guid userId, bool dryRun, CancellationToken ct = default);
    Task<DirectoryResult> SetEnabledAsync(Guid objectId, DirectoryObjectKind kind, bool enabled, bool dryRun, CancellationToken ct = default);
    Task<DirectoryResult> MoveAsync(Guid objectId, DirectoryObjectKind kind, string targetOuDn, bool dryRun, CancellationToken ct = default);
    Task<DirectoryResult> AddToGroupsAsync(Guid memberId, IReadOnlyList<Guid> groupIds, bool dryRun, CancellationToken ct = default);
    Task<DirectoryResult> RemoveFromGroupsAsync(Guid memberId, IReadOnlyList<Guid> groupIds, bool dryRun, CancellationToken ct = default);

    Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default);
}
