using Microsoft.EntityFrameworkCore;
using ServiceDashboard.Data;
using ServiceDashboard.Models;

namespace ServiceDashboard.Services;

public sealed class LogFilter
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public Guid? UserId { get; set; }
    public string? Action { get; set; }
    public string? Result { get; set; }
    public string? Target { get; set; }
    public string? Ticket { get; set; }
    public string? Module { get; set; }
    public string? TargetId { get; set; }
}

public sealed record LogRow(
    long Id, DateTime TimeUtc, string Category, Guid? UserId, string? UserName, string Action, string? Module, string? Target, string? TargetId,
    string? PreviousValue, string? NewValue, string Result, string? Error, string? Justification, string? TicketNumber,
    string? IpAddress, string? UserAgent, string? CorrelationId);

/// <summary>Filtering, paging and scoping for the Activity and Logs area. The audit table is only ever read here, never edited.</summary>
public static class AuditQuery
{
    public const int ListValueLimit = 300;

    public static IQueryable<AuditLog> Filter(IQueryable<AuditLog> q, LogFilter f)
    {
        if (f.From != null) q = q.Where(a => a.TimeUtc >= f.From);
        if (f.To != null) q = q.Where(a => a.TimeUtc <= f.To);
        if (f.UserId != null) q = q.Where(a => a.UserId == f.UserId);
        if (!string.IsNullOrWhiteSpace(f.Action)) q = q.Where(a => EF.Functions.Like(a.Action, Like(f.Action), "\\"));
        if (!string.IsNullOrWhiteSpace(f.Result)) q = q.Where(a => a.Result == f.Result);
        if (!string.IsNullOrWhiteSpace(f.Target)) q = q.Where(a => a.Target != null && EF.Functions.Like(a.Target, Like(f.Target), "\\"));
        if (!string.IsNullOrWhiteSpace(f.Ticket)) q = q.Where(a => a.TicketNumber != null && EF.Functions.Like(a.TicketNumber, Like(f.Ticket), "\\"));
        if (!string.IsNullOrWhiteSpace(f.Module)) q = q.Where(a => a.Module == f.Module);
        if (!string.IsNullOrWhiteSpace(f.TargetId)) q = q.Where(a => a.TargetId == f.TargetId);
        return q;
    }

    /// <summary>"contains" pattern with LIKE wildcards in the user's text escaped.</summary>
    private static string Like(string text) => "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public static IQueryable<AuditLog> ForCategory(IQueryable<AuditLog> q, string? tab) => tab switch
    {
        "logons" => q.Where(a => a.Category == AuditCategory.Logon),
        "access" => q.Where(a => a.Category == AuditCategory.Access),
        "admin" => q.Where(a => a.Category == AuditCategory.Admin),
        _ => q, // user-activity: every category
    };

    public static IQueryable<LogRow> ToRows(IQueryable<AuditLog> q) => q.Select(a => new LogRow(
        a.Id, a.TimeUtc, a.Category.ToString(), a.UserId, a.UserName, a.Action, a.Module, a.Target, a.TargetId,
        a.PreviousValue == null ? null : a.PreviousValue.Length > ListValueLimit ? a.PreviousValue.Substring(0, ListValueLimit) : a.PreviousValue,
        a.NewValue == null ? null : a.NewValue.Length > ListValueLimit ? a.NewValue.Substring(0, ListValueLimit) : a.NewValue,
        a.Result, a.Error, a.Justification, a.TicketNumber, a.IpAddress, a.UserAgent, a.CorrelationId));

    public static LogRow ToFullRow(AuditLog a) => new(a.Id, a.TimeUtc, a.Category.ToString(), a.UserId, a.UserName, a.Action, a.Module, a.Target, a.TargetId,
        a.PreviousValue, a.NewValue, a.Result, a.Error, a.Justification, a.TicketNumber, a.IpAddress, a.UserAgent, a.CorrelationId);

    public static readonly string[] CsvHeader =
        ["Time (UTC)", "Category", "User", "Action", "Module", "Target", "Target ID", "Previous value", "New value", "Result", "Error", "Justification", "Ticket", "IP address", "Browser", "Correlation ID"];

    public static IEnumerable<string?> CsvRow(AuditLog a) =>
    [
        a.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss"), a.Category.ToString(), a.UserName, a.Action, a.Module, a.Target, a.TargetId, a.PreviousValue, a.NewValue,
        a.Result, a.Error, a.Justification, a.TicketNumber, a.IpAddress, a.UserAgent, a.CorrelationId,
    ];
}

public sealed record LogPage(IReadOnlyList<LogRow> Items, int Total, int Page, int PageSize);

/// <summary>Audit history of one object (by TargetId, for AD the objectGUID), for the Activity History tab in drawers.</summary>
public sealed class ObjectActivity(AppDbContext db, ICurrentUser currentUser)
{
    public async Task<LogPage> ForAsync(Guid targetId, int page, int pageSize)
    {
        var user = (await currentUser.GetAsync())!;
        var all = user.Has(Permissions.LogsRead);
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);
        var q = db.AuditLogs.AsNoTracking().Where(a => a.Category == AuditCategory.Admin && a.TargetId == targetId.ToString());
        if (!all) q = q.Where(a => a.UserId == user.Id);
        var total = await q.CountAsync();
        var items = await AuditQuery.ToRows(q.OrderByDescending(a => a.TimeUtc).ThenByDescending(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync();
        return new LogPage(items, total, page, pageSize);
    }
}
