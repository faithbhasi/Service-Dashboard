using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDashboard.Data;
using ServiceDashboard.Middleware;
using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers;

namespace ServiceDashboard.Modules.ActiveDirectory.Controllers;

public sealed record HourlyPoint(DateTime HourUtc, int PasswordResets, int Unlocks, int Lockouts);
public sealed record HourlyActivity(int Hours, IReadOnlyList<HourlyPoint> Points, int TotalPasswordResets, int TotalUnlocks, int TotalLockouts, string? LockoutsNote);

/// <summary>Hour-by-hour counts for the Home chart: password resets and unlocks done through this application, and account lockouts recorded in AD.</summary>
[ApiController, ModuleGate(ActiveDirectoryModule.Id)]
[Route("api/modules/ad/activity")]
public sealed class AdInsightsController(AppDbContext db, IDirectoryProvider provider) : ControllerBase
{
    private const int MaxLockouts = 5000;

    [HttpGet("hourly"), Authorize(Policy = Permissions.AdUsersRead)]
    public async Task<IActionResult> Hourly([FromQuery] int hours = 24, CancellationToken ct = default)
    {
        hours = Math.Clamp(hours, 6, 72);
        var end = TopOfHour(DateTime.UtcNow).AddHours(1); // exclusive: the current, unfinished hour is the last bar
        var start = end.AddHours(-hours);

        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.TimeUtc >= start && a.Result == AuditResult.Success && (a.Action == "ad.user.resetPassword" || a.Action == "ad.user.unlock"))
            .Select(a => new { a.TimeUtc, a.Action }).ToListAsync(ct);

        string? note = null;
        IReadOnlyList<DateTime> lockouts = [];
        try { lockouts = await provider.LockoutTimesAsync(start, MaxLockouts, ct); }
        catch (Exception ex) when (ex is DirectoryUnavailableException or ModuleUnavailableException)
        { note = "Lockouts could not be read because the directory is unreachable."; }
        if (lockouts.Count >= MaxLockouts) note = $"Lockouts are capped at {MaxLockouts}.";

        var points = new List<HourlyPoint>();
        for (var h = start; h < end; h = h.AddHours(1))
        {
            var next = h.AddHours(1);
            points.Add(new HourlyPoint(h,
                rows.Count(r => r.Action == "ad.user.resetPassword" && r.TimeUtc >= h && r.TimeUtc < next),
                rows.Count(r => r.Action == "ad.user.unlock" && r.TimeUtc >= h && r.TimeUtc < next),
                lockouts.Count(t => t >= h && t < next)));
        }
        return Ok(new HourlyActivity(hours, points, points.Sum(p => p.PasswordResets), points.Sum(p => p.Unlocks), points.Sum(p => p.Lockouts), note));
    }

    private static DateTime TopOfHour(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);
}
