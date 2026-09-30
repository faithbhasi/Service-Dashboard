using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ServiceDashboard.Middleware;

/// <summary>Requires a valid anti-forgery token (X-XSRF-TOKEN header or form field) on every state-changing API request.</summary>
public sealed class AntiforgeryFilter(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method))
            return;

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            var pd = new ProblemDetails
            {
                Status = 400, Title = "Request could not be verified",
                Detail = "The security token is missing or expired. Reload the page and try again.",
            };
            pd.Extensions["code"] = "antiforgery";
            pd.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
            context.Result = new ObjectResult(pd) { StatusCode = 400, ContentTypes = { "application/problem+json" } };
        }
    }
}
