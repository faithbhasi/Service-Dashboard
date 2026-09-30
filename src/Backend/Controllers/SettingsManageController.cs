using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ServiceDashboard.Models;
using ServiceDashboard.Services;

namespace ServiceDashboard.Controllers;

/// <summary>General (with the banner), Modules and Action Policies. Every save is validated here and audited with before and after values.</summary>
[ApiController]
[Route("api/settings")]
public sealed class SettingsManageController(SettingsService settings, ModuleCatalog modules, IEnumerable<ModuleDescriptor> registered, IAuditService audit, ICurrentUser currentUser) : ControllerBase
{
    public static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy"];
    public static readonly string[] BannerTypes = ["Information", "Warning", "Maintenance"];
    public const int BannerMaxLength = 300;

    private static ApiException Invalid(string message) => new(400, "Invalid setting", message, "validation");

    private async Task<string?> WhoAsync() => (await currentUser.GetAsync())?.DisplayName;

    // ------------------------------------------------------------ General (+ banner)

    [HttpGet("general"), Authorize(Policy = PermissionPolicies.SettingsAccess)]
    public async Task<IActionResult> GetGeneral()
    {
        var g = await settings.GetGeneralAsync();
        g.EnvironmentLabelColor ??= "";
        return Ok(g);
    }

    [HttpPut("general"), Authorize(Policy = Permissions.SettingsManage)]
    public async Task<IActionResult> PutGeneral([FromBody] GeneralSettings value)
    {
        var current = await settings.GetGeneralAsync();
        value.EnvironmentLabelColor ??= current.EnvironmentLabelColor ?? ""; // a client that does not send the colour keeps what is set
        var next = ValidateGeneral(value);
        await settings.SaveAsync(SettingKeys.General, next, await WhoAsync(), settings.DefaultGeneral);

        // The banner is audited on its own so creating, changing or removing it is easy to find.
        var before = Serialize(WithoutBanner(current));
        var after = Serialize(WithoutBanner(next));
        if (before != after)
            await audit.WriteAsync(new AuditEntry { Action = "settings.general.update", Module = "core", Target = "General settings", PreviousValue = before, NewValue = after });

        var bBefore = Serialize(current.Banner);
        var bAfter = Serialize(next.Banner);
        if (bBefore != bAfter)
        {
            var had = current.Banner.Enabled && current.Banner.Text.Length > 0;
            var has = next.Banner.Enabled && next.Banner.Text.Length > 0;
            var action = !had && has ? "settings.banner.create" : had && !has ? "settings.banner.remove" : "settings.banner.update";
            await audit.WriteAsync(new AuditEntry { Action = action, Module = "core", Target = "Application banner", PreviousValue = bBefore, NewValue = bAfter });
        }
        return Ok(next);
    }

    private static object WithoutBanner(GeneralSettings g) => new { g.ProductName, g.EnvironmentLabel, g.EnvironmentLabelColor, g.TimeZone, g.DateFormat, g.SupportContact, g.IdleTimeoutMinutes, g.AbsoluteTimeoutMinutes };

    private static string Serialize(object o) => JsonSerializer.Serialize(o, SettingsService.Json);

    private GeneralSettings ValidateGeneral(GeneralSettings v)
    {
        var name = (v.ProductName ?? "").Trim();
        if (name.Length is 0 or > 100) throw Invalid("The product name must be 1 to 100 characters.");
        var env = (v.EnvironmentLabel ?? "").Trim();
        if (env.Length > 30) throw Invalid("The environment label can be up to 30 characters.");
        var envColor = (v.EnvironmentLabelColor ?? "").Trim();
        if (envColor.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(envColor, @"\A#[0-9a-fA-F]{6}\z")) throw Invalid("The environment label colour must look like #1f5fbf.");
        var tz = (v.TimeZone ?? "").Trim();
        try { TimeZoneInfo.FindSystemTimeZoneById(tz); } catch (Exception) { throw Invalid($"'{tz}' is not a known time zone. Use an IANA name such as Europe/London or UTC."); }
        if (!DateFormats.Contains(v.DateFormat)) throw Invalid("Choose one of the listed date formats.");
        var support = (v.SupportContact ?? "").Trim();
        if (support.Length > 200) throw Invalid("The support contact can be up to 200 characters.");
        if (v.IdleTimeoutMinutes is < 5 or > 1440) throw Invalid("The idle timeout must be between 5 and 1440 minutes.");
        if (v.AbsoluteTimeoutMinutes is < 15 or > 10080) throw Invalid("The absolute session lifetime must be between 15 minutes and 7 days.");
        if (v.AbsoluteTimeoutMinutes < v.IdleTimeoutMinutes) throw Invalid("The absolute session lifetime cannot be shorter than the idle timeout.");

        var b = v.Banner ?? new BannerSettings();
        var text = (b.Text ?? "").Trim();
        if (!BannerTypes.Contains(b.Type)) throw Invalid("The banner type must be Information, Warning or Maintenance.");
        if (text.Length > BannerMaxLength) throw Invalid($"The banner text can be up to {BannerMaxLength} characters.");
        if (text.Contains('<') || text.Contains('>')) throw Invalid("The banner text is plain text only. Remove any HTML.");
        if (b.Enabled && text.Length == 0) throw Invalid("Enter the banner text, or turn the banner off.");
        var start = ParseLocal(b.StartLocal, "start");
        var end = ParseLocal(b.EndLocal, "end");
        if (start != null && end != null && end <= start) throw Invalid("The banner end must be after its start.");

        return new GeneralSettings
        {
            ProductName = name, EnvironmentLabel = env, EnvironmentLabelColor = envColor.ToLowerInvariant(), TimeZone = tz, DateFormat = v.DateFormat, SupportContact = support,
            IdleTimeoutMinutes = v.IdleTimeoutMinutes, AbsoluteTimeoutMinutes = v.AbsoluteTimeoutMinutes,
            Banner = new BannerSettings
            {
                Enabled = b.Enabled, Type = b.Type, Text = text,
                StartLocal = start?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
                EndLocal = end?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            },
        };
    }

    private static DateTime? ParseLocal(string? s, string what)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!DateTime.TryParseExact(s.Trim(), ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            throw Invalid($"The banner {what} time is not a valid date and time.");
        return d;
    }

    // ------------------------------------------------------------ Modules

    [HttpGet("modules"), Authorize(Policy = PermissionPolicies.SettingsAccess)]
    public async Task<IActionResult> GetModules() => Ok(await modules.ListAsync());

    public sealed record ModuleToggle(bool Enabled);

    [HttpPut("modules/{id}"), Authorize(Policy = Permissions.SettingsManage)]
    public async Task<IActionResult> PutModule(string id, [FromBody] ModuleToggle body)
    {
        if (registered.All(m => m.Id != id))
            return Problem(statusCode: 400, title: "Cannot change this module", detail: ModuleCatalog.ComingSoon.Any(m => m.Id == id) ? "This module is Coming Soon and cannot be enabled yet." : "Unknown module.");

        var current = await settings.GetModulesAsync();
        var was = !current.Enabled.TryGetValue(id, out var w) || w;
        current.Enabled[id] = body.Enabled;
        await settings.SaveAsync(SettingKeys.Modules, current, await WhoAsync(), () => new ModulesSettings());
        if (was != body.Enabled)
            await audit.WriteAsync(new AuditEntry
            {
                Action = "settings.modules.update", Module = "core", Target = "Module: " + id,
                PreviousValue = was ? "Enabled" : "Disabled", NewValue = body.Enabled ? "Enabled" : "Disabled",
            });
        return Ok(await modules.ListAsync());
    }

    // ------------------------------------------------------------ Action policies

    [HttpGet("action-policies"), Authorize(Policy = PermissionPolicies.SettingsAccess)]
    public async Task<IActionResult> GetPolicies() => Ok(await settings.GetActionPoliciesAsync());

    [HttpPut("action-policies"), Authorize(Policy = Permissions.SettingsManage)]
    public async Task<IActionResult> PutPolicies([FromBody] ActionPoliciesSettings value)
    {
        var next = new ActionPoliciesSettings { Actions = new Dictionary<string, ActionPolicy>() };
        foreach (var key in ActionKeys.All)
        {
            if (value.Actions == null || !value.Actions.TryGetValue(key, out var p)) throw Invalid($"The policy for '{key}' is missing.");
            var pattern = string.IsNullOrWhiteSpace(p.TicketPattern) ? null : p.TicketPattern.Trim();
            if (pattern != null)
            {
                if (pattern.Length > 200) throw Invalid("A ticket pattern can be up to 200 characters.");
                try { _ = Regex.IsMatch("INC-0001", pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250)); }
                catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException) { throw Invalid($"The ticket pattern for '{key}' is not a valid regular expression."); }
            }
            if (p.JustificationMinLength is < 0 or > 500) throw Invalid("The minimum justification length must be between 0 and 500.");
            if (p.JustificationRequired && p.JustificationMinLength < 1) throw Invalid("A required justification needs a minimum length of at least 1.");
            next.Actions[key] = new ActionPolicy
            {
                JustificationRequired = p.JustificationRequired, JustificationMinLength = p.JustificationMinLength,
                TicketRequired = p.TicketRequired, TicketPattern = pattern, TypedConfirmationRequired = p.TypedConfirmationRequired,
            };
        }
        if (value.GeneratedPasswordLength is < 8 or > 128) throw Invalid("The generated password length must be between 8 and 128.");
        next.GeneratedPasswordLength = value.GeneratedPasswordLength;
        next.MustChangePasswordDefault = value.MustChangePasswordDefault;

        var (before, after) = await settings.SaveAsync(SettingKeys.ActionPolicies, next, await WhoAsync(), () => new ActionPoliciesSettings());
        if (before != after)
            await audit.WriteAsync(new AuditEntry { Action = "settings.actionPolicies.update", Module = "core", Target = "Action policies", PreviousValue = before, NewValue = after });
        return Ok(next);
    }
}
