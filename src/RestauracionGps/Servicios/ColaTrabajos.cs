using System.Collections.Concurrent;
using System.Threading.Channels;

namespace RestauracionGps.Servicios;

/// <summary>Cola en memoria de ids de trabajo. El estado real vive en MySQL.</summary>
public sealed class ColaTrabajos
{
    private readonly Channel<string> _canal = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    // Ids encolados y aún no tomados por el worker (evita duplicados al re-encolar).
    private readonly ConcurrentDictionary<string, byte> _enCola = new();

    public string? EnProceso { get; private set; }

    public int Pendientes => _enCola.Count;

    public bool Encolar(string jobId)
    {
        if (jobId == EnProceso || !_enCola.TryAdd(jobId, 0)) return false;
        return _canal.Writer.TryWrite(jobId);
    }

    public async IAsyncEnumerable<string> LeerAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var id in _canal.Reader.ReadAllAsync(ct))
        {
            _enCola.TryRemove(id, out _);
            EnProceso = id;
            try { yield return id; }
            finally { EnProceso = null; }
        }
    }
}
