using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers;

namespace ServiceDashboard.Modules.ActiveDirectory.Services;

/// <summary>The OUs and groups a role's scope can be chosen from: the global manageable lists on Settings > AD Integration.</summary>
public sealed class AdScopeCatalog(AdSettingsService settings, IDirectoryProvider provider) : IAdScopeCatalog
{
    public async Task<AdScopeOptions> GetOptionsAsync()
    {
        var s = await settings.GetAsync();
        var groups = new List<AdScopeOption>();
        foreach (var dn in s.ManageableGroups.Take(300))
        {
            var g = await provider.GetGroupByDnAsync(dn);
            groups.Add(new AdScopeOption(dn, g?.Name ?? OuLabel(dn)));
        }
        return new AdScopeOptions(
            s.ManageableUserOus.Select(d => new AdScopeOption(d, OuLabel(d))).OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase).ToList(),
            s.ManageableComputerOus.Select(d => new AdScopeOption(d, OuLabel(d))).OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase).ToList(),
            groups.OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>"OU=Sales,OU=Staff,OU=Corp,DC=x" -> "Corp / Staff / Sales".</summary>
    private static string OuLabel(string dn)
    {
        var parts = DnText.SplitRdns(dn).Where(p => p.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) || p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[3..]).Reverse().ToList();
        return parts.Count == 0 ? dn : string.Join(" / ", parts);
    }
}
