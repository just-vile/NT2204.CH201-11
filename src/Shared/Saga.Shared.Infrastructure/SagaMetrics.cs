using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Saga.Shared.Infrastructure;

/// <summary>
/// Custom business metrics for the choreography saga, emitted on the shared
/// <see cref="TelemetryConstants.SagaMeterName"/> meter so the existing
/// OpenTelemetry registration picks them up automatically.
/// </summary>
public static class SagaMetrics
{
    public static readonly Meter Meter = new(TelemetryConstants.SagaMeterName, "1.0.0");

    /// <summary>
    /// Counts saga executions that reached a terminal outcome (completed or cancelled).
    /// Tags: <c>outcome</c> ("completed" | "cancelled"), <c>reason</c> (free-form string).
    /// </summary>
    public static readonly Counter<long> Terminal = Meter.CreateCounter<long>(
        name: "saga.terminal",
        unit: "{order}",
        description: "Saga reached a terminal outcome");

    public static void RecordTerminal(string outcome, string reason)
    {
        Debug.Assert(
            outcome is "completed" or "cancelled",
            $"saga.terminal outcome must be 'completed' or 'cancelled', got '{outcome}'");

        Terminal.Add(1, new TagList
        {
            { "outcome", outcome },
            { "reason", reason }
        });
    }
}
