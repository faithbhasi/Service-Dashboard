using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ServiceDashboard.Middleware;
using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers;
using ServiceDashboard.Modules.ActiveDirectory.Services;
using ServiceDashboard.Services;

namespace ServiceDashboard.Modules.ActiveDirectory.Controllers;

[ApiController, ModuleGate(ActiveDirectoryModule.Id)]
[Route("api/modules/ad/users")]
public sealed class AdUsersController(AdDirectoryService ad, AdChangeService changes, ObjectActivity activity) : ControllerBase
{
    [HttpGet, Authorize(Policy = Permissions.AdUsersRead), EnableRateLimiting(RateLimitPolicies.Search)]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] UserFilter filter = UserFilter.All,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, [FromQuery] string? ou = null,
        [FromQuery] string? department = null, [FromQuery] string? title = null, CancellationToken ct = default) =>
        Ok(await ad.SearchUsersAsync(q, filter, page, pageSize, ou, ct, department, title));

    [HttpGet("{id:guid}"), Authorize(Policy = Permissions.AdUsersRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        await ad.GetUserAsync(id, ct) is { } u ? Ok(u) : NotFoundProblem("User");

    [HttpGet("{id:guid}/groups"), Authorize(Policy = Permissions.AdUsersRead)]
    public async Task<IActionResult> Groups(Guid id, CancellationToken ct) =>
        await ad.GetMembershipsAsync(id, DirectoryObjectKind.User, ct) is { } m ? Ok(m) : NotFoundProblem("User");

    /// <summary>Everything done to this user through this application. Changes made with other tools are not shown.</summary>
    [HttpGet("{id:guid}/activity"), Authorize(Policy = Permissions.AdUsersRead), Authorize(Policy = PermissionPolicies.LogsAccess)]
    public async Task<IActionResult> Activity(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 25) =>
        Ok(await activity.ForAsync(id, page, pageSize));

    // ---------- changes: every one goes through AdChangeService (permission, validation, re-read, allowlists, dry run, audit) ----------

    [HttpPost("{id:guid}/reset-password"), Authorize(Policy = Permissions.AdUsersResetPassword), EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> ResetPassword(Guid id, [FromBody] ResetPasswordRequest req, CancellationToken ct) =>
        AsResult(await changes.ResetPasswordAsync(id, req, ct));

    [HttpPost("{id:guid}/unlock"), Authorize(Policy = Permissions.AdUsersUnlock), EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> Unlock(Guid id, [FromBody] ChangeInput req, CancellationToken ct) =>
        AsResult(await changes.UnlockAsync(id, req, ct));

    [HttpPost("{id:guid}/enable"), Authorize(Policy = Permissions.AdUsersEnable), EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> Enable(Guid id, [FromBody] ChangeInput req, CancellationToken ct) =>
        AsResult(await changes.SetUserEnabledAsync(id, true, req, ct));

    [HttpPost("{id:guid}/disable"), Authorize(Policy = Permissions.AdUsersDisable), EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> Disable(Guid id, [FromBody] ChangeInput req, CancellationToken ct) =>
        AsResult(await changes.SetUserEnabledAsync(id, false, req, ct));

    [HttpPost("{id:guid}/move"), Authorize(Policy = Permissions.AdUsersMove), EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> Move(Guid id, [FromBody] MoveRequest req, CancellationToken ct) =>
        AsResult(await changes.MoveAsync(id, DirectoryObjectKind.User, req, ct));

    [HttpGet("{id:guid}/addable-groups"), Authorize(Policy = Permissions.AdUsersGroupsAdd)]
    public async Task<IActionResult> AddableGroups(Guid id, [FromQuery] string? q, CancellationToken ct) =>
        Ok(await changes.AddableGroupsAsync(id, q, ct));

    [HttpPost("{id:guid}/groups/add"), Authorize(Policy = Permissions.AdUsersGroupsAdd), EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> AddToGroups(Guid id, [FromBody] GroupsRequest req, CancellationToken ct) =>
        Ok(new { results = await changes.ChangeGroupsAsync(id, add: true, req, ct) });

    [HttpPost("{id:guid}/groups/remove"), Authorize(Policy = Permissions.AdUsersGroupsRemove), EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> RemoveFromGroups(Guid id, [FromBody] GroupsRequest req, CancellationToken ct) =>
        Ok(new { results = await changes.ChangeGroupsAsync(id, add: false, req, ct) });

    private IActionResult AsResult(ChangeResult r) => ChangeResults.ToActionResult(this, r);

    private IActionResult NotFoundProblem(string what) => Problem(statusCode: 404, title: "Not found", detail: $"{what} not found in the directory.");
}
