using MegaDl.Core.Api;
using MegaDl.Core.Links;

namespace MegaDl.Core.Transfers;

/// <summary>
/// The real <see cref="ITransferEngine"/>: a queue of real MEGA downloads with a cap on
/// how many run at once, pause/resume/cancel/retry, and a moving-average speed meter.
///
/// One lifecycle per job (<see cref="RunLifecycleAsync"/>), re-entered on <see cref="Resume"/>:
/// it waits its turn on <see cref="_concurrency"/>, fetches metadata once, then drives its
/// <see cref="ResumableDownload"/> until done, cancelled (pause) or it throws (failed).
///
/// Java port note: TransferenceManager + DownloadManager + SpeedMeter, minus the database —
/// state lives in memory only (see docs/PORT_PLAN.md: queue persistence is a stretch goal).
/// </summary>
public sealed class TransferManager : ITransferEngine, IDisposable
{
    // Hard-coded engine knobs (see docs/PORT_PLAN.md Phase 3 — exposed in settings later).
    public const int DefaultMaxConcurrent = 3;
    public static readonly DownloadOptions DefaultDownloadOptions = new(Workers: 4);

    private sealed class Job
    {
        public required Guid Id;
        public required MegaLink Link;
        public required string DestinationDir;
        public string Name = "";
        public long Size;
        public double BytesPerSecond;
        public long LastSampleBytes;
        public TransferState State = TransferState.Queued;
        public string? Error;
        public bool CancelRequested;
        public RemoteFileInfo? Info;
        public ResumableDownload? Download;
        public CancellationTokenSource LifecycleCts = new();

        public long BytesDone => Download?.BytesDone ?? 0;
    }

    private readonly HttpClient _http;
    private readonly MegaApiClient _api;
    private readonly DownloadOptions _downloadOptions;
    private readonly SemaphoreSlim _concurrency;
    private readonly List<Job> _jobs = [];
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _speedLoop;

    public TransferManager(HttpClient http, int maxConcurrent = DefaultMaxConcurrent, DownloadOptions? downloadOptions = null)
    {
        _http = http;
        _api = new MegaApiClient(http);
        _downloadOptions = downloadOptions ?? DefaultDownloadOptions;
        _concurrency = new SemaphoreSlim(maxConcurrent);
        _speedLoop = Task.Run(() => SpeedLoopAsync(_shutdown.Token));
    }

    public Guid Enqueue(MegaLink link, string destinationDirectory)
    {
        var job = new Job { Id = Guid.NewGuid(), Link = link, DestinationDir = destinationDirectory, Name = link.Id };
        lock (_lock) _jobs.Add(job);
        _ = RunLifecycleAsync(job);
        return job.Id;
    }

    public void Pause(Guid id)
    {
        Job? job = Find(id);
        bool running;
        lock (_lock) running = job is not null && job.State is TransferState.Queued or TransferState.Starting or TransferState.Running;
        if (running) job!.LifecycleCts.Cancel();
    }

    public void Resume(Guid id)
    {
        Job? job = Find(id);
        if (job is null) return;

        bool start = false;
        lock (_lock)
        {
            if (job.State is TransferState.Paused or TransferState.Failed)
            {
                job.LifecycleCts.Dispose();
                job.LifecycleCts = new CancellationTokenSource();
                job.State = TransferState.Queued;
                job.Error = null;
                start = true;
            }
        }
        if (start) _ = RunLifecycleAsync(job);
    }

    public void Cancel(Guid id)
    {
        Job? job;
        bool activeRun;
        lock (_lock)
        {
            job = _jobs.Find(j => j.Id == id);
            if (job is null) return;
            job.CancelRequested = true;
            activeRun = job.State is TransferState.Queued or TransferState.Starting or TransferState.Running;
            if (!activeRun) _jobs.Remove(job);
        }

        // Active jobs get cleaned up by their own lifecycle task's catch block (below);
        // the rest (paused/failed/completed) have no running task left to do it.
        if (activeRun) job.LifecycleCts.Cancel();
        else job.Download?.Cancel();
    }

    public IReadOnlyList<TransferSnapshot> Snapshot()
    {
        lock (_lock)
            return _jobs.Select(j => new TransferSnapshot(j.Id, j.Name, j.Size, j.BytesDone, j.BytesPerSecond, j.State, j.Error)).ToList();
    }

    private Job? Find(Guid id)
    {
        lock (_lock) return _jobs.Find(j => j.Id == id);
    }

    private async Task RunLifecycleAsync(Job job)
    {
        CancellationToken token = job.LifecycleCts.Token;
        bool acquired = false;
        try
        {
            await _concurrency.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            lock (_lock) job.State = TransferState.Starting;

            if (job.Info is null)
            {
                RemoteFileInfo info = await _api.GetFileInfoAsync(job.Link, token).ConfigureAwait(false);
                lock (_lock) { job.Info = info; job.Name = info.Name; job.Size = info.Size; }
            }

            job.Download ??= new ResumableDownload(_http, job.Info!, Path.Combine(job.DestinationDir, job.Info!.Name), _downloadOptions);
            lock (_lock) job.State = TransferState.Running;

            bool ok = await job.Download.RunAsync(token).ConfigureAwait(false);

            lock (_lock)
            {
                job.State = ok ? TransferState.Completed : TransferState.Failed;
                job.Error = ok ? null : "Integrity check failed (MEGA meta MAC mismatch). Retry to re-download.";
                job.BytesPerSecond = 0;
            }
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            lock (_lock)
            {
                if (job.CancelRequested)
                {
                    _jobs.Remove(job);
                    job.Download?.Cancel();
                }
                else
                {
                    job.State = TransferState.Paused;
                }
                job.BytesPerSecond = 0;
            }
        }
        catch (OperationCanceledException)
        {
            // App shutdown — leave state as-is, nothing more to report.
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                job.State = TransferState.Failed;
                job.Error = Flatten(ex).Message;
                job.BytesPerSecond = 0;
            }
        }
        finally
        {
            if (acquired) _concurrency.Release();
        }
    }

    // Parallel.ForEachAsync wraps a single worker failure as-is but wraps 2+ concurrent
    // failures in an AggregateException; this unwraps either case to the first real cause.
    private static Exception Flatten(Exception ex) => ex is AggregateException { InnerExceptions: [var first, ..] } ? first : ex;

    private async Task SpeedLoopAsync(CancellationToken ct)
    {
        var tick = System.Diagnostics.Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            double dt = tick.Elapsed.TotalSeconds;
            tick.Restart();

            lock (_lock)
            {
                foreach (Job job in _jobs)
                {
                    if (job.State != TransferState.Running)
                    {
                        job.BytesPerSecond = 0;
                        job.LastSampleBytes = job.BytesDone;
                        continue;
                    }

                    long now = job.BytesDone;
                    double instant = dt > 0 ? (now - job.LastSampleBytes) / dt : 0;
                    job.BytesPerSecond = job.BytesPerSecond <= 0 ? instant : job.BytesPerSecond * 0.7 + instant * 0.3;
                    job.LastSampleBytes = now;
                }
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _speedLoop.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { /* best-effort shutdown */ }
        lock (_lock)
            foreach (Job job in _jobs) job.LifecycleCts.Cancel();
        _concurrency.Dispose();
        _shutdown.Dispose();
    }
}
