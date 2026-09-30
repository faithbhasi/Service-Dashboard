using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ServiceDashboard.Models;

namespace ServiceDashboard.Middleware;

/// <summary>Turns unhandled exceptions into safe Problem Details. Stack traces and internals never reach the client.</summary>
public sealed class GlobalExceptionHandler(IProblemDetailsService problems, ILogger<GlobalExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        int status; string title; string detail; string code;
        switch (ex)
        {
            case ModuleUnavailableException mue:
                (status, title, detail, code) = (503, "Service unavailable", mue.SafeMessage, "module_unavailable");
                log.LogError(ex, "Module unavailable");
                break;
            case ApiException api:
                (status, title, detail, code) = (api.Status, api.Title, api.Detail, api.Code);
                break;
            case OperationCanceledException when ctx.RequestAborted.IsCancellationRequested:
                return true;
            default:
                (status, title, detail, code) = (500, "Unexpected error", "Something went wrong. Quote the correlation ID when reporting this.", "server_error");
                log.LogError(ex, "Unhandled exception");
                break;
        }

        ctx.Response.StatusCode = status;
        var pd = new ProblemDetails { Status = status, Title = title, Detail = detail };
        pd.Extensions["code"] = code;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = ctx, ProblemDetails = pd, Exception = null });
    }
}
