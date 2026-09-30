using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ServiceDashboard.Middleware;
using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers;
using ServiceDashboard.Modules.ActiveDirectory.Services;

namespace ServiceDashboard.Modules.ActiveDirectory.Controllers;

[ApiController, ModuleGate(ActiveDirectoryModule.Id)]
[Route("api/modules/ad/users")]
public sealed class AdUsersController(AdDirectoryService ad) : ControllerBase
{
    [HttpGet, Authorize(Policy = Permissions.AdUsersRead), EnableRateLimiting(RateLimitPolicies.Search)]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] UserFilter filter = UserFilter.All,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, [FromQuery] string? ou = null, CancellationToken ct = default) =>
        Ok(await ad.SearchUsersAsync(q, filter, page, pageSize, ou, ct));

    [HttpGet("{id:guid}"), Authorize(Policy = Permissions.AdUsersRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        await ad.GetUserAsync(id, ct) is { } u ? Ok(u) : NotFoundProblem("User");

    [HttpGet("{id:guid}/groups"), Authorize(Policy = Permissions.AdUsersRead)]
    public async Task<IActionResult> Groups(Guid id, CancellationToken ct) =>
        await ad.GetMembershipsAsync(id, DirectoryObjectKind.User, ct) is { } m ? Ok(m) : NotFoundProblem("User");

    private IActionResult NotFoundProblem(string what) => Problem(statusCode: 404, title: "Not found", detail: $"{what} not found in the directory.");
}
