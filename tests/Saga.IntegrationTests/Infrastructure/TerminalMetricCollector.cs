using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Saga.Shared.Infrastructure;

namespace Saga.IntegrationTests.Infrastructure;

/// <summary>
/// In-process listener for the <c>saga.terminal</c> counter on the
/// <see cref="TelemetryConstants.SagaMeterName"/> meter. Mirrors the pattern of
/// <see cref="EventCollector"/>: every recorded measurement (with its tags) lands
/// in a thread-safe queue so tests can assert that the metric was emitted.
/// </summary>
public sealed class TerminalMetricCollector : IDisposable
{
    private readonly ConcurrentQueue<TerminalMeasurement> _measurements = new();
    private readonly MeterListener _listener;

    public IReadOnlyCollection<TerminalMeasurement> All => _measurements.ToArray();

    public TerminalMetricCollector()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == TelemetryConstants.SagaMeterName
                    && instrument.Name == "saga.terminal")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.Start();
    }

    private void OnMeasurement(
        Instrument instrument,
        long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        string? outcome = null;
        string? reason = null;
        foreach (var tag in tags)
        {
            switch (tag.Key)
            {
                case "outcome": outcome = tag.Value?.ToString(); break;
                case "reason": reason = tag.Value?.ToString(); break;
            }
        }

        _measurements.Enqueue(new TerminalMeasurement(
            measurement, outcome, reason, DateTimeOffset.UtcNow));
    }

    public async Task<TerminalMeasurement?> WaitFor(
        Predicate<TerminalMeasurement> match,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var found = _measurements.FirstOrDefault(m => match(m));
            if (found is not null) return found;
            await Task.Delay(100, ct);
        }
        return null;
    }

    public void Dispose() => _listener.Dispose();
}

public sealed record TerminalMeasurement(
    long Value,
    string? Outcome,
    string? Reason,
    DateTimeOffset ObservedAt);
