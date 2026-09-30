using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDashboard.Data;
using ServiceDashboard.Models;
using ServiceDashboard.Services;

namespace ServiceDashboard.Controllers;

[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController(
    IEnumerable<IDashboardCardProvider> providers, ModuleCatalog modules, ICurrentUser currentUser, AppDbContext db, SettingsService settings) : ControllerBase
{
    private static readonly string[] Counted = [AuditResult.Success, AuditResult.Failure, AuditResult.Denied];

    /// <summary>Fixed cards. Each one only appears if the user holds the permission that reads the underlying data.</summary>
    [HttpGet, Authorize(Policy = Permissions.DashboardRead)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var user = (await currentUser.GetAsync())!;
        var cards = new List<DashboardCard>();
        foreach (var p in providers)
            if (await modules.IsEnabledAsync(p.ModuleId)) cards.AddRange(await p.GetCardsAsync(user, ct));

        var general = await settings.GetGeneralAsync();
        var tz = BannerLogic.FindZone(general.TimeZone);
        var startOfToday = TimeZoneInfo.ConvertTimeToUtc(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date, tz);

        object? lastActions = null;
        if (user.HasAny(Permissions.LogsRead, Permissions.LogsReadOwn))
        {
            var all = user.Has(Permissions.LogsRead);
            var today = db.AuditLogs.AsNoTracking().Where(a => a.Category == AuditCategory.Admin && a.TimeUtc >= startOfToday && Counted.Contains(a.Result));
            var recent = db.AuditLogs.AsNoTracking().Where(a => a.Category == AuditCategory.Admin && a.Result != AuditResult.Validated);
            if (!all)
            {
                today = today.Where(a => a.UserId == user.Id);
                recent = recent.Where(a => a.UserId == user.Id);
            }
            var now = DateTime.UtcNow;
            cards.Add(new DashboardCard("actionsToday", all ? "Actions performed today" : "Your actions today",
                await today.CountAsync(ct), "/logs/admin?range=today", "neutral", now));
            cards.Add(new DashboardCard("failedToday", all ? "Failed actions today" : "Your failed actions today",
                await today.CountAsync(a => a.Result != AuditResult.Success, ct), "/logs/admin?range=today&result=Failure", "error", now));
            lastActions = new
            {
                title = all ? "Last 10 admin actions" : "Your last 10 actions",
                items = await recent.OrderByDescending(a => a.TimeUtc).ThenByDescending(a => a.Id).Take(10)
                    .Select(a => new { a.Id, a.TimeUtc, a.UserName, a.Action, a.Target, a.Result }).ToListAsync(ct),
            };
        }
        return Ok(new { cards, lastActions });
    }
}
