using ServiceDashboard.Modules.ActiveDirectory.Providers;
using ServiceDashboard.Services;

namespace ServiceDashboard.Modules.ActiveDirectory.Services;

/// <summary>Runtime AD settings edited on Settings > AD Integration. Stored in SQLite under the key "ad".</summary>
public sealed class AdSettings
{
    /// <summary>OUs (DNs) whose users may be changed or moved. Empty means nothing is manageable.</summary>
    public List<string> ManageableUserOus { get; set; } = [];
    public List<string> ManageableComputerOus { get; set; } = [];
    /// <summary>OUs that are never manageable, even if they sit under an allowed OU.</summary>
    public List<string> ProtectedOus { get; set; } = [];
    /// <summary>Groups (DNs) that users may be added to or removed from.</summary>
    public List<string> ManageableGroups { get; set; } = [];
    /// <summary>Extra protected groups (DN or name), on top of the built-in list and adminCount=1.</summary>
    public List<string> ProtectedGroups { get; set; } = [];
    public string EmployeeIdAttribute { get; set; } = "employeeID";
    /// <summary>Attribute that holds a computer's last logged-in user (for example written by a logon script). Empty = not available.</summary>
    public string? ComputerLastUserAttribute { get; set; }
    public int SearchResultLimit { get; set; } = 1000;

    public DirectoryReadOptions ReadOptions => new(EmployeeIdAttribute, string.IsNullOrWhiteSpace(ComputerLastUserAttribute) ? null : ComputerLastUserAttribute);
}

public sealed class AdSettingsService(SettingsService settings, IDirectoryProvider provider)
{
    private AdSettings Defaults()
    {
        var s = new AdSettings();
        if (provider.SuggestedDefaults is { } d)
        {
            s.ManageableUserOus = [.. d.UserOus];
            s.ManageableComputerOus = [.. d.ComputerOus];
            s.ManageableGroups = [.. d.ManageableGroups];
            s.ProtectedGroups = [.. d.ProtectedGroups];
            s.ComputerLastUserAttribute = "description";
        }
        return s;
    }

    public Task<AdSettings> GetAsync() => settings.GetAsync(SettingKeys.ActiveDirectory, Defaults);

    public Task<(string Before, string After)> SaveAsync(AdSettings value, string? by) =>
        settings.SaveAsync(SettingKeys.ActiveDirectory, value, by, Defaults);
}
