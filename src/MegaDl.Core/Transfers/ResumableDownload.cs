using Microsoft.Win32.SafeHandles;
using MegaDl.Core.Api;
using MegaDl.Core.Crypto;
using MegaDl.Core.Proxy;

namespace MegaDl.Core.Transfers;

/// <summary>
/// One file's download state, kept alive across pause/resume within a session.
///
/// Unlike <see cref="FileDownloader.DownloadAsync"/> (one call, start to finish — used by
/// the console), <see cref="RunAsync"/> can be called again after a cancellation: it skips
/// whatever download chunks already finished, so pausing never throws away progress.
/// The streaming meta-MAC fold (see <see cref="MegaMac"/>) lives on this instance too, so
/// it keeps working across those calls — it doesn't know or care that a pause happened.
///
/// Java port note: Download's resumable half of TransferenceManager/DownloadManager.
/// </summary>
public sealed class ResumableDownload : IDisposable
{
    private readonly RemoteFileInfo _file;
    private readonly string _partPath;
    private readonly FileDownloader _chunkFetcher;
    private readonly DownloadOptions _opt;
    private readonly List<Chunk> _downloadChunks;
    private readonly List<Chunk> _macChunks;     // multiplier 1 — MEGA's MAC boundaries
    private readonly HashSet<long> _doneChunkIds = [];
    private readonly SortedDictionary<long, byte[]> _pendingMacs = [];
    private readonly Lock _lock = new();
    private readonly MegaMac _mac;
    private long _nextMacId = 1;
    private long _bytesDone;
    private SafeFileHandle? _handle;
    private bool _disposed;

    public string DestinationPath { get; }
    public long BytesDone => Interlocked.Read(ref _bytesDone);

    /// <summary>Snapshot of which (big) download chunks are already down — persisted across
    /// app restarts (see <see cref="MegaDl.Core.Transfers.TransferManager"/>'s queue save/restore)
    /// and replayed via <see cref="SeedCompletedChunksAsync"/>.</summary>
    public IReadOnlyCollection<long> DoneChunkIds
    {
        get { lock (_lock) return _doneChunkIds.ToArray(); }
    }

    public ResumableDownload(HttpClient http, RemoteFileInfo file, string destinationPath, DownloadOptions? options = null,
        ProxyPool? proxyPool = null, SmartProxySettings? proxySettings = null)
    {
        _file = file;
        DestinationPath = destinationPath;
        _partPath = destinationPath + ".part";
        _opt = options ?? new DownloadOptions();
        _chunkFetcher = new FileDownloader(http, _opt, proxyPool, proxySettings);
        _downloadChunks = ChunkLayout.For(file.Size, _opt.ChunkMultiplier).Where(c => c.Size > 0).ToList();
        _macChunks = ChunkLayout.For(file.Size).ToList();
        _mac = new MegaMac(file.Key);
        Interlocked.Exchange(ref _bytesDone, 0);
    }

    /// <summary>
    /// Downloads whatever chunks aren't done yet. Returns true once the whole file is
    /// down and the meta-MAC matches (and the file has been moved to its final name).
    /// Cancelling <paramref name="ct"/> (a pause) leaves already-finished chunks in place
    /// for the next call; other exceptions (quota, API, I/O) propagate to the caller.
    /// </summary>
    public async Task<bool> RunAsync(CancellationToken ct)
    {
        // .NET only accepts preallocationSize with FileMode.CreateNew/Create — OpenOrCreate
        // rejects it outright even when the file doesn't exist yet, so pick the mode explicitly.
        if (_handle is null)
        {
            _handle = File.Exists(_partPath)
                ? File.OpenHandle(_partPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous)
                : File.OpenHandle(_partPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous, preallocationSize: _file.Size);
        }

        List<Chunk> remaining;
        lock (_lock) remaining = _downloadChunks.Where(c => !_doneChunkIds.Contains(c.Id)).ToList();

        if (remaining.Count > 0)
        {
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = _opt.Workers, CancellationToken = ct };
            await Parallel.ForEachAsync(remaining, parallel, async (chunk, token) =>
            {
                byte[] data = await _chunkFetcher.FetchChunkAsync(_file.DownloadUrl, chunk, _file.Size, token);

                using (var ctr = new AesCtr(_file.Key))
                    ctr.Transform(data, data, chunk.Offset);

                await RandomAccess.WriteAsync(_handle!, data, chunk.Offset, token);
                Interlocked.Add(ref _bytesDone, data.Length);
                FoldMac(chunk, data);

                lock (_lock) _doneChunkIds.Add(chunk.Id);
            });
        }

        _handle.Dispose();
        _handle = null;

        bool ok = _mac.Finish().AsSpan().SequenceEqual(_file.Key.MetaMac);
        if (ok) File.Move(_partPath, DestinationPath, overwrite: true);
        return ok;
    }

    /// <summary>
    /// Marks the given download chunks as already done, from a previous session's
    /// <see cref="DoneChunkIds"/> — read back from the existing ".part" file on disk rather
    /// than re-downloaded, and folded into the meta-MAC exactly as if they'd just landed
    /// (the MAC state itself is in-memory only, so it has to be rebuilt this way after a
    /// restart). Best-effort: if the .part file is missing or doesn't match (e.g. deleted,
    /// corrupted, or MEGA's reported size changed), this quietly seeds nothing and the
    /// chunks are re-downloaded instead of the restore crashing the job.
    /// </summary>
    public async Task SeedCompletedChunksAsync(IEnumerable<long> downloadChunkIds, CancellationToken ct = default)
    {
        var ids = new HashSet<long>(downloadChunkIds);
        if (ids.Count == 0 || !File.Exists(_partPath)) return;

        try
        {
            _handle ??= File.OpenHandle(_partPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);

            foreach (Chunk chunk in _downloadChunks.Where(c => ids.Contains(c.Id)).OrderBy(c => c.Id))
            {
                byte[] data = new byte[chunk.Size];
                await RandomAccess.ReadAsync(_handle, data, chunk.Offset, ct).ConfigureAwait(false);
                FoldMac(chunk, data);
                lock (_lock) _doneChunkIds.Add(chunk.Id);
                Interlocked.Add(ref _bytesDone, data.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Couldn't read it back — treat as if nothing was seeded; RunAsync will just
            // re-download these chunks (and MAC folding for them hasn't started, so no
            // partial fold to unwind).
        }
    }

    /// <summary>Folds one downloaded (big) chunk's MAC in. Buffers out-of-order arrivals until contiguous.</summary>
    private void FoldMac(Chunk chunk, byte[] decrypted)
    {
        foreach (Chunk sub in _macChunks.Where(c => c.Offset >= chunk.Offset && c.End <= chunk.End))
        {
            byte[] subMac = _mac.ComputeChunkMac(decrypted.AsSpan((int)(sub.Offset - chunk.Offset), (int)sub.Size));
            lock (_lock)
            {
                _pendingMacs[sub.Id] = subMac;
                while (_pendingMacs.TryGetValue(_nextMacId, out byte[]? ready))
                {
                    _mac.AppendChunkMac(_nextMacId, ready);
                    _pendingMacs.Remove(_nextMacId);
                    _nextMacId++;
                }
            }
        }
    }

    /// <summary>Stops for good: closes the handle and deletes the partial file.</summary>
    public void Cancel()
    {
        _handle?.Dispose();
        _handle = null;
        try { File.Delete(_partPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle?.Dispose();
        _mac.Dispose();
    }
}
