using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ServiceDashboard.Services;

namespace ServiceDashboard.Controllers;

[ApiController]
[Route("api/search")]
public sealed class SearchController(IEnumerable<IModuleSearchProvider> providers, ModuleCatalog modules, ICurrentUser currentUser) : ControllerBase
{
    public const int MinLength = 2;
    public const int MaxLength = 100;

    /// <summary>
    /// Asks every enabled module for results. There is no permission of its own: each module only searches and returns
    /// the categories the caller may read, so a user with no read permissions simply gets nothing back.
    /// </summary>
    [HttpGet, Authorize, EnableRateLimiting(RateLimitPolicies.Search)]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
    {
        var query = (q ?? "").Trim();
        if (query.Length < MinLength) return Problem(statusCode: 400, title: "Search text too short", detail: $"Type at least {MinLength} characters.");
        if (query.Length > MaxLength) query = query[..MaxLength];

        var user = await currentUser.GetAsync();
        if (user is not { IsEnabled: true }) return Unauthorized();

        var results = new List<object>();
        foreach (var provider in providers)
        {
            if (!await modules.IsEnabledAsync(provider.ModuleId)) continue;
            var categories = await provider.SearchAsync(query, user, ct);
            if (categories.Count > 0) results.Add(new { moduleId = provider.ModuleId, categories });
        }
        return Ok(new { query, modules = results });
    }
}
