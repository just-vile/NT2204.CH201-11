namespace Saga.WebUI.Services;

/// <summary>
/// Tiny in-memory log of orders this UI instance has placed. Survives across users in this process
/// (singleton) so multiple browser sessions watching the demo see the same list. Capped at 50.
/// </summary>
public class OrderTracker
{
    private const int Cap = 50;
    private readonly object _lock = new();
    private readonly LinkedList<TrackedOrder> _items = new();

    public IReadOnlyList<TrackedOrder> Recent
    {
        get { lock (_lock) return _items.ToArray(); }
    }

    public void Track(Guid id, string label)
    {
        lock (_lock)
        {
            _items.AddFirst(new TrackedOrder(id, label, DateTimeOffset.UtcNow));
            while (_items.Count > Cap) _items.RemoveLast();
        }
    }
}
