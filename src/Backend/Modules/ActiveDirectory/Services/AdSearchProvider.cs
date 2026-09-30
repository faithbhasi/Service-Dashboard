using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers;
using ServiceDashboard.Services;

namespace ServiceDashboard.Modules.ActiveDirectory.Services;

/// <summary>Global search for the AD module: users, computers and groups in parallel, top 5 of each. Escaping happens in the provider.</summary>
public sealed class AdSearchProvider(AdDirectoryService ad, AdSettingsService settings) : IModuleSearchProvider
{
    public const int PerCategory = 5;
    public string ModuleId => ActiveDirectoryModule.Id;

    public async Task<IReadOnlyList<SearchCategory>> SearchAsync(string query, CurrentUserInfo user, CancellationToken ct)
    {
        await settings.GetAsync(); // warm the settings cache once so the parallel searches below never touch the database context together

        var tasks = new List<Task<SearchCategory>>();
        var encoded = Uri.EscapeDataString(query);
        if (user.Has(Permissions.AdUsersRead)) tasks.Add(Users(query, encoded, ct));
        if (user.Has(Permissions.AdComputersRead)) tasks.Add(Computers(query, encoded, ct));
        if (user.Has(Permissions.AdGroupsRead)) tasks.Add(Groups(query, encoded, ct));
        return await Task.WhenAll(tasks);
    }

    private async Task<SearchCategory> Users(string q, string encoded, CancellationToken ct)
    {
        var r = await ad.SearchUsersAsync(q, UserFilter.All, 1, PerCategory, null, ct);
        return new SearchCategory("users", "Users", r.Items.Select(u => new SearchResultItem(
            u.Id.ToString(), u.DisplayName ?? u.SamAccountName,
            $"{u.SamAccountName} - {OuLabel(u.Ou)}",
            Tags(u.Enabled ? null : "Disabled", u.LockedOut ? "Locked" : null),
            $"/ad/users/{u.Id}")).ToList(), r.Total, r.TotalIsCapped, $"/ad/users?q={encoded}");
    }

    private async Task<SearchCategory> Computers(string q, string encoded, CancellationToken ct)
    {
        var r = await ad.SearchComputersAsync(q, ComputerFilter.All, 1, PerCategory, null, ct);
        return new SearchCategory("computers", "Computers", r.Items.Select(c => new SearchResultItem(
            c.Id.ToString(), c.Name, $"{c.DnsHostName ?? c.OperatingSystem} - {OuLabel(c.Ou)}",
            Tags(c.Enabled ? null : "Disabled"), $"/ad/computers/{c.Id}")).ToList(), r.Total, r.TotalIsCapped, $"/ad/computers?q={encoded}");
    }

    private async Task<SearchCategory> Groups(string q, string encoded, CancellationToken ct)
    {
        var r = await ad.SearchGroupsAsync(q, 1, PerCategory, ct);
        return new SearchCategory("groups", "Groups", r.Items.Select(g => new SearchResultItem(
            g.Id.ToString(), g.Name, g.Description, Tags(g.IsProtected ? "Protected" : null), $"/ad/groups/{g.Id}")).ToList(),
            r.Total, r.TotalIsCapped, $"/ad/groups?q={encoded}");
    }

    private static string[] Tags(params string?[] tags) => tags.Where(t => t != null).Select(t => t!).ToArray();

    private static string OuLabel(string dn) =>
        string.Join(" / ", DnText.SplitRdns(dn).Where(r => r.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) || r.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            .Select(r => r[3..]).Reverse());
}
