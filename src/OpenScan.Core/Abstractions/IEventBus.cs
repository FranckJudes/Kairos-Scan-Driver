using OpenScan.Contracts;

namespace OpenScan.Core.Abstractions;

public interface IEventBus
{
    Task PublishAsync(AgentEvent evt, CancellationToken ct = default);

    IDisposable Subscribe(Func<AgentEvent, Task> handler);
}
