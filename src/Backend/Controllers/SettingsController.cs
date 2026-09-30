using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServiceDashboard.Data;
using ServiceDashboard.Models;
using ServiceDashboard.Services;

namespace ServiceDashboard.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController(SettingsService settings, ModuleCatalog modules, AppDbContext db) : ControllerBase
{
    /// <summary>Branding for the login page and the shell. Contains no secrets, so it is public.</summary>
    [HttpGet("branding"), AllowAnonymous]
    public async Task<IActionResult> Branding()
    {
        var general = await settings.GetGeneralAsync();
        var branding = await settings.GetBrandingAsync();
        var logos = await db.LogoAssets.AsNoTracking().ToListAsync();
        return Ok(new
        {
            productName = general.ProductName, light = branding.Light, dark = branding.Dark,
            hasLogoLight = logos.Any(l => l.Kind == "logoLight"), hasLogoDark = logos.Any(l => l.Kind == "logoDark"), hasFavicon = logos.Any(l => l.Kind == "favicon"),
            // Changes whenever a logo changes, so browsers fetch the new image.
            assetVersion = logos.Count == 0 ? "0" : logos.Max(l => l.UpdatedUtc).Ticks.ToString(),
        });
    }

    /// <summary>What the app shell needs after sign-in: labels, time zone, active banner and module states.</summary>
    [HttpGet("shell"), Authorize]
    public async Task<IActionResult> Shell()
    {
        var general = await settings.GetGeneralAsync();
        var policies = await settings.GetActionPoliciesAsync();
        return Ok(new
        {
            productName = general.ProductName,
            environmentLabel = general.EnvironmentLabel,
            timeZone = general.TimeZone,
            dateFormat = general.DateFormat,
            supportContact = general.SupportContact,
            idleTimeoutMinutes = general.IdleTimeoutMinutes,
            banner = BannerLogic.ActiveBanner(general),
            actionPolicies = policies,
            modules = await modules.ListAsync(),
        });
    }
}

public static class BannerLogic
{
    /// <summary>The banner to show right now, or null when disabled or outside its start and end times.</summary>
    public static object? ActiveBanner(GeneralSettings g, DateTime? nowUtc = null)
    {
        var b = g.Banner;
        if (!b.Enabled || string.IsNullOrWhiteSpace(b.Text)) return null;
        var tz = FindZone(g.TimeZone);
        var now = TimeZoneInfo.ConvertTimeFromUtc(nowUtc ?? DateTime.UtcNow, tz);
        if (Parse(b.StartLocal) is { } start && now < start) return null;
        if (Parse(b.EndLocal) is { } end && now > end) return null;
        return new { type = b.Type, text = b.Text, dismissible = b.Type == "Information" };
    }

    public static TimeZoneInfo FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception) { return TimeZoneInfo.Utc; }
    }

    private static DateTime? Parse(string? s) =>
        DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
}
