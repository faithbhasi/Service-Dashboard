using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ServiceDashboard.Services;

namespace ServiceDashboard.Middleware;

/// <summary>Put on a module's controllers. A disabled module answers every route with the same "module disabled" error.</summary>
public sealed class ModuleGateAttribute : TypeFilterAttribute
{
    public ModuleGateAttribute(string moduleId) : base(typeof(ModuleGateFilter)) => Arguments = [moduleId];
}

public sealed class ModuleGateFilter(string moduleId, ModuleCatalog catalog) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (await catalog.IsEnabledAsync(moduleId))
        {
            await next();
            return;
        }
        var pd = new ProblemDetails { Status = 403, Title = "Module disabled", Detail = $"The '{moduleId}' module is disabled." };
        pd.Extensions["code"] = "module_disabled";
        pd.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
        context.Result = new ObjectResult(pd) { StatusCode = 403, ContentTypes = { "application/problem+json" } };
    }
}
