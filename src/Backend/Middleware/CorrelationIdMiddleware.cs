using System.Text.RegularExpressions;
using Serilog.Context;

namespace ServiceDashboard.Middleware;

/// <summary>Every request gets a correlation ID (reusing a well-formed incoming one). It is the ASP.NET TraceIdentifier.</summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string Header = "X-Correlation-ID";

    [GeneratedRegex("^[A-Za-z0-9\\-_]{8,64}$")]
    private static partial Regex Valid();

    public async Task InvokeAsync(HttpContext ctx)
    {
        var incoming = ctx.Request.Headers[Header].ToString();
        var id = Valid().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");
        ctx.TraceIdentifier = id;
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers[Header] = id;
            return Task.CompletedTask;
        });
        using (LogContext.PushProperty("CorrelationId", id))
            await next(ctx);
    }
}
