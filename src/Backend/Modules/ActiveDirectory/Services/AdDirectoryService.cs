using ServiceDashboard.Modules.ActiveDirectory.Providers;

namespace ServiceDashboard.Modules.ActiveDirectory.Services;

public sealed record GroupDto(
    Guid Id, string Dn, string Ou, string Name, string? Description, string Scope, string Type, ObjectRef? ManagedBy,
    int? MemberCount, bool IsProtected, bool IsManageable, string? BlockReason)
{
    public static GroupDto From(DirectoryGroup g, AdSettings s) => new(
        g.Id, g.Dn, g.Ou, g.Name, g.Description, g.Scope, g.Type, g.ManagedBy, g.MemberCount,
        AdProtection.IsGroupProtected(g, s), AdProtection.GroupBlockReason(g, s) == null, AdProtection.GroupBlockReason(g, s));
}

public sealed record NestedGroupDto(GroupDto Group, string? Via);
public sealed record MembershipsDto(IReadOnlyList<GroupDto> Direct, IReadOnlyList<NestedGroupDto> Nested, GroupDto? Primary);
public sealed record UserDetail(DirectoryUser User, bool OuManageable, string? OuReason);
public sealed record ComputerDetail(DirectoryComputer Computer, bool OuManageable, string? OuReason);
public sealed record OuNode(string Dn, string Name, bool HasChildren, bool Allowed, string? Reason);

/// <summary>Read side of the AD module: talks to IDirectoryProvider and annotates results with allowlist and protection state.</summary>
public sealed class AdDirectoryService(IDirectoryProvider provider, AdSettingsService settings)
{
    public IDirectoryProvider Provider => provider;

    private static int Clamp(int v, int min, int max) => Math.Min(Math.Max(v, min), max);

    public async Task<PagedResult<DirectoryUser>> SearchUsersAsync(string? q, UserFilter filter, int page, int pageSize, string? ou, CancellationToken ct)
    {
        var s = await settings.GetAsync();
        return await provider.SearchUsersAsync(new UserSearch(q, filter, Math.Max(1, page), Clamp(pageSize, 1, 200), ValidOu(ou),
            Clamp(s.SearchResultLimit, 50, 5000), s.ReadOptions), ct);
    }

    public async Task<UserDetail?> GetUserAsync(Guid id, CancellationToken ct)
    {
        var s = await settings.GetAsync();
        var u = await provider.GetUserAsync(id, s.ReadOptions, ct);
        if (u == null) return null;
        var reason = AdProtection.OuUseReason(u.Ou, s.ManageableUserOus, s, provider.BaseDn);
        return new UserDetail(u, reason == null, reason);
    }

    public async Task<PagedResult<DirectoryComputer>> SearchComputersAsync(string? q, ComputerFilter filter, int page, int pageSize, string? ou, CancellationToken ct)
    {
        var s = await settings.GetAsync();
        return await provider.SearchComputersAsync(new ComputerSearch(q, filter, Math.Max(1, page), Clamp(pageSize, 1, 200), ValidOu(ou),
            Clamp(s.SearchResultLimit, 50, 5000), s.ReadOptions), ct);
    }

    public async Task<ComputerDetail?> GetComputerAsync(Guid id, CancellationToken ct)
    {
        var s = await settings.GetAsync();
        var c = await provider.GetComputerAsync(id, s.ReadOptions, ct);
        if (c == null) return null;
        var reason = AdProtection.OuUseReason(c.Ou, s.ManageableComputerOus, s, provider.BaseDn);
        return new ComputerDetail(c, reason == null, reason);
    }

    public async Task<PagedResult<GroupDto>> SearchGroupsAsync(string? q, int page, int pageSize, CancellationToken ct)
    {
        var s = await settings.GetAsync();
        var r = await provider.SearchGroupsAsync(new GroupSearch(q, Math.Max(1, page), Clamp(pageSize, 1, 200), Clamp(s.SearchResultLimit, 50, 5000)), ct);
        return new PagedResult<GroupDto>
        {
            Items = r.Items.Select(g => GroupDto.From(g, s)).ToList(), Total = r.Total, Page = r.Page, PageSize = r.PageSize, TotalIsCapped = r.TotalIsCapped,
        };
    }

    public async Task<GroupDto?> GetGroupAsync(Guid id, CancellationToken ct)
    {
        var s = await settings.GetAsync();
        var g = await provider.GetGroupAsync(id, ct);
        return g == null ? null : GroupDto.From(g, s);
    }

    public async Task<MembershipsDto?> GetMembershipsAsync(Guid id, DirectoryObjectKind kind, CancellationToken ct)
    {
        var s = await settings.GetAsync();
        var m = await provider.GetMembershipsAsync(id, kind, ct);
        if (m == null) return null;
        return new MembershipsDto(
            m.Direct.Select(g => GroupDto.From(g, s)).ToList(),
            m.Nested.Select(n => new NestedGroupDto(GroupDto.From(n.Group, s), n.Via)).ToList(),
            m.Primary == null ? null : GroupDto.From(m.Primary, s));
    }

    public Task<PagedResult<GroupMember>> SearchMembersAsync(Guid groupId, string? q, MemberKind? kind, int page, int pageSize, CancellationToken ct) =>
        provider.SearchGroupMembersAsync(groupId, new MemberSearch(q, kind, Math.Max(1, page), Clamp(pageSize, 1, 500)), ct);

    /// <summary>Lazy OU tree: children of <paramref name="parent"/> (or the domain root), marked with whether the object type may be moved there.</summary>
    public async Task<IReadOnlyList<OuNode>> BrowseOusAsync(string? parent, DirectoryObjectKind kind, string? search, CancellationToken ct)
    {
        var s = await settings.GetAsync();
        var list = string.IsNullOrWhiteSpace(search)
            ? await provider.BrowseOusAsync(ValidOu(parent), ct)
            : await provider.SearchOusAsync(search.Trim(), 50, ct);
        var allow = AdProtection.AllowlistFor(kind, s).ToList();
        return list.Select(o =>
        {
            var reason = AdProtection.OuUseReason(o.Dn, allow, s, provider.BaseDn);
            // A parent that is not itself allowed may still contain allowed children, so it stays browsable.
            return new OuNode(o.Dn, o.Name, o.HasChildren, reason == null, reason);
        }).ToList();
    }

    private string? ValidOu(string? dn) => !string.IsNullOrWhiteSpace(dn) && DnText.IsValidDn(dn) && DnText.IsUnderOrEqual(dn, provider.BaseDn) ? dn : null;
}
