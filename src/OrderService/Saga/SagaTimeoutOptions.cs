using System.ComponentModel.DataAnnotations;

namespace Saga.OrderService.Saga;

/// <summary>
/// Strongly-typed configuration for <see cref="OrderTimeoutWatchdog"/>. Bound from the
/// <c>Saga:Timeout</c> section. <see cref="Total"/> is the saga SLA after which the
/// watchdog emits a synthetic failure event; <see cref="ScanInterval"/> is how often
/// the watchdog polls for stalled orders.
/// </summary>
public sealed class SagaTimeoutOptions
{
    public const string SectionName = "Saga:Timeout";

    [Range(typeof(TimeSpan), "00:00:00.500", "01:00:00")]
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(5);

    [Range(typeof(TimeSpan), "00:00:01", "24:00:00")]
    public TimeSpan Total { get; set; } = TimeSpan.FromMinutes(2);
}
