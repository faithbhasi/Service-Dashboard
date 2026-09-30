using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ServiceDashboard.Middleware;
using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers;
using ServiceDashboard.Modules.ActiveDirectory.Services;

namespace ServiceDashboard.Modules.ActiveDirectory.Controllers;

[ApiController, ModuleGate(ActiveDirectoryModule.Id)]
[Route("api/modules/ad/ous")]
public sealed class AdOusController(AdDirectoryService ad) : ControllerBase
{
    /// <summary>Lazy OU tree. Pass parent to expand a node, or q to search by name.</summary>
    [HttpGet, Authorize(Policy = PermissionPolicies.AdOuBrowse)]
    public async Task<IActionResult> Browse([FromQuery] string? parent, [FromQuery] string? q, [FromQuery] DirectoryObjectKind kind = DirectoryObjectKind.User, CancellationToken ct = default) =>
        Ok(await ad.BrowseOusAsync(parent, kind, q, ct));
}
