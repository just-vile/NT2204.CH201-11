using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Saga.Shared.Infrastructure;

public static class TelemetryConstants
{
    public const string CorrelationHeader = "X-Correlation-ID";
    public const string CorrelationLogProperty = "CorrelationId";

    /// <summary>
    /// Common ActivitySource / Meter name for saga-specific custom telemetry.
    /// Services pass this name to <see cref="Activity"/> and <see cref="Meter"/> factories
    /// to keep saga signals discoverable.
    /// </summary>
    public const string SagaActivitySourceName = "Saga.Choreography";
    public const string SagaMeterName = "Saga.Choreography";
}
