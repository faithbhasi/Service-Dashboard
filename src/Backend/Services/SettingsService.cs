using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ServiceDashboard.Configuration;
using ServiceDashboard.Data;
using ServiceDashboard.Models;

namespace ServiceDashboard.Services;

public static class SettingKeys
{
    public const string General = "general";
    public const string Modules = "modules";
    public const string ActionPolicies = "actionPolicies";
    public const string Branding = "branding";
    public const string ActiveDirectory = "ad";
}

/// <summary>Runtime settings stored as one JSON document per key in SQLite, cached in memory until saved.</summary>
public sealed class SettingsService(AppDbContext db, IMemoryCache cache, IOptions<AppOptions> app)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<T> GetAsync<T>(string key, Func<T> defaults) where T : class
    {
        var cacheKey = "setting:" + key;
        if (cache.TryGetValue(cacheKey, out string? json) && json != null)
            return JsonSerializer.Deserialize<T>(json, Json) ?? defaults();

        var entry = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        json = entry?.Json ?? JsonSerializer.Serialize(defaults(), Json);
        cache.Set(cacheKey, json);
        return JsonSerializer.Deserialize<T>(json, Json) ?? defaults();
    }

    /// <summary>Saves and returns (before, after) JSON for the audit record.</summary>
    public async Task<(string Before, string After)> SaveAsync<T>(string key, T value, string? updatedBy, Func<T> defaults) where T : class
    {
        var before = JsonSerializer.Serialize(await GetAsync(key, defaults), Json);
        var after = JsonSerializer.Serialize(value, Json);
        var entry = await db.Settings.FirstOrDefaultAsync(s => s.Key == key);
        if (entry == null) db.Settings.Add(entry = new SettingEntry { Key = key });
        entry.Json = after;
        entry.UpdatedUtc = DateTime.UtcNow;
        entry.UpdatedBy = updatedBy;
        await db.SaveChangesAsync();
        cache.Remove("setting:" + key);
        return (before, after);
    }

    public GeneralSettings DefaultGeneral() => new()
    {
        ProductName = app.Value.ProductName,
        EnvironmentLabel = app.Value.Environment,
    };

    public Task<GeneralSettings> GetGeneralAsync() => GetAsync(SettingKeys.General, DefaultGeneral);
    public Task<ModulesSettings> GetModulesAsync() => GetAsync(SettingKeys.Modules, () => new ModulesSettings());
    public Task<ActionPoliciesSettings> GetActionPoliciesAsync() => GetAsync(SettingKeys.ActionPolicies, () => new ActionPoliciesSettings());
    public Task<BrandingSettings> GetBrandingAsync() => GetAsync(SettingKeys.Branding, () => new BrandingSettings());
}
