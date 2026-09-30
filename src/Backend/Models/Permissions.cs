namespace ServiceDashboard.Models;

public sealed record PermissionInfo(string Id, string Group, string Description);

/// <summary>Every permission in the application. Each one is also an ASP.NET Core authorization policy.</summary>
public static class Permissions
{
    public const string DashboardRead = "dashboard.read";
    public const string AdUsersRead = "ad.users.read";
    public const string AdUsersResetPassword = "ad.users.resetPassword";
    public const string AdUsersUnlock = "ad.users.unlock";
    public const string AdUsersEnable = "ad.users.enable";
    public const string AdUsersDisable = "ad.users.disable";
    public const string AdUsersMove = "ad.users.move";
    public const string AdUsersGroupsAdd = "ad.users.groups.add";
    public const string AdUsersGroupsRemove = "ad.users.groups.remove";
    public const string AdComputersRead = "ad.computers.read";
    public const string AdComputersEnable = "ad.computers.enable";
    public const string AdComputersDisable = "ad.computers.disable";
    public const string AdComputersMove = "ad.computers.move";
    public const string AdGroupsRead = "ad.groups.read";
    public const string AdGroupsMemberExport = "ad.groups.member.export";
    public const string LogsReadOwn = "logs.read.own";
    public const string LogsRead = "logs.read";
    public const string LogsExport = "logs.export";
    public const string AdminUsersManage = "admin.users.manage";
    public const string AdminRolesManage = "admin.roles.manage";
    public const string SettingsRead = "settings.read";
    public const string SettingsManage = "settings.manage";
    public const string SettingsPersonalizationManage = "settings.personalization.manage";

    public static readonly IReadOnlyList<PermissionInfo> All =
    [
        new(DashboardRead, "Home", "View the Home dashboard cards."),
        new(AdUsersRead, "Active Directory - Users", "Search and view AD users and their memberships."),
        new(AdUsersResetPassword, "Active Directory - Users", "Reset an AD user's password."),
        new(AdUsersUnlock, "Active Directory - Users", "Unlock a locked AD user account."),
        new(AdUsersEnable, "Active Directory - Users", "Enable a disabled AD user account."),
        new(AdUsersDisable, "Active Directory - Users", "Disable an AD user account."),
        new(AdUsersMove, "Active Directory - Users", "Move an AD user to another allowed OU."),
        new(AdUsersGroupsAdd, "Active Directory - Users", "Add an AD user to manageable groups."),
        new(AdUsersGroupsRemove, "Active Directory - Users", "Remove an AD user from manageable groups."),
        new(AdComputersRead, "Active Directory - Computers", "Search and view AD computers."),
        new(AdComputersEnable, "Active Directory - Computers", "Enable a disabled AD computer account."),
        new(AdComputersDisable, "Active Directory - Computers", "Disable an AD computer account."),
        new(AdComputersMove, "Active Directory - Computers", "Move an AD computer to another allowed OU."),
        new(AdGroupsRead, "Active Directory - Groups", "View AD groups and their members."),
        new(AdGroupsMemberExport, "Active Directory - Groups", "Export a group's member list to CSV."),
        new(LogsReadOwn, "Activity and Logs", "View only the activity performed by yourself."),
        new(LogsRead, "Activity and Logs", "View all activity and logs."),
        new(LogsExport, "Activity and Logs", "Export activity and logs to CSV."),
        new(AdminUsersManage, "Users and Groups", "Manage app users and Okta group mappings."),
        new(AdminRolesManage, "Users and Groups", "Create, clone and edit roles."),
        new(SettingsRead, "Settings", "View settings."),
        new(SettingsManage, "Settings", "Change settings."),
        new(SettingsPersonalizationManage, "Settings", "Change logo, colours and product name."),
    ];

    public static readonly IReadOnlySet<string> AllIds = new HashSet<string>(All.Select(p => p.Id), StringComparer.Ordinal);
}

/// <summary>Policies that accept any one of several permissions.</summary>
public static class PermissionPolicies
{
    public const string LogsAccess = "any:logs.read|logs.read.own";
    public const string AdminAccess = "any:admin.users.manage|admin.roles.manage";
    public const string SettingsAccess = "any:settings.read|settings.manage|settings.personalization.manage";
    /// <summary>Browsing the OU tree: needed by the Move OU tabs and by the allowlist editor in Settings.</summary>
    public const string AdOuBrowse = "any:ad.users.move|ad.computers.move|settings.manage";

    public static readonly IReadOnlyDictionary<string, string[]> Combined = new Dictionary<string, string[]>
    {
        [LogsAccess] = [Permissions.LogsRead, Permissions.LogsReadOwn],
        [AdminAccess] = [Permissions.AdminUsersManage, Permissions.AdminRolesManage],
        [SettingsAccess] = [Permissions.SettingsRead, Permissions.SettingsManage, Permissions.SettingsPersonalizationManage],
        [AdOuBrowse] = [Permissions.AdUsersMove, Permissions.AdComputersMove, Permissions.SettingsManage],
    };
}

public static class DefaultRoles
{
    public static readonly Guid AdminsId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid AuditorsId = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    public static readonly Guid UsersId = Guid.Parse("00000000-0000-0000-0000-0000000000a3");

    public const string Admins = "Admins";
    public const string Auditors = "Auditors and Security";
    public const string Users = "Users";

    public static readonly string[] AuditorPermissions =
    [
        Permissions.DashboardRead, Permissions.AdUsersRead, Permissions.AdComputersRead, Permissions.AdGroupsRead, Permissions.AdGroupsMemberExport,
        Permissions.LogsRead, Permissions.LogsExport,
    ];

    // "Users: only the modules and actions assigned to them" - so the default is the bare minimum.
    public static readonly string[] UserPermissions = [Permissions.DashboardRead, Permissions.LogsReadOwn];
}
