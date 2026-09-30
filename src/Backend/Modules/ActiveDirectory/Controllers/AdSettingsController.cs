using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ServiceDashboard.Configuration;
using ServiceDashboard.Middleware;
using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers;
using ServiceDashboard.Modules.ActiveDirectory.Services;
using ServiceDashboard.Services;

namespace ServiceDashboard.Modules.ActiveDirectory.Controllers;

/// <summary>The AD Integration page in Settings gets its data from here, so every module owns its own settings route.</summary>
[ApiController, ModuleGate(ActiveDirectoryModule.Id)]
[Route("api/modules/ad/settings")]
public sealed partial class AdSettingsController(
    AdSettingsService settings, AdDirectoryService ad, IOptions<ActiveDirectoryOptions> options, IAuditService audit, ICurrentUser currentUser) : ControllerBase
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9-]{0,63}$")]
    private static partial Regex AttributeName();

    [HttpGet, Authorize(Policy = Permissions.SettingsRead)]
    public async Task<IActionResult> Get()
    {
        var o = options.Value;
        return Ok(new
        {
            // Read-only: these come from appsettings.json, not the database.
            connection = new
            {
                provider = ad.Provider.ProviderName, domain = o.Domain, server = o.Server, port = o.Port,
                securityMode = o.UseLdaps ? (o.VerifyCertificate ? "LDAPS (certificate verified)" : "LDAPS (certificate NOT verified)") : "Plain LDAP",
                baseDn = ad.Provider.BaseDn,
            },
            settings = await settings.GetAsync(),
        });
    }

    [HttpPut, Authorize(Policy = Permissions.SettingsManage)]
    public async Task<IActionResult> Put([FromBody] AdSettings value)
    {
        var cleaned = Validate(value);
        var user = await currentUser.GetAsync();
        var (before, after) = await settings.SaveAsync(cleaned, user?.DisplayName);
        await audit.WriteAsync(new AuditEntry { Action = "settings.ad.update", Module = "ad", Target = "AD Integration settings", PreviousValue = before, NewValue = after });
        return Ok(new { settings = cleaned });
    }

    [HttpPost("test-connection"), Authorize(Policy = Permissions.SettingsManage)]
    public async Task<IActionResult> TestConnection(CancellationToken ct)
    {
        var result = await ad.Provider.TestConnectionAsync(ct);
        await audit.WriteAsync(new AuditEntry
        {
            Action = "settings.ad.testConnection", Module = "ad", Target = "AD connection",
            Result = result.Success ? AuditResult.Success : AuditResult.Failure,
            NewValue = string.Join("; ", result.Steps.Select(s => $"{s.Name}: {(s.Passed ? "ok" : "FAILED")}")),
        });
        return Ok(result);
    }

    /// <summary>Group picker for the allowlist editor (users without ad.groups.read can still manage settings).</summary>
    [HttpGet("groups"), Authorize(Policy = Permissions.SettingsManage)]
    public async Task<IActionResult> Groups([FromQuery] string? q, [FromQuery] int page = 1, CancellationToken ct = default) =>
        Ok(await ad.SearchGroupsAsync(q, page, 25, ct));

    private AdSettings Validate(AdSettings v)
    {
        var baseDn = ad.Provider.BaseDn;
        List<string> Dns(IEnumerable<string>? input, string label, bool underBase = true)
        {
            var list = new List<string>();
            foreach (var raw in input ?? [])
            {
                var dn = (raw ?? "").Trim();
                if (dn.Length == 0) continue;
                if (!DnText.IsValidDn(dn)) throw new ApiException(400, "Invalid distinguished name", $"'{dn}' in {label} is not a valid distinguished name.", "validation");
                if (underBase && !DnText.IsUnderOrEqual(dn, baseDn)) throw new ApiException(400, "Outside the domain", $"'{dn}' in {label} is not inside {baseDn}.", "validation");
                if (!list.Any(x => DnText.Equal(x, dn))) list.Add(dn);
            }
            return list;
        }

        var employee = string.IsNullOrWhiteSpace(v.EmployeeIdAttribute) ? "employeeID" : v.EmployeeIdAttribute.Trim();
        if (!AttributeName().IsMatch(employee)) throw new ApiException(400, "Invalid attribute", "The employee ID attribute must be a plain attribute name such as employeeID.", "validation");
        var lastUser = string.IsNullOrWhiteSpace(v.ComputerLastUserAttribute) ? null : v.ComputerLastUserAttribute.Trim();
        if (lastUser != null && !AttributeName().IsMatch(lastUser)) throw new ApiException(400, "Invalid attribute", "The last logged-in user attribute must be a plain attribute name.", "validation");
        if (v.SearchResultLimit is < 50 or > 5000) throw new ApiException(400, "Invalid limit", "The search result limit must be between 50 and 5000.", "validation");

        return new AdSettings
        {
            ManageableUserOus = Dns(v.ManageableUserOus, "manageable user OUs"),
            ManageableComputerOus = Dns(v.ManageableComputerOus, "manageable computer OUs"),
            ProtectedOus = Dns(v.ProtectedOus, "protected OUs"),
            ManageableGroups = Dns(v.ManageableGroups, "manageable groups"),
            ProtectedGroups = (v.ProtectedGroups ?? []).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            EmployeeIdAttribute = employee,
            ComputerLastUserAttribute = lastUser,
            SearchResultLimit = v.SearchResultLimit,
        };
    }
}
