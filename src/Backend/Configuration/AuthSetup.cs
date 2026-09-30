using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ServiceDashboard.Data;
using ServiceDashboard.Models;
using ServiceDashboard.Services;

namespace ServiceDashboard.Configuration;

public static class AuthSetup
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;

    public static void AddAppAuthentication(this WebApplicationBuilder builder)
    {
        var okta = builder.Configuration.GetSection(OktaOptions.Section).Get<OktaOptions>() ?? new();
        var isDev = builder.Environment.IsDevelopment();

        var auth = builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = CookieScheme;
            o.DefaultChallengeScheme = CookieScheme; // API calls get a 401, never a redirect
        });

        auth.AddCookie(CookieScheme, o =>
        {
            o.Cookie.Name = "sd.session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = isDev ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            // Real idle and absolute limits come from Settings (checked below); this is only the outer bound.
            o.ExpireTimeSpan = TimeSpan.FromHours(24);
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx => Status(ctx, StatusCodes.Status401Unauthorized);
            o.Events.OnRedirectToAccessDenied = ctx => Status(ctx, StatusCodes.Status403Forbidden);
            o.Events.OnValidatePrincipal = SessionValidator.ValidateAsync;
        });

        if (okta.DevelopmentSignIn) return; // development sign-in replaces Okta entirely

        auth.AddOpenIdConnect(OidcScheme, o =>
        {
            o.SignInScheme = CookieScheme;
            o.Authority = okta.Issuer;
            o.ClientId = okta.ClientId;
            o.ClientSecret = okta.ClientSecret;
            o.ResponseType = "code";
            o.ResponseMode = "query";
            o.UsePkce = true;
            o.SaveTokens = true;
            o.MapInboundClaims = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.CallbackPath = "/signin-oidc";
            o.SignedOutCallbackPath = "/signout-callback-oidc";
            o.SignedOutRedirectUri = "/login?signedOut=1";
            o.Scope.Clear();
            foreach (var s in okta.Scopes) o.Scope.Add(s);
            o.TokenValidationParameters.NameClaimType = "name";
            o.Events = new OpenIdConnectEvents
            {
                OnTokenValidated = OnTokenValidated,
                // Keep only the ID token (needed to sign out of Okta). Access and refresh tokens are dropped.
                OnTicketReceived = ctx =>
                {
                    var idToken = ctx.Properties?.GetTokenValue("id_token");
                    ctx.Properties?.StoreTokens(idToken == null ? [] : [new AuthenticationToken { Name = "id_token", Value = idToken }]);
                    return Task.CompletedTask;
                },
                OnRemoteFailure = async ctx =>
                {
                    var audit = ctx.HttpContext.RequestServices.GetRequiredService<IAuditService>();
                    await audit.WriteAsync(new AuditEntry
                    {
                        Category = AuditCategory.Logon, Action = "logon.failed", Result = AuditResult.Failure,
                        Error = SafeFailureReason(ctx.Failure),
                    });
                    ctx.Response.Redirect("/login?error=failed");
                    ctx.HandleResponse();
                },
            };
        });
    }

    private static async Task OnTokenValidated(TokenValidatedContext ctx)
    {
        var sp = ctx.HttpContext.RequestServices;
        var okta = sp.GetRequiredService<IOptions<OktaOptions>>().Value;
        var p = ctx.Principal!;
        var subject = p.FindFirstValue("sub");
        if (string.IsNullOrEmpty(subject))
        {
            ctx.Fail("missing subject");
            return;
        }

        var groups = p.FindAll(string.IsNullOrEmpty(okta.GroupsClaim) ? "groups" : okta.GroupsClaim).Select(c => c.Value).ToList();
        var outcome = await sp.GetRequiredService<UserProvisioning>().SignInAsync(
            subject, p.FindFirstValue("email") ?? p.FindFirstValue("preferred_username"), p.FindFirstValue("name"), groups);

        if (outcome.User == null)
        {
            ctx.Response.Redirect("/login?error=disabled");
            ctx.HandleResponse();
            return;
        }
        ctx.Principal = UserProvisioning.BuildPrincipal(outcome.User, CookieScheme);
    }

    private static string SafeFailureReason(Exception? ex) => ex switch
    {
        null => "Sign-in failed",
        _ when ex.Message.Contains("access_denied", StringComparison.OrdinalIgnoreCase) => "Access denied by Okta",
        _ => "Sign-in could not be completed",
    };

    private static Task Status(RedirectContext<CookieAuthenticationOptions> ctx, int code)
    {
        ctx.Response.StatusCode = code;
        return Task.CompletedTask;
    }
}

/// <summary>Enforces the idle timeout, absolute lifetime and "user still enabled" on every request.</summary>
public static class SessionValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var sp = ctx.HttpContext.RequestServices;
        // HttpContext.User is not set yet at this point, so read the user straight from the ticket's principal.
        if (!Guid.TryParse(ctx.Principal?.FindFirstValue(AuditService.UidClaim), out var uid)) { await Reject(ctx); return; }
        var enabled = await sp.GetRequiredService<AppDbContext>().Users.AsNoTracking()
            .Where(u => u.Id == uid).Select(u => (bool?)u.IsEnabled).FirstOrDefaultAsync();
        if (enabled != true) { await Reject(ctx); return; }

        var general = await sp.GetRequiredService<SettingsService>().GetGeneralAsync();
        var now = DateTimeOffset.UtcNow;
        var signIn = Read(ctx.Principal, UserProvisioning.SignInClaim);
        var active = Read(ctx.Principal, UserProvisioning.ActiveClaim);
        if (signIn == null || active == null
            || now - signIn > TimeSpan.FromMinutes(general.AbsoluteTimeoutMinutes)
            || now - active > TimeSpan.FromMinutes(general.IdleTimeoutMinutes))
        {
            await Reject(ctx);
            return;
        }

        if (now - active > TimeSpan.FromSeconds(60))
        {
            var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
            var old = identity.FindFirst(UserProvisioning.ActiveClaim);
            if (old != null) identity.RemoveClaim(old);
            identity.AddClaim(new Claim(UserProvisioning.ActiveClaim, now.ToUnixTimeSeconds().ToString()));
            ctx.ReplacePrincipal(ctx.Principal);
            ctx.ShouldRenew = true;
        }
    }

    private static DateTimeOffset? Read(ClaimsPrincipal? p, string type) =>
        long.TryParse(p?.FindFirstValue(type), out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    private static async Task Reject(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(AuthSetup.CookieScheme);
    }
}
