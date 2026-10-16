using System.Net;
using MegaDl.Core.Api;
using MegaDl.Core.Crypto;
using MegaDl.Core.Proxy;

namespace MegaDl.Core.Transfers;

public sealed record DownloadOptions(int Workers = 4, int ChunkMultiplier = 20, int MaxRetriesPerChunk = 5);

/// <summary>
/// Phase-1 download engine: N workers pull chunks in parallel, decrypt each with
/// AES-CTR at its own offset, and write it straight to its position in the file.
///
/// Mental model: a warehouse with N pickers. Each picker takes the next order slip
/// (chunk), fetches it, unwraps it (decrypt) and puts it on its numbered shelf
/// (file offset). Order of completion doesn't matter because every slot is pre-labelled.
///
/// Java port note: Download + ChunkDownloader + ChunkWriterManager. MegaBasterd
/// buffers chunks to temp files and has a dedicated writer thread that writes them in
/// order; positional writes (RandomAccess.WriteAsync) remove the need for that.
///
/// Phase 4 (smart proxy): once MEGA answers a chunk with 509 (quota exceeded), every
/// following chunk for *this* file goes through <paramref name="proxyPool"/> instead —
/// see <see cref="FetchChunkAsync"/>. <paramref name="proxySettings"/> is a snapshot taken
/// when the download started (see docs on <see cref="SmartProxySettings"/>).
/// </summary>
public sealed class FileDownloader(HttpClient http, DownloadOptions? options = null,
    ProxyPool? proxyPool = null, SmartProxySettings? proxySettings = null)
{
    private readonly DownloadOptions _opt = options ?? new DownloadOptions();
    private readonly SmartProxySettings _proxySettings = proxySettings ?? new SmartProxySettings();
    private ProxyEntry? _activeProxy;
    private long _bytesDone;

    /// <summary>Bytes written so far. Read this on a timer — don't push every update to a UI.</summary>
    public long BytesDone => Interlocked.Read(ref _bytesDone);

    /// <summary>
    /// Downloads to "&lt;destinationPath&gt;.part", computing MEGA's meta MAC as chunks
    /// land (no second full read at the end), then renames to <paramref name="destinationPath"/>
    /// only once the MAC is verified. Returns false (and leaves the .part file) on mismatch.
    /// </summary>
    public async Task<bool> DownloadAsync(RemoteFileInfo file, string destinationPath, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _bytesDone, 0);

        string partPath = destinationPath + ".part";
        using var handle = File.OpenHandle(partPath, FileMode.Create, FileAccess.ReadWrite,
            FileShare.Read, FileOptions.Asynchronous, preallocationSize: file.Size);

        // MEGA's MAC boundaries (multiplier 1) — the big download chunks below always
        // align on these (see ChunkLayout), so each download chunk is a whole number of them.
        var macChunks = ChunkLayout.For(file.Size).ToList();
        using var mac = new MegaMac(file.Key);
        var pendingMacs = new SortedDictionary<long, byte[]>();
        long nextMacId = 1;
        var macLock = new object();

        var parallel = new ParallelOptions { MaxDegreeOfParallelism = _opt.Workers, CancellationToken = ct };

        await Parallel.ForEachAsync(ChunkLayout.For(file.Size, _opt.ChunkMultiplier), parallel, async (chunk, token) =>
        {
            if (chunk.Size == 0) return;

            byte[] data = await FetchChunkAsync(file.DownloadUrl, chunk, file.Size, token);

            using (var ctr = new AesCtr(file.Key))
                ctr.Transform(data, data, chunk.Offset);           // decrypt in place

            await RandomAccess.WriteAsync(handle, data, chunk.Offset, token);
            Interlocked.Add(ref _bytesDone, data.Length);

            foreach (Chunk sub in macChunks.Where(c => c.Offset >= chunk.Offset && c.End <= chunk.End))
            {
                byte[] subMac = mac.ComputeChunkMac(data.AsSpan((int)(sub.Offset - chunk.Offset), (int)sub.Size));
                lock (macLock)
                {
                    pendingMacs[sub.Id] = subMac;
                    while (pendingMacs.TryGetValue(nextMacId, out byte[]? ready))
                    {
                        mac.AppendChunkMac(nextMacId, ready);
                        pendingMacs.Remove(nextMacId);
                        nextMacId++;
                    }
                }
            }
        });

        handle.Dispose();
        bool ok = mac.Finish().AsSpan().SequenceEqual(file.Key.MetaMac);
        if (ok) File.Move(partPath, destinationPath, overwrite: true);
        return ok;
    }

    /// <summary>
    /// Fetches and range-checks one encrypted chunk. Retries transient failures; on 509
    /// (or, once already in proxy mode, on any failure) switches to/rotates a proxy from
    /// <paramref name="fileUrl"/>'s pool instead of giving up — see the class doc.
    /// </summary>
    public async Task<byte[]> FetchChunkAsync(string fileUrl, Chunk chunk, long totalSize, CancellationToken ct)
    {
        // Same URL shape as ChunkWriterManager.genChunkUrl: open-ended range for the last chunk.
        string url = $"{fileUrl}/{chunk.Offset}" + (chunk.End == totalSize ? "" : $"-{chunk.End - 1}");

        if (proxyPool is not null && _proxySettings.Enabled && _proxySettings.Force && _activeProxy is null)
            if (!await TrySwitchProxyAsync(ct)) throw new TransferQuotaExceededException();

        for (int attempt = 1; ; attempt++)
        {
            bool usingProxy = _activeProxy is not null;
            HttpClient client = usingProxy ? proxyPool!.GetClient(_activeProxy!, TimeSpan.FromSeconds(_proxySettings.TimeoutSeconds)) : http;

            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

                if ((int)response.StatusCode == 509)
                {
                    if (!await TrySwitchProxyAsync(ct)) throw new TransferQuotaExceededException();
                    continue;
                }
                response.EnsureSuccessStatusCode();

                byte[] data = await response.Content.ReadAsByteArrayAsync(ct);
                if (data.Length != chunk.Size)
                    throw new IOException($"Chunk {chunk.Id}: expected {chunk.Size} bytes, got {data.Length}.");
                return data;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                       && !ct.IsCancellationRequested
                                       && (ex as HttpRequestException)?.StatusCode is not HttpStatusCode.Forbidden)
            {
                if (usingProxy)
                {
                    if (!await TrySwitchProxyAsync(ct)) throw new TransferQuotaExceededException();
                    continue;
                }
                if (attempt >= _opt.MaxRetriesPerChunk) throw;
                await Task.Delay(MegaApiClient.Backoff(attempt), ct);
            }
        }
    }

    /// <summary>
    /// Bans the current proxy (if any — a plain 509 on a direct connection just means
    /// "enter proxy mode", nothing to ban yet), then picks a replacement. If the pool is
    /// empty, refreshes it and waits 30s, up to 5 times, before giving up.
    /// </summary>
    private async Task<bool> TrySwitchProxyAsync(CancellationToken ct)
    {
        if (proxyPool is null || !_proxySettings.Enabled) return false;

        if (_activeProxy is not null)
            proxyPool.Ban(_activeProxy, TimeSpan.FromSeconds(_proxySettings.BanSeconds));

        ProxyEntry? next = proxyPool.Pick(_activeProxy?.Key);
        for (int round = 0; next is null && round < 5; round++)
        {
            await proxyPool.RefreshAsync(ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            next = proxyPool.Pick(_activeProxy?.Key);
        }

        if (next is null) return false;
        _activeProxy = next;
        return true;
    }
}
