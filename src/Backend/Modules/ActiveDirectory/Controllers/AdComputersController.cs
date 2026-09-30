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
[Route("api/modules/ad/computers")]
public sealed class AdComputersController(AdDirectoryService ad, AdChangeService changes, ObjectActivity activity) : ControllerBase
{
    [HttpGet, Authorize(Policy = Permissions.AdComputersRead), EnableRateLimiting(RateLimitPolicies.Search)]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] ComputerFilter filter = ComputerFilter.All,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, [FromQuery] string? ou = null, CancellationToken ct = default) =>
        Ok(await ad.SearchComputersAsync(q, filter, page, pageSize, ou, ct));

    [HttpGet("{id:guid}"), Authorize(Policy = Permissions.AdComputersRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        await ad.GetComputerAsync(id, ct) is { } c ? Ok(c) : NotFoundProblem();

    [HttpGet("{id:guid}/groups"), Authorize(Policy = Permissions.AdComputersRead)]
    public async Task<IActionResult> Groups(Guid id, CancellationToken ct) =>
        await ad.GetMembershipsAsync(id, DirectoryObjectKind.Computer, ct) is { } m ? Ok(m) : NotFoundProblem();

    [HttpGet("{id:guid}/activity"), Authorize(Policy = Permissions.AdComputersRead), Authorize(Policy = PermissionPolicies.LogsAccess)]
    public async Task<IActionResult> Activity(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 25) =>
        Ok(await activity.ForAsync(id, page, pageSize));

    [HttpPost("{id:guid}/enable"), Authorize(Policy = Permissions.AdComputersEnable), EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> Enable(Guid id, [FromBody] ChangeInput req, CancellationToken ct) =>
        ChangeResults.ToActionResult(this, await changes.SetComputerEnabledAsync(id, true, req, ct));

    [HttpPost("{id:guid}/disable"), Authorize(Policy = Permissions.AdComputersDisable), EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> Disable(Guid id, [FromBody] ChangeInput req, CancellationToken ct) =>
        ChangeResults.ToActionResult(this, await changes.SetComputerEnabledAsync(id, false, req, ct));

    [HttpPost("{id:guid}/move"), Authorize(Policy = Permissions.AdComputersMove), EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> Move(Guid id, [FromBody] MoveRequest req, CancellationToken ct) =>
        ChangeResults.ToActionResult(this, await changes.MoveAsync(id, DirectoryObjectKind.Computer, req, ct));

    private IActionResult NotFoundProblem() => Problem(statusCode: 404, title: "Not found", detail: "Computer not found in the directory.");
}
