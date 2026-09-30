using ServiceDashboard.Models;

namespace ServiceDashboard.Services;

/// <summary>A module registers one of these from its AddXxxModule() method. Module ids are lowercase and never change.</summary>
public sealed record ModuleDescriptor(string Id, string Name, string Description);

public sealed record ModuleStatus(string Id, string Name, string Description, string Status, bool Enabled);

public sealed class ModuleCatalog(IEnumerable<ModuleDescriptor> modules, SettingsService settings)
{
    /// <summary>Planned modules, shown in Settings and the navigation as "Coming Soon".</summary>
    public static readonly ModuleDescriptor[] ComingSoon =
    [
        new("okta", "Okta", "Okta user and group administration."),
        new("m365", "Microsoft 365", "Microsoft 365 licences and mailboxes."),
        new("mimecast", "Mimecast", "Mimecast email security."),
        new("citrix", "Citrix", "Citrix sessions and applications."),
    ];

    public async Task<bool> IsEnabledAsync(string moduleId)
    {
        if (!modules.Any(m => m.Id == moduleId)) return false;
        var s = await settings.GetModulesAsync();
        return !s.Enabled.TryGetValue(moduleId, out var on) || on;
    }

    public async Task<IReadOnlyList<ModuleStatus>> ListAsync()
    {
        var s = await settings.GetModulesAsync();
        var list = modules.Select(m =>
        {
            var on = !s.Enabled.TryGetValue(m.Id, out var v) || v;
            return new ModuleStatus(m.Id, m.Name, m.Description, on ? "Active" : "Disabled", on);
        }).ToList();
        list.AddRange(ComingSoon.Select(m => new ModuleStatus(m.Id, m.Name, m.Description, "Coming Soon", false)));
        return list;
    }
}
