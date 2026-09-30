using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ServiceDashboard.Data;
using ServiceDashboard.Models;

namespace ServiceDashboard.Services;

/// <summary>What the caller supplies; user, IP, browser and correlation ID are filled from the current request.</summary>
public sealed class AuditEntry
{
    public AuditCategory Category { get; init; } = AuditCategory.Admin;
    public required string Action { get; init; }
    public string? Module { get; init; }
    public string? Target { get; init; }
    public string? TargetId { get; init; }
    public string? PreviousValue { get; init; }
    public string? NewValue { get; init; }
    public string Result { get; init; } = AuditResult.Success;
    public string? Error { get; init; }
    public string? Justification { get; init; }
    public string? TicketNumber { get; init; }
    /// <summary>Set for sign-in events, where there is no authenticated principal yet.</summary>
    public Guid? UserId { get; init; }
    public string? UserName { get; init; }
}

public interface IAuditService
{
    Task WriteAsync(AuditEntry entry);
}

public sealed class AuditService(IDbContextFactory<AppDbContext> factory, IHttpContextAccessor accessor, ILogger<AuditService> log) : IAuditService
{
    public const string UidClaim = ClaimTypes.NameIdentifier;

    public async Task WriteAsync(AuditEntry e)
    {
        try
        {
            var ctx = accessor.HttpContext;
            var userId = e.UserId;
            var userName = e.UserName;
            if (userId == null && ctx?.User.Identity?.IsAuthenticated == true
                && Guid.TryParse(ctx.User.FindFirstValue(UidClaim), out var uid))
            {
                userId = uid;
                userName ??= ctx.User.FindFirstValue("name") ?? ctx.User.FindFirstValue("email");
            }

            var row = new AuditLog
            {
                TimeUtc = DateTime.UtcNow,
                Category = e.Category,
                UserId = userId,
                UserName = Trim(userName, 256),
                Action = e.Action,
                Module = e.Module,
                Target = Trim(e.Target, 512),
                TargetId = Trim(e.TargetId, 128),
                PreviousValue = Trim(e.PreviousValue, 20000),
                NewValue = Trim(e.NewValue, 20000),
                Result = e.Result,
                Error = Trim(e.Error, 1000),
                Justification = Trim(e.Justification, 2000),
                TicketNumber = Trim(e.TicketNumber, 100),
                IpAddress = ctx?.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Trim(ctx?.Request.Headers.UserAgent.ToString(), 512),
                CorrelationId = ctx?.TraceIdentifier,
            };
            await using var db = await factory.CreateDbContextAsync();
            db.AuditLogs.Add(row);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Never let an audit failure hide the outcome of the request, but make it visible.
            log.LogError(ex, "Failed to write audit record for {AuditAction}", e.Action);
        }
    }

    private static string? Trim(string? s, int max) => s is { Length: > 0 } && s.Length > max ? s[..max] : s;
}
