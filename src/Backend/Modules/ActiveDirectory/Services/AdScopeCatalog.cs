using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers;
using ServiceDashboard.Services;

namespace ServiceDashboard.Modules.ActiveDirectory.Services;

/// <summary>The OUs and groups a role's scope can be chosen from: the global manageable lists on Settings > AD Integration.</summary>
public sealed class AdScopeCatalog(AdSettingsService settings, IDirectoryProvider provider, ICurrentUser currentUser, AdDirectoryService directory) : IAdScopeCatalog
{
    public async Task<AdScopeOptions> GetOptionsAsync()
    {
        var s = await settings.GetAsync();
        // Only what the signed-in person could hand out themselves (a limited manager cannot give a role more than their own reach).
        var mine = (await currentUser.GetAsync())?.AdScope ?? AdScope.Unrestricted;
        var groups = new List<AdScopeOption>();
        foreach (var dn in s.ManageableGroups.Take(300).Where(d => AdScopeRules.GroupReason(mine, d) == null))
        {
            var g = await provider.GetGroupByDnAsync(dn);
            groups.Add(new AdScopeOption(dn, g?.Name ?? OuLabel(dn)));
        }
        return new AdScopeOptions(
            s.ManageableUserOus.Where(d => AdScopeRules.OuReason(mine, DirectoryObjectKind.User, d) == null).Select(d => new AdScopeOption(d, OuLabel(d))).OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase).ToList(),
            s.ManageableComputerOus.Where(d => AdScopeRules.OuReason(mine, DirectoryObjectKind.Computer, d) == null).Select(d => new AdScopeOption(d, OuLabel(d))).OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase).ToList(),
            groups.OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static DirectoryObjectKind? KindOf(string kind) =>
        kind.Equals("users", StringComparison.OrdinalIgnoreCase) ? DirectoryObjectKind.User
        : kind.Equals("computers", StringComparison.OrdinalIgnoreCase) ? DirectoryObjectKind.Computer : null;

    /// <summary>
    /// The real OU tree, lazily, so every OU can be ticked. It is the same tree and the same rules as Move OU (the manageable lists,
    /// protected OUs, and what the signed-in person can manage themselves), so there is one place where "may this OU be used" is decided.
    /// </summary>
    public async Task<IReadOnlyList<AdScopeOuNode>> BrowseOusAsync(string kind, string? parentDn, string? search)
    {
        var k = KindOf(kind) ?? throw new ApiException(400, "Unknown kind", "Use users or computers.", "validation");
        var nodes = await directory.BrowseOusAsync(parentDn, k, search, CancellationToken.None);
        return nodes.Select(n => new AdScopeOuNode(n.Dn, n.Name, n.HasChildren, n.Allowed, n.Reason)).ToList();
    }

    public async Task<bool> IsSelectableAsync(string kind, string dn)
    {
        if (string.IsNullOrWhiteSpace(dn) || !DnText.IsValidDn(dn)) return false;
        var s = await settings.GetAsync();
        if (kind.Equals("groups", StringComparison.OrdinalIgnoreCase)) return s.ManageableGroups.Any(g => DnText.Equal(g, dn));
        if (KindOf(kind) is not { } k) return false;
        if (!DnText.IsUnderOrEqual(dn, provider.BaseDn)) return false;
        if (AdProtection.OuUseReason(dn, AdProtection.AllowlistFor(k, s), s, provider.BaseDn) != null) return false;
        return await provider.GetOuAsync(dn) != null; // the OU must exist: a made-up name under an allowed OU manages nothing
    }

    /// <summary>"OU=Sales,OU=Staff,OU=Corp,DC=x" -> "Corp / Staff / Sales".</summary>
    private static string OuLabel(string dn)
    {
        var parts = DnText.SplitRdns(dn).Where(p => p.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) || p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[3..]).Reverse().ToList();
        return parts.Count == 0 ? dn : string.Join(" / ", parts);
    }
}
