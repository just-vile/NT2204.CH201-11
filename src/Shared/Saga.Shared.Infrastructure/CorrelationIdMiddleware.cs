using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace Saga.Shared.Infrastructure;

public static class CorrelationIdMiddleware
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            var header = ctx.Request.Headers[TelemetryConstants.CorrelationHeader].FirstOrDefault();
            var correlationId = !string.IsNullOrWhiteSpace(header) && Guid.TryParse(header, out var parsed)
                ? parsed
                : Guid.NewGuid();

            ctx.Items[TelemetryConstants.CorrelationLogProperty] = correlationId;
            ctx.Response.Headers[TelemetryConstants.CorrelationHeader] = correlationId.ToString();

            using (LogContext.PushProperty(TelemetryConstants.CorrelationLogProperty, correlationId))
            {
                System.Diagnostics.Activity.Current?.SetTag("correlation.id", correlationId.ToString());
                await next();
            }
        });
    }

    public static Guid GetCorrelationId(this HttpContext ctx)
        => ctx.Items[TelemetryConstants.CorrelationLogProperty] is Guid g ? g : Guid.NewGuid();
}
