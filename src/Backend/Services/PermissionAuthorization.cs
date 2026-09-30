using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http.Extensions;
using ServiceDashboard.Models;

namespace ServiceDashboard.Services;

/// <summary>Satisfied when the user holds at least one of the listed permissions.</summary>
public sealed class PermissionRequirement(params string[] anyOf) : IAuthorizationRequirement
{
    public IReadOnlyList<string> AnyOf { get; } = anyOf;
}

public sealed class PermissionHandler(ICurrentUser currentUser) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var user = await currentUser.GetAsync();
        if (user != null && user.HasAny([.. requirement.AnyOf])) context.Succeed(requirement);
    }
}

public static class PermissionPolicyRegistration
{
    public static void AddPermissionPolicies(this AuthorizationOptions options)
    {
        foreach (var p in Permissions.All)
            options.AddPolicy(p.Id, b => b.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(p.Id)));
        foreach (var (name, perms) in PermissionPolicies.Combined)
            options.AddPolicy(name, b => b.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(perms)));
    }
}

/// <summary>Returns Problem Details for API auth failures and audits every denied attempt.</summary>
public sealed class ApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Succeeded || !context.Request.Path.StartsWithSegments("/api"))
        {
            await _default.HandleAsync(next, context, policy, result);
            return;
        }

        if (result.Challenged)
        {
            await Problem(context, StatusCodes.Status401Unauthorized, "Not signed in", "Sign in to continue.", "unauthenticated");
            return;
        }

        var audit = context.RequestServices.GetRequiredService<IAuditService>();
        var path = context.Request.Path.Value ?? "";
        await audit.WriteAsync(new AuditEntry
        {
            Category = AuditCategory.Access,
            Action = "api.access",
            Module = ModuleOf(path),
            Target = $"{context.Request.Method} {path}",
            Result = AuditResult.Denied,
            Error = "Missing permission",
        });
        await Problem(context, StatusCodes.Status403Forbidden, "Access denied", "You do not have permission to do this.", "forbidden");
    }

    public static string ModuleOf(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is ["api", "modules", var id, ..] ? id : "core";
    }

    private static async Task Problem(HttpContext ctx, int status, string title, string detail, string code)
    {
        ctx.Response.StatusCode = status;
        var pd = new Microsoft.AspNetCore.Mvc.ProblemDetails { Status = status, Title = title, Detail = detail };
        pd.Extensions["code"] = code;
        pd.Extensions["correlationId"] = ctx.TraceIdentifier;
        await ctx.Response.WriteAsJsonAsync(pd, options: null, contentType: "application/problem+json");
    }
}
