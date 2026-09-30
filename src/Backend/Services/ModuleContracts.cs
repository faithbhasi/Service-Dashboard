namespace ServiceDashboard.Services;

// What a module can contribute to the core application. A module registers an implementation of each one it supports;
// the core never needs to change.

public sealed record SearchResultItem(string Id, string Title, string? Subtitle, string[] Tags, string Route);

public sealed record SearchCategory(string Key, string Label, IReadOnlyList<SearchResultItem> Items, int Total, bool TotalIsCapped, string SeeAllRoute);

public interface IModuleSearchProvider
{
    string ModuleId { get; }
    /// <summary>Only categories the user may read are searched and returned. Input has already been length-checked but NOT escaped.</summary>
    Task<IReadOnlyList<SearchCategory>> SearchAsync(string query, CurrentUserInfo user, CancellationToken ct);
}

public sealed record DashboardCard(string Key, string Title, long Count, string Route, string Tone, DateTime UpdatedUtc);

public interface IDashboardCardProvider
{
    string ModuleId { get; }
    Task<IReadOnlyList<DashboardCard>> GetCardsAsync(CurrentUserInfo user, CancellationToken ct);
}
