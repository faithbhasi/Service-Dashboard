using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ServiceDashboard.Configuration;
using ServiceDashboard.Data;
using ServiceDashboard.Models;
using ServiceDashboard.Services;

namespace ServiceDashboard.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    IOptions<OktaOptions> okta, IHostEnvironment env, IAntiforgery antiforgery, AppDbContext db,
    UserProvisioning provisioning, ICurrentUser currentUser, IAuditService audit, SettingsService settings) : ControllerBase
{
    private bool DevSignIn => okta.Value.DevelopmentSignIn && env.IsDevelopment();

    /// <summary>Public bootstrap info for the login page. Also hands out the anti-forgery token for anonymous posts.</summary>
    [HttpGet("config"), AllowAnonymous]
    public async Task<IActionResult> GetConfig()
    {
        var general = await settings.GetGeneralAsync();
        return Ok(new { devSignIn = DevSignIn, productName = general.ProductName, csrfToken = Token() });
    }

    [HttpGet("login"), AllowAnonymous]
    public IActionResult Login([FromQuery] string? returnUrl)
    {
        var target = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";
        if (DevSignIn) return Redirect("/login");
        return Challenge(new AuthenticationProperties { RedirectUri = target }, AuthSetup.OidcScheme);
    }

    [HttpGet("dev-users"), AllowAnonymous]
    public async Task<IActionResult> DevUsers()
    {
        if (!DevSignIn) return NotFound();
        var users = await db.Users.AsNoTracking().Where(u => u.Subject.StartsWith("dev|"))
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).OrderBy(u => u.DisplayName).ToListAsync();
        return Ok(users.Select(u => new
        {
            id = u.Id, displayName = u.DisplayName, email = u.Email, isEnabled = u.IsEnabled,
            roles = u.UserRoles.Select(r => r.Role!.Name).ToArray(),
        }));
    }

    public sealed record DevLoginRequest(Guid UserId);

    [HttpPost("dev-login"), AllowAnonymous]
    public async Task<IActionResult> DevLogin([FromBody] DevLoginRequest req)
    {
        if (!DevSignIn) return NotFound();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == req.UserId && u.Subject.StartsWith("dev|"));
        if (user == null) return Problem(statusCode: 404, title: "Unknown development user");

        var outcome = await provisioning.SignInAsync(user.Subject, user.Email, user.DisplayName,
            AccessService.ParseGroups(user.OktaGroupsJson));
        if (outcome.User == null)
            return Problem(statusCode: 403, title: "Access disabled", detail: "This user is disabled in the app.");

        await HttpContext.SignInAsync(AuthSetup.CookieScheme, UserProvisioning.BuildPrincipal(outcome.User, AuthSetup.CookieScheme));
        return Ok(new { ok = true });
    }

    /// <summary>Posted by a normal HTML form so the browser can follow the redirect to Okta's sign-out page.</summary>
    [HttpPost("logout"), AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        if (User.Identity?.IsAuthenticated == true)
            await audit.WriteAsync(new AuditEntry { Category = AuditCategory.Logon, Action = "logon.signout" });

        var props = new AuthenticationProperties { RedirectUri = "/login?signedOut=1" };
        if (DevSignIn)
        {
            await HttpContext.SignOutAsync(AuthSetup.CookieScheme);
            return Redirect("/login?signedOut=1");
        }
        return SignOut(props, AuthSetup.CookieScheme, AuthSetup.OidcScheme);
    }

    [HttpGet("me"), Authorize]
    public async Task<IActionResult> Me()
    {
        var user = await currentUser.GetAsync();
        if (user == null || !user.IsEnabled) return Problem(statusCode: 401, title: "Not signed in");
        var general = await settings.GetGeneralAsync();
        return Ok(new
        {
            id = user.Id,
            displayName = user.DisplayName,
            email = user.Email,
            initials = user.Initials,
            hasAccess = user.HasAccess,
            roles = user.Roles.Select(r => new { r.Id, r.Name, r.Source }),
            roleName = string.Join(", ", user.Roles.Select(r => r.Name)),
            permissions = user.Permissions.OrderBy(p => p),
            preferences = new { theme = user.ThemePreference, navCollapsed = user.NavCollapsed },
            supportContact = general.SupportContact,
            csrfToken = Token(),
        });
    }

    public sealed record PreferencesRequest(string Theme, bool NavCollapsed);

    [HttpPut("preferences"), Authorize]
    public async Task<IActionResult> SetPreferences([FromBody] PreferencesRequest req)
    {
        if (req.Theme is not ("light" or "dark" or "system"))
            return Problem(statusCode: 400, title: "Theme must be light, dark or system");
        var user = await currentUser.GetAsync();
        if (user == null) return Unauthorized();
        var row = await db.Users.FirstAsync(u => u.Id == user.Id);
        row.ThemePreference = req.Theme;
        row.NavCollapsed = req.NavCollapsed;
        await db.SaveChangesAsync();
        return NoContent();
    }

    public sealed record PageAccessRequest(string Page);

    /// <summary>Records that the user opened a page (Application Access log) and whether they were allowed.</summary>
    [HttpPost("access"), Authorize]
    public async Task<IActionResult> RecordAccess([FromBody] PageAccessRequest req)
    {
        var user = await currentUser.GetAsync();
        if (user == null) return Unauthorized();
        if (!PageAccess.Required.TryGetValue(req.Page, out var required))
            return Problem(statusCode: 400, title: "Unknown page");

        var allowed = required.Length == 0 || user.HasAny(required);
        await audit.WriteAsync(new AuditEntry
        {
            Category = AuditCategory.Access,
            Action = "page.view",
            Module = req.Page.StartsWith("ad.") ? "ad" : "core",
            Target = req.Page,
            Result = allowed ? AuditResult.Success : AuditResult.Denied,
            Error = allowed ? null : "Missing permission",
        });
        return Ok(new { allowed });
    }

    private string Token() => antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? "";
}

/// <summary>Which permissions open each front-end page (any one is enough). Mirrors the navigation.</summary>
public static class PageAccess
{
    public static readonly IReadOnlyDictionary<string, string[]> Required = new Dictionary<string, string[]>
    {
        ["home"] = [],
        ["ad.users"] = [Permissions.AdUsersRead],
        ["ad.computers"] = [Permissions.AdComputersRead],
        ["ad.groups"] = [Permissions.AdGroupsRead],
        ["logs"] = [Permissions.LogsRead, Permissions.LogsReadOwn],
        ["admin"] = [Permissions.AdminUsersManage, Permissions.AdminRolesManage],
        ["settings"] = [Permissions.SettingsRead, Permissions.SettingsManage, Permissions.SettingsPersonalizationManage],
    };
}
