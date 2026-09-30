using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ServiceDashboard.Configuration;
using ServiceDashboard.Data;
using ServiceDashboard.Models;
using ServiceDashboard.Services;

namespace ServiceDashboard.Controllers;

/// <summary>
/// Activity and Logs. Read-only over the append-only audit table. People with only logs.read.own see nothing but their own activity;
/// the scope is applied here on the server, whatever the query string says.
/// </summary>
[ApiController]
[Route("api/logs")]
public sealed class LogsController(AppDbContext db, ICurrentUser currentUser, IAuditService audit, IOptions<AppOptions> app) : ControllerBase
{
    private static readonly string[] Tabs = ["logons", "access", "admin", "user-activity"];

    private async Task<(CurrentUserInfo User, bool All)> Scope()
    {
        var user = (await currentUser.GetAsync())!;
        return (user, user.Has(Permissions.LogsRead));
    }

    private static IQueryable<AuditLog> Scoped(IQueryable<AuditLog> q, CurrentUserInfo user, bool all) => all ? q : q.Where(a => a.UserId == user.Id);

    [HttpGet("{tab}"), Authorize(Policy = PermissionPolicies.LogsAccess)]
    public async Task<IActionResult> List(string tab, [FromQuery] LogFilter filter, [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        if (!Tabs.Contains(tab)) return NotFound();
        var (user, all) = await Scope();
        if (tab == "user-activity" && !all) filter.UserId = user.Id; // own timeline only
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var q = AuditQuery.Filter(AuditQuery.ForCategory(Scoped(db.AuditLogs.AsNoTracking(), user, all), tab), filter);
        var total = await q.CountAsync();
        var items = await AuditQuery.ToRows(q.OrderByDescending(a => a.TimeUtc).ThenByDescending(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync();
        return Ok(new LogPage(items, total, page, pageSize));
    }

    [HttpGet("entry/{id:long}"), Authorize(Policy = PermissionPolicies.LogsAccess)]
    public async Task<IActionResult> Detail(long id)
    {
        var (user, all) = await Scope();
        var row = await Scoped(db.AuditLogs.AsNoTracking(), user, all).FirstOrDefaultAsync(a => a.Id == id);
        return row == null ? NotFound() : Ok(AuditQuery.ToFullRow(row));
    }

    /// <summary>App users to pick from on the User Activity tab.</summary>
    [HttpGet("users"), Authorize(Policy = Permissions.LogsRead)]
    public async Task<IActionResult> Users([FromQuery] string? q)
    {
        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim().ToLower(); query = query.Where(u => u.DisplayName.ToLower().Contains(t) || u.Email.ToLower().Contains(t)); }
        return Ok(await query.OrderBy(u => u.DisplayName).Take(25).Select(u => new { u.Id, u.DisplayName, u.Email }).ToListAsync());
    }

    /// <summary>Everyone who accessed a module or performed an action in a date range.</summary>
    [HttpGet("users-by"), Authorize(Policy = Permissions.LogsRead)]
    public async Task<IActionResult> UsersBy([FromQuery] LogFilter filter)
    {
        var q = AuditQuery.Filter(db.AuditLogs.AsNoTracking().Where(a => a.UserId != null), filter);
        var rows = await q.GroupBy(a => new { a.UserId, a.UserName }).Select(g => new { userId = g.Key.UserId, userName = g.Key.UserName, count = g.Count(), lastUtc = g.Max(a => a.TimeUtc) })
            .OrderByDescending(x => x.count).Take(500).ToListAsync();
        return Ok(rows);
    }

    /// <summary>CSV of the current filtered view, generated in the request. Capped, audited, and protected against formula injection.</summary>
    [HttpGet("{tab}/export"), Authorize(Policy = Permissions.LogsExport)]
    public async Task<IActionResult> Export(string tab, [FromQuery] LogFilter filter, CancellationToken ct)
    {
        if (!Tabs.Contains(tab)) return NotFound();
        var (user, all) = await Scope();
        if (!all) { if (tab == "user-activity") filter.UserId = user.Id; }
        if (!user.HasAny(Permissions.LogsRead, Permissions.LogsReadOwn))
            return Problem(statusCode: 403, title: "Access denied", detail: "You cannot read logs.");

        var q = AuditQuery.Filter(AuditQuery.ForCategory(Scoped(db.AuditLogs.AsNoTracking(), user, all), tab), filter);
        var total = await q.CountAsync(ct);
        var limit = app.Value.ExportRowLimit;
        var description = Describe(tab, filter);

        if (total > limit)
        {
            await audit.WriteAsync(new AuditEntry
            {
                Action = "logs.export", Module = "core", Target = $"Logs: {tab}", Result = AuditResult.Failure,
                Error = $"{total} rows exceed the export limit of {limit}", NewValue = description,
            });
            return Problem(statusCode: 400, title: "Too many rows", detail: $"This export has {total} rows, more than the limit of {limit}. Narrow the filter (for example a shorter date range) and try again.");
        }

        await audit.WriteAsync(new AuditEntry { Action = "logs.export", Module = "core", Target = $"Logs: {tab}", NewValue = $"{total} rows; {description}" });
        await CsvWriter.WriteAsync(Response, $"activity-{tab}-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv", AuditQuery.CsvHeader,
            Rows(q.OrderByDescending(a => a.TimeUtc).ThenByDescending(a => a.Id), ct));
        return new EmptyResult();
    }

    private static async IAsyncEnumerable<IEnumerable<string?>> Rows(IQueryable<AuditLog> q, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var a in q.AsAsyncEnumerable().WithCancellation(ct)) yield return AuditQuery.CsvRow(a);
    }

    private static string Describe(string tab, LogFilter f)
    {
        var parts = new List<string>();
        if (f.From != null) parts.Add($"from {f.From:u}");
        if (f.To != null) parts.Add($"to {f.To:u}");
        if (f.UserId != null) parts.Add($"user {f.UserId}");
        if (!string.IsNullOrWhiteSpace(f.Action)) parts.Add($"action '{f.Action}'");
        if (!string.IsNullOrWhiteSpace(f.Result)) parts.Add($"result {f.Result}");
        if (!string.IsNullOrWhiteSpace(f.Target)) parts.Add($"target '{f.Target}'");
        if (!string.IsNullOrWhiteSpace(f.Ticket)) parts.Add($"ticket '{f.Ticket}'");
        if (!string.IsNullOrWhiteSpace(f.Module)) parts.Add($"module {f.Module}");
        return parts.Count == 0 ? "no filters" : string.Join(", ", parts);
    }
}
