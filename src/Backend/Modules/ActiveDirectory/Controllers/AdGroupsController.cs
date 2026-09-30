using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ServiceDashboard.Configuration;
using ServiceDashboard.Middleware;
using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers;
using ServiceDashboard.Modules.ActiveDirectory.Services;
using ServiceDashboard.Services;

namespace ServiceDashboard.Modules.ActiveDirectory.Controllers;

/// <summary>Groups are read-only except for their user members: add and remove go through the same change pipeline as the user's Groups tab.</summary>
[ApiController, ModuleGate(ActiveDirectoryModule.Id)]
[Route("api/modules/ad/groups")]
public sealed class AdGroupsController(AdDirectoryService ad, AdChangeService changes, IAuditService audit, IOptions<AppOptions> app) : ControllerBase
{
    [HttpGet, Authorize(Policy = Permissions.AdGroupsRead), EnableRateLimiting(RateLimitPolicies.Search)]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Ok(await ad.SearchGroupsAsync(q, page, pageSize, ct));

    [HttpGet("{id:guid}"), Authorize(Policy = Permissions.AdGroupsRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        await ad.GetGroupAsync(id, ct) is { } g ? Ok(g) : Problem(statusCode: 404, title: "Not found", detail: "Group not found in the directory.");

    /// <summary>Server-side member search and paging: the directory filters, this never loads the whole member list.</summary>
    [HttpGet("{id:guid}/members"), Authorize(Policy = Permissions.AdGroupsRead), EnableRateLimiting(RateLimitPolicies.Search)]
    public async Task<IActionResult> Members(Guid id, [FromQuery] string? q, [FromQuery] MemberKind? kind, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        Ok(await ad.SearchMembersAsync(id, q, kind, page, pageSize, ct));

    /// <summary>Users that could be added to this group, matching the search text, flagged when already a direct member.</summary>
    [HttpGet("{id:guid}/addable-users"), Authorize(Policy = Permissions.AdUsersGroupsAdd), EnableRateLimiting(RateLimitPolicies.Search)]
    public async Task<IActionResult> AddableUsers(Guid id, [FromQuery] string? q, CancellationToken ct = default)
    {
        var text = q?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length < 2) return Ok(Array.Empty<object>());
        var users = await ad.SearchUsersAsync(text, UserFilter.All, 1, 20, null, ct);
        var members = await ad.SearchMembersAsync(id, text, MemberKind.User, 1, 200, ct);
        var memberIds = members.Items.Select(m => m.Id).ToHashSet();
        return Ok(users.Items.Select(u => new { u.Id, name = u.DisplayName ?? u.SamAccountName, u.SamAccountName, u.Email, u.Enabled, alreadyMember = memberIds.Contains(u.Id) }));
    }

    [HttpPost("{id:guid}/members/add"), Authorize(Policy = Permissions.AdUsersGroupsAdd), EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> AddMembers(Guid id, [FromBody] GroupMembersRequest req, CancellationToken ct) =>
        Ok(new { results = await changes.ChangeGroupMembersAsync(id, add: true, req, ct) });

    [HttpPost("{id:guid}/members/remove"), Authorize(Policy = Permissions.AdUsersGroupsRemove), EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> RemoveMembers(Guid id, [FromBody] GroupMembersRequest req, CancellationToken ct) =>
        Ok(new { results = await changes.ChangeGroupMembersAsync(id, add: false, req, ct) });

    [HttpGet("{id:guid}/members/export"), Authorize(Policy = Permissions.AdGroupsMemberExport)]
    public async Task<IActionResult> ExportMembers(Guid id, [FromQuery] string? q, [FromQuery] MemberKind? kind, CancellationToken ct = default)
    {
        var group = await ad.GetGroupAsync(id, ct);
        if (group == null) return Problem(statusCode: 404, title: "Not found", detail: "Group not found in the directory.");

        var limit = app.Value.ExportRowLimit;
        var probe = await ad.SearchMembersAsync(id, q, kind, 1, 1, ct);
        var filterText = $"filter '{q}', type {kind?.ToString() ?? "All"}";
        if (probe.Total > limit)
        {
            await audit.WriteAsync(new AuditEntry
            {
                Action = "ad.groups.member.export", Module = "ad", Target = group.Name, TargetId = id.ToString(),
                Result = AuditResult.Failure, Error = $"{probe.Total} rows exceed the export limit of {limit}", NewValue = filterText,
            });
            return Problem(statusCode: 400, title: "Too many rows", detail: $"This export has {probe.Total} rows, more than the limit of {limit}. Narrow the filter and try again.");
        }

        await audit.WriteAsync(new AuditEntry
        {
            Action = "ad.groups.member.export", Module = "ad", Target = group.Name, TargetId = id.ToString(), NewValue = $"{probe.Total} rows, {filterText}",
        });
        await CsvWriter.WriteAsync(Response, $"group-members-{Slug(group.Name)}-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv",
            ["Name", "Username", "Email", "Type", "Enabled", "Distinguished name"], Rows(id, q, kind, ct));
        return new EmptyResult();
    }

    private async IAsyncEnumerable<IEnumerable<string?>> Rows(Guid id, string? q, MemberKind? kind, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var page = 1; ; page++)
        {
            var r = await ad.SearchMembersAsync(id, q, kind, page, 500, ct);
            foreach (var m in r.Items)
                yield return [m.Name, m.SamAccountName, m.Email, m.Kind.ToString(), m.Enabled?.ToString() ?? "", m.Dn];
            if (page * 500 >= r.Total || r.Items.Count == 0) yield break;
        }
    }

    private static string Slug(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
