using System.Text.Json;

namespace ServiceDashboard.Models;

/// <summary>
/// What a role may manage in Active Directory, inside the global allowlists on Settings > AD Integration (which stay the ceiling).
/// A null list means "no extra limit from this role"; an empty list means "nothing". Entries are distinguished names.
/// </summary>
public sealed class AdScope
{
    /// <summary>OUs whose users this role may change.</summary>
    public List<string>? UserOus { get; set; }
    public List<string>? ComputerOus { get; set; }
    /// <summary>Groups users may be added to or removed from by this role.</summary>
    public List<string>? Groups { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUnrestricted => UserOus == null && ComputerOus == null && Groups == null;
    [System.Text.Json.Serialization.JsonIgnore]
    public static AdScope Unrestricted => new();

    public static AdScope Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AdScope();
        try { return JsonSerializer.Deserialize<AdScope>(json, Options) ?? new AdScope(); }
        catch (JsonException) { return new AdScope(); }
    }

    public string? ToJson() => IsUnrestricted ? null : JsonSerializer.Serialize(this, Options);

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

/// <summary>Implemented by the Active Directory module so the core can check a role's scope against the global allowlists.</summary>
public interface IAdScopeCatalog
{
    /// <summary>The entries a role scope can be chosen from: the global manageable OUs and groups.</summary>
    Task<AdScopeOptions> GetOptionsAsync();

    /// <summary>Children of an OU (or the domain root), or OUs matching <paramref name="search"/>, for choosing what a role may manage.</summary>
    Task<IReadOnlyList<AdScopeOuNode>> BrowseOusAsync(string kind, string? parentDn, string? search);

    /// <summary>True when a role scope entry of this kind ("users", "computers" or "groups") may be chosen: it is inside the global manageable lists.</summary>
    Task<bool> IsSelectableAsync(string kind, string dn);
}

public sealed record AdScopeOuNode(string Dn, string Name, bool HasChildren, bool Selectable, string? Reason);

public sealed record AdScopeOption(string Dn, string Label);
public sealed record AdScopeOptions(IReadOnlyList<AdScopeOption> UserOus, IReadOnlyList<AdScopeOption> ComputerOus, IReadOnlyList<AdScopeOption> Groups);
