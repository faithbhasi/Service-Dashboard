using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ServiceDashboard.Configuration;
using ServiceDashboard.Data;
using ServiceDashboard.Models;
using ServiceDashboard.Services;

namespace ServiceDashboard.Controllers;

/// <summary>Logo, favicon, product name and colours. Applies to everyone. Logos are files under the asset directory, metadata is in SQLite.</summary>
[ApiController]
[Route("api/settings/personalization")]
public sealed partial class PersonalizationController(
    SettingsService settings, AppDbContext db, AppPaths paths, IOptions<AppOptions> app, IAuditService audit, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>PNG, JPEG and WebP only. SVG is refused because it can contain scripts.</summary>
    public static readonly string[] AllowedTypes = ["image/png", "image/jpeg", "image/webp"];
    private static readonly Dictionary<string, string> Kinds = new() { ["light"] = "logoLight", ["dark"] = "logoDark", ["favicon"] = "favicon" };

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Hex();

    private async Task<string?> WhoAsync() => (await currentUser.GetAsync())?.DisplayName;
    private static ApiException Invalid(string m) => new(400, "Invalid setting", m, "validation");

    [HttpGet, Authorize(Policy = PermissionPolicies.SettingsAccess)]
    public async Task<IActionResult> Get()
    {
        var general = await settings.GetGeneralAsync();
        var branding = await settings.GetBrandingAsync();
        var logos = await db.LogoAssets.AsNoTracking().ToListAsync();
        return Ok(new
        {
            productName = general.ProductName, light = branding.Light, dark = branding.Dark,
            defaults = new { light = ThemeColors.DefaultLight(), dark = ThemeColors.DefaultDark() },
            logos = logos.ToDictionary(l => l.Kind, l => new { l.FileName, l.ContentType, l.SizeBytes, l.UpdatedUtc }),
            maxLogoBytes = app.Value.LogoMaxBytes, allowedTypes = AllowedTypes,
        });
    }

    public sealed record PersonalizationRequest(string ProductName, ThemeColors Light, ThemeColors Dark);

    [HttpPut, Authorize(Policy = Permissions.SettingsPersonalizationManage)]
    public async Task<IActionResult> Put([FromBody] PersonalizationRequest req)
    {
        var name = (req.ProductName ?? "").Trim();
        if (name.Length is 0 or > 100) throw Invalid("The product name must be 1 to 100 characters.");
        var light = ValidateColors(req.Light, "light");
        var dark = ValidateColors(req.Dark, "dark");

        var general = await settings.GetGeneralAsync();
        var oldBranding = await settings.GetBrandingAsync();
        var (before, after) = await settings.SaveAsync(SettingKeys.Branding, new BrandingSettings { Light = light, Dark = dark }, await WhoAsync(), () => new BrandingSettings());
        var oldName = general.ProductName;
        if (oldName != name)
        {
            general.ProductName = name;
            await settings.SaveAsync(SettingKeys.General, general, await WhoAsync(), settings.DefaultGeneral);
        }
        if (before != after || oldName != name)
            await audit.WriteAsync(new AuditEntry
            {
                Action = "settings.personalization.update", Module = "core", Target = "Personalization",
                PreviousValue = System.Text.Json.JsonSerializer.Serialize(new { productName = oldName, oldBranding.Light, oldBranding.Dark }, SettingsService.Json),
                NewValue = System.Text.Json.JsonSerializer.Serialize(new { productName = name, light, dark }, SettingsService.Json),
            });
        return Ok(new { productName = name, light, dark });
    }

    /// <summary>Puts the colours (both themes) back to the built-in defaults. The product name and logos are untouched.</summary>
    [HttpPost("reset-colors"), Authorize(Policy = Permissions.SettingsPersonalizationManage)]
    public async Task<IActionResult> ResetColors()
    {
        var (before, after) = await settings.SaveAsync(SettingKeys.Branding, new BrandingSettings(), await WhoAsync(), () => new BrandingSettings());
        if (before != after)
            await audit.WriteAsync(new AuditEntry { Action = "settings.personalization.reset", Module = "core", Target = "Colours", PreviousValue = before, NewValue = after });
        return Ok(new BrandingSettings());
    }

    private static ThemeColors ValidateColors(ThemeColors? c, string theme)
    {
        if (c == null) throw Invalid($"The {theme} colours are missing.");
        foreach (var (label, v) in new[]
        {
            ("primary", c.Primary), ("top bar background", c.TopBarBackground), ("navigation background", c.NavBackground), ("navigation text", c.NavText),
            ("navigation selected item", c.NavSelected), ("page background", c.PageBackground), ("card background", c.CardBackground),
            ("section header", c.SectionHeader), ("success", c.Success), ("warning", c.Warning), ("error", c.Error),
        })
            if (v == null || !Hex().IsMatch(v)) throw Invalid($"The {theme} {label} colour must be a hex value such as #1f5fbf.");
        return c;
    }

    // ------------------------------------------------------------ logo files

    /// <summary>The image itself. Public (the login page needs it) and served with the type detected from its bytes, never the uploaded name.</summary>
    [HttpGet("logo/{kind}"), AllowAnonymous]
    public async Task<IActionResult> GetLogo(string kind)
    {
        if (!Kinds.TryGetValue(kind, out var key)) return NotFound();
        var asset = await db.LogoAssets.AsNoTracking().FirstOrDefaultAsync(l => l.Kind == key);
        if (asset == null) return NotFound();
        var path = Path.Combine(paths.LogoDirectory, asset.FileName);
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers.CacheControl = "public, max-age=300";
        return PhysicalFile(path, asset.ContentType);
    }

    [HttpPost("logo/{kind}"), Authorize(Policy = Permissions.SettingsPersonalizationManage)]
    public async Task<IActionResult> Upload(string kind, IFormFile file, CancellationToken ct)
    {
        if (!Kinds.TryGetValue(kind, out var key)) return NotFound();
        if (file == null || file.Length == 0) throw Invalid("Choose an image file.");
        if (file.Length > app.Value.LogoMaxBytes) throw Invalid($"The image is larger than the limit of {app.Value.LogoMaxBytes / 1024} KB.");

        byte[] bytes;
        await using (var s = file.OpenReadStream())
        {
            bytes = new byte[file.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = await s.ReadAsync(bytes.AsMemory(read), ct);
                if (n == 0) break;
                read += n;
            }
        }

        // Trust the bytes, not the file name or the browser's content type.
        var (type, ext) = Sniff(bytes) ?? throw Invalid("Only PNG, JPEG and WebP images are accepted. SVG is not allowed because it can contain scripts.");

        // Fixed file names per kind: nothing from the upload ever reaches a path.
        Directory.CreateDirectory(paths.LogoDirectory);
        foreach (var old in Directory.GetFiles(paths.LogoDirectory, key + ".*")) System.IO.File.Delete(old);
        var fileName = key + ext;
        await System.IO.File.WriteAllBytesAsync(Path.Combine(paths.LogoDirectory, fileName), bytes, ct);

        var asset = await db.LogoAssets.FirstOrDefaultAsync(l => l.Kind == key, ct);
        var had = asset != null;
        if (asset == null) db.LogoAssets.Add(asset = new LogoAsset { Kind = key });
        asset.FileName = fileName; asset.ContentType = type; asset.SizeBytes = bytes.Length; asset.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(new AuditEntry
        {
            Action = "settings.personalization.logo", Module = "core", Target = "Logo: " + kind,
            PreviousValue = had ? "Custom image" : "Default", NewValue = $"{type}, {bytes.Length} bytes",
        });
        return Ok(new { asset.FileName, asset.ContentType, asset.SizeBytes, asset.UpdatedUtc });
    }

    [HttpDelete("logo/{kind}"), Authorize(Policy = Permissions.SettingsPersonalizationManage)]
    public async Task<IActionResult> ResetLogo(string kind, CancellationToken ct)
    {
        if (!Kinds.TryGetValue(kind, out var key)) return NotFound();
        var asset = await db.LogoAssets.FirstOrDefaultAsync(l => l.Kind == key, ct);
        if (asset == null) return NoContent();
        foreach (var f in Directory.GetFiles(paths.LogoDirectory, key + ".*")) System.IO.File.Delete(f);
        db.LogoAssets.Remove(asset);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry { Action = "settings.personalization.logo", Module = "core", Target = "Logo: " + kind, PreviousValue = "Custom image", NewValue = "Default" });
        return NoContent();
    }

    public static (string Type, string Ext)? Sniff(byte[] b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return ("image/png", ".png");
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ("image/jpeg", ".jpg");
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return ("image/webp", ".webp");
        return null;
    }
}
