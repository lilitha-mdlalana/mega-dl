using System.Net;
using MegaDl.Core.Api;
using MegaDl.Core.Crypto;

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
/// Resume, pause, speed limits and proxies are later phases.
/// </summary>
public sealed class FileDownloader(HttpClient http, DownloadOptions? options = null)
{
    private readonly DownloadOptions _opt = options ?? new DownloadOptions();
    private long _bytesDone;

    /// <summary>Bytes written so far. Read this on a timer — don't push every update to a UI.</summary>
    public long BytesDone => Interlocked.Read(ref _bytesDone);

    public async Task DownloadAsync(RemoteFileInfo file, string destinationPath, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _bytesDone, 0);

        using var handle = File.OpenHandle(destinationPath, FileMode.Create, FileAccess.ReadWrite,
            FileShare.Read, FileOptions.Asynchronous, preallocationSize: file.Size);

        var parallel = new ParallelOptions { MaxDegreeOfParallelism = _opt.Workers, CancellationToken = ct };

        await Parallel.ForEachAsync(ChunkLayout.For(file.Size, _opt.ChunkMultiplier), parallel, async (chunk, token) =>
        {
            if (chunk.Size == 0) return;

            byte[] data = await FetchChunkAsync(file.DownloadUrl, chunk, file.Size, token);

            using (var ctr = new AesCtr(file.Key))
                ctr.Transform(data, data, chunk.Offset);           // decrypt in place

            await RandomAccess.WriteAsync(handle, data, chunk.Offset, token);
            Interlocked.Add(ref _bytesDone, data.Length);
        });
    }

    /// <summary>Re-reads the finished file and checks MEGA's meta MAC.</summary>
    public static async Task<bool> VerifyAsync(string path, FileKey key, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        byte[] mac = await MegaMac.ComputeAsync(stream, key, stream.Length, ct);
        return mac.AsSpan().SequenceEqual(key.MetaMac);
    }

    private async Task<byte[]> FetchChunkAsync(string fileUrl, Chunk chunk, long totalSize, CancellationToken ct)
    {
        // Same URL shape as ChunkWriterManager.genChunkUrl: open-ended range for the last chunk.
        string url = $"{fileUrl}/{chunk.Offset}" + (chunk.End == totalSize ? "" : $"-{chunk.End - 1}");

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode == 509) throw new TransferQuotaExceededException();
                response.EnsureSuccessStatusCode();

                byte[] data = await response.Content.ReadAsByteArrayAsync(ct);
                if (data.Length != chunk.Size)
                    throw new IOException($"Chunk {chunk.Id}: expected {chunk.Size} bytes, got {data.Length}.");
                return data;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                       && attempt < _opt.MaxRetriesPerChunk
                                       && !ct.IsCancellationRequested
                                       && (ex as HttpRequestException)?.StatusCode is not HttpStatusCode.Forbidden)
            {
                await Task.Delay(MegaApiClient.Backoff(attempt), ct);
            }
        }
    }
}
