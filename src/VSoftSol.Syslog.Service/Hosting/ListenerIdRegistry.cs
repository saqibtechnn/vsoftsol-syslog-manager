using System.Collections.Concurrent;
using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// v1.1 — P2-1: the live Protocol → <c>listeners.listener_id</c> mapping. Populated once per
/// protocol at collector startup (<see cref="ListenerRegistrationHostedService"/>) and
/// updated again after a live UDP/TCP port change (<see cref="ListenerPortReloadService"/>).
/// <see cref="EventEnricher"/> reads it — a pure in-memory lookup, no per-event database
/// cost. A miss (nothing registered yet, or this process hosts no collector runtime) leaves
/// <see cref="Core.Events.SyslogEvent.ListenerId"/> at its default 0/unresolved — never a
/// reason to delay or drop an event.
/// </summary>
public sealed class ListenerIdRegistry
{
    private readonly ConcurrentDictionary<Protocol, long> _ids = new();

    public void SetId(Protocol protocol, long listenerId) => _ids[protocol] = listenerId;

    public bool TryGetId(Protocol protocol, out long listenerId) => _ids.TryGetValue(protocol, out listenerId);
}
