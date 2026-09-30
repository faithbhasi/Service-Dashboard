using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ServiceDashboard.Configuration;
using ServiceDashboard.Data;
using ServiceDashboard.Models;

namespace ServiceDashboard.Services;

/// <summary>Audit retention and the optional online SQLite backup. Kept as plain methods so they can be called from tests.</summary>
public sealed class MaintenanceTasks(IDbContextFactory<AppDbContext> factory, AppPaths paths, IOptions<AppOptions> options, IAuditService audit, ILogger<MaintenanceTasks> log)
{
    /// <summary>Removes audit rows older than the retention period. The removal is itself logged. Returns the number removed.</summary>
    public async Task<int> PurgeAuditAsync(CancellationToken ct = default)
    {
        var days = options.Value.AuditRetentionDays;
        if (days <= 0) return 0; // 0 = keep forever
        var cutoff = DateTime.UtcNow.AddDays(-days);
        await using var db = await factory.CreateDbContextAsync(ct);
        var removed = await db.AuditLogs.Where(a => a.TimeUtc < cutoff).ExecuteDeleteAsync(ct);
        if (removed > 0)
        {
            log.LogInformation("Audit retention removed {Count} records older than {Cutoff:u}", removed, cutoff);
            await audit.WriteAsync(new AuditEntry
            {
                Action = "maintenance.auditRetention", Module = "core", Target = "Audit log",
                NewValue = $"Removed {removed} records older than {cutoff:u} (retention {days} days)",
            });
        }
        return removed;
    }

    /// <summary>
    /// Consistent online backup: VACUUM INTO writes a complete copy of the database (safe while WAL mode is on),
    /// and the logo folder is copied beside it. Keeps the newest BackupRetentionCount backups.
    /// </summary>
    public async Task<string?> BackupAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(paths.BackupDirectory)) return null;
        var folder = Path.Combine(paths.BackupDirectory, "backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(paths.DatabaseFile));

        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var escaped = target.Replace("'", "''");
            await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{escaped}'", ct);
            if (Directory.Exists(paths.AssetDirectory)) CopyDirectory(paths.AssetDirectory, Path.Combine(folder, "assets"));

            Prune(paths.BackupDirectory, Math.Max(1, options.Value.BackupRetentionCount));
            log.LogInformation("Database backup written to {Folder}", folder);
            await audit.WriteAsync(new AuditEntry { Action = "maintenance.backup", Module = "core", Target = "SQLite database and assets", NewValue = folder });
            return folder;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Database backup failed");
            await audit.WriteAsync(new AuditEntry { Action = "maintenance.backup", Module = "core", Target = "SQLite database and assets", Result = AuditResult.Failure, Error = "Backup failed; see the application log" });
            return null;
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private static void Prune(string root, int keep)
    {
        var old = Directory.GetDirectories(root, "backup-*").OrderByDescending(d => d, StringComparer.Ordinal).Skip(keep);
        foreach (var d in old) Directory.Delete(d, recursive: true);
    }
}

/// <summary>A simple daily timer: retention on start and then once a day, plus the optional daily backup. Not a job framework.</summary>
public sealed class MaintenanceService(IServiceScopeFactory scopes, IOptions<AppOptions> options, ILogger<MaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var tasks = scope.ServiceProvider.GetRequiredService<MaintenanceTasks>();
                await tasks.PurgeAuditAsync(stoppingToken);
                if (options.Value.DailyBackupEnabled) await tasks.BackupAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogError(ex, "Daily maintenance failed"); }
        }
        while (await SafeWait(timer, stoppingToken));
    }

    private static async Task<bool> SafeWait(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); } catch (OperationCanceledException) { return false; }
    }
}
