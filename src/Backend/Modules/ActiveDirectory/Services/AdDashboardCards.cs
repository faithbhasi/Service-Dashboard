using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ServiceDashboard.Configuration;
using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers;
using ServiceDashboard.Services;

namespace ServiceDashboard.Modules.ActiveDirectory.Services;

/// <summary>
/// Home cards for AD. Counting can mean walking a lot of directory objects, so the counts are cached for a short,
/// configurable time (App:DashboardCacheMinutes) and the cards show when they were last updated.
/// </summary>
public sealed class AdDashboardCards(IDirectoryProvider provider, IMemoryCache cache, IOptions<AppOptions> app) : IDashboardCardProvider
{
    private sealed record Counts(long Locked, long Disabled, long Expired, long DisabledComputers, DateTime UpdatedUtc);

    // One refresh at a time across all requests, so a burst of dashboard loads runs the directory queries once.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const string CacheKey = "dashboard:ad:counts";

    public string ModuleId => ActiveDirectoryModule.Id;

    public async Task<IReadOnlyList<DashboardCard>> GetCardsAsync(CurrentUserInfo user, CancellationToken ct)
    {
        var wantsUsers = user.Has(Permissions.AdUsersRead);
        var wantsComputers = user.Has(Permissions.AdComputersRead);
        if (!wantsUsers && !wantsComputers) return [];

        var c = await GetCountsAsync(ct);
        var cards = new List<DashboardCard>();
        if (wantsUsers)
        {
            // "Locked" is confirmed with msDS-User-Account-Control-Computed by the provider, not just an old lockoutTime.
            cards.Add(new("lockedUsers", "Locked users", c.Locked, "/ad/users?filter=Locked", "warning", c.UpdatedUtc));
            cards.Add(new("disabledUsers", "Disabled users", c.Disabled, "/ad/users?filter=Disabled", "neutral", c.UpdatedUtc));
            cards.Add(new("expiredUsers", "Expired user accounts", c.Expired, "/ad/users?filter=AccountExpired", "warning", c.UpdatedUtc));
        }
        if (wantsComputers)
            cards.Add(new("disabledComputers", "Disabled computers", c.DisabledComputers, "/ad/computers?filter=Disabled", "neutral", c.UpdatedUtc));
        return cards;
    }

    private async Task<Counts> GetCountsAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out Counts? hit) && hit != null) return hit;
        await Gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(CacheKey, out hit) && hit != null) return hit;
            var counts = new Counts(
                await provider.CountUsersAsync(UserFilter.Locked, ct),
                await provider.CountUsersAsync(UserFilter.Disabled, ct),
                await provider.CountUsersAsync(UserFilter.AccountExpired, ct),
                await provider.CountComputersAsync(ComputerFilter.Disabled, ct),
                DateTime.UtcNow);
            if (app.Value.DashboardCacheMinutes > 0) cache.Set(CacheKey, counts, TimeSpan.FromMinutes(app.Value.DashboardCacheMinutes));
            return counts;
        }
        finally { Gate.Release(); }
    }
}
