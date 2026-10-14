using MegaDl.Core.Links;
using MegaDl.Core.Transfers;

namespace MegaDl.Desktop.Services;

/// <summary>
/// A fake engine with the same contract as the real one, so the UI can be built and
/// tuned before Phase 3. It simulates: a queue with max 3 concurrent transfers, a
/// "fetching metadata" delay, jittery speeds, pause/resume, and the occasional
/// quota failure (HTTP 509), which is MegaBasterd's most common real-world error.
/// </summary>
public sealed class SimulatedTransferEngine : ITransferEngine, IDisposable
{
    private const int MaxConcurrent = 3;

    private sealed class Sim
    {
        public required Guid Id;
        public required string Name;
        public required long Size;
        public long Done;
        public double Speed;
        public TransferState State = TransferState.Queued;
        public string? Error;
        public double StartingFor;          // seconds spent in Starting
        public bool WillFail;
    }

    private readonly List<Sim> _items = [];
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();

    public SimulatedTransferEngine() => _ = Task.Run(() => LoopAsync(_cts.Token));

    public Guid Enqueue(MegaLink link, string destinationDirectory)
    {
        var sim = new Sim
        {
            Id = Guid.NewGuid(),
            Name = link.Kind == MegaLinkKind.Folder ? $"[folder] {link.Id}" : $"{link.Id}.mkv",
            Size = Random.Shared.NextInt64(40L << 20, 3L << 30),   // 40 MB – 3 GB
            WillFail = Random.Shared.Next(8) == 0,
        };
        lock (_lock) _items.Add(sim);
        return sim.Id;
    }

    public void Pause(Guid id) => With(id, s =>
    {
        if (s.State is TransferState.Queued or TransferState.Starting or TransferState.Running)
            (s.State, s.Speed) = (TransferState.Paused, 0);
    });

    public void Resume(Guid id) => With(id, s =>
    {
        if (s.State is TransferState.Paused or TransferState.Failed)
            (s.State, s.Error, s.WillFail) = (TransferState.Queued, null, false);
    });

    public void Cancel(Guid id) { lock (_lock) _items.RemoveAll(s => s.Id == id); }

    public IReadOnlyList<TransferSnapshot> Snapshot()
    {
        lock (_lock)
            return _items.Select(s => new TransferSnapshot(s.Id, s.Name, s.Size, s.Done, s.Speed, s.State, s.Error)).ToList();
    }

    private void With(Guid id, Action<Sim> action)
    {
        lock (_lock) { var s = _items.Find(x => x.Id == id); if (s is not null) action(s); }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        const double dt = 0.05; // the engine ticks at 20 Hz — faster than the UI samples, on purpose
        while (!ct.IsCancellationRequested)
        {
            lock (_lock)
            {
                int active = _items.Count(s => s.State is TransferState.Starting or TransferState.Running);
                foreach (var s in _items.Where(s => s.State == TransferState.Queued))
                    if (active++ < MaxConcurrent) s.State = TransferState.Starting;

                foreach (var s in _items) Step(s, dt);
            }
            await Task.Delay(TimeSpan.FromSeconds(dt), ct).ConfigureAwait(false);
        }
    }

    private static void Step(Sim s, double dt)
    {
        switch (s.State)
        {
            case TransferState.Starting:
                if ((s.StartingFor += dt) > 0.8) s.State = TransferState.Running;
                break;

            case TransferState.Running:
                double target = Random.Shared.Next(4, 16) * 1024 * 1024;          // 4–16 MB/s
                s.Speed = s.Speed * 0.9 + target * 0.1;                             // smooth the jitter
                s.Done = Math.Min(s.Size, s.Done + (long)(s.Speed * dt * 20));     // ×20 so demos finish fast
                if (s.WillFail && s.Done > s.Size * 0.4)
                    (s.State, s.Speed, s.Error) = (TransferState.Failed, 0, "HTTP 509: transfer quota exceeded");
                else if (s.Done == s.Size)
                    (s.State, s.Speed) = (TransferState.Verifying, 0);
                break;

            case TransferState.Verifying:
                s.State = TransferState.Completed;
                break;
        }
    }

    public void Dispose() => _cts.Cancel();
}
