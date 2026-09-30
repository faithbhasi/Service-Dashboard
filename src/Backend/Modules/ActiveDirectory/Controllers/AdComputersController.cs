using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ServiceDashboard.Middleware;
using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers;
using ServiceDashboard.Modules.ActiveDirectory.Services;

namespace ServiceDashboard.Modules.ActiveDirectory.Controllers;

[ApiController, ModuleGate(ActiveDirectoryModule.Id)]
[Route("api/modules/ad/computers")]
public sealed class AdComputersController(AdDirectoryService ad) : ControllerBase
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

    private IActionResult NotFoundProblem() => Problem(statusCode: 404, title: "Not found", detail: "Computer not found in the directory.");
}
