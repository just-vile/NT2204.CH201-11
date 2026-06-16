using Microsoft.AspNetCore.Http;
using Saga.Shared.Infrastructure;

namespace Saga.WebUI.Services;

/// <summary>
/// Propagates the inbound <c>X-Correlation-ID</c> from the active HTTP request onto every
/// outbound API call made through a typed HttpClient. Keeps the trace contiguous from
/// browser → WebUI → backend services in Jaeger and Serilog.
/// </summary>
public sealed class CorrelationForwardingHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(TelemetryConstants.CorrelationHeader))
        {
            var correlationId = accessor.HttpContext?.GetCorrelationId();
            if (correlationId is { } id && id != Guid.Empty)
            {
                request.Headers.TryAddWithoutValidation(TelemetryConstants.CorrelationHeader, id.ToString());
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
