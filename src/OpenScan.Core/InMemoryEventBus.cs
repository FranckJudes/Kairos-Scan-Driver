using System.Collections.Concurrent;
using OpenScan.Contracts;
using OpenScan.Core.Abstractions;

namespace OpenScan.Core;


public sealed class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Guid, Func<AgentEvent, Task>> _subscribers = new();

    public async Task PublishAsync(AgentEvent evt, CancellationToken ct = default)
    {
        foreach (var handler in _subscribers.Values)
        {
            await handler(evt);
        }
    }

    public IDisposable Subscribe(Func<AgentEvent, Task> handler)
    {
        var id = Guid.NewGuid();
        _subscribers[id] = handler;
        return new Subscription(() => _subscribers.TryRemove(id, out _));
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
