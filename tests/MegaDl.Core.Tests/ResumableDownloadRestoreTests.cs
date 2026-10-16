using System.Net;
using System.Security.Cryptography;
using MegaDl.Core.Api;
using MegaDl.Core.Crypto;
using MegaDl.Core.Transfers;
using Xunit;

namespace MegaDl.Core.Tests;

/// <summary>
/// Phase 5's queue persistence claims a restarted download picks up exactly where it left
/// off, integrity check included. This proves it end to end: start a download, "crash" it
/// partway through (cancel), then — in a brand new ResumableDownload instance, as a restart
/// would create — seed it from the first instance's DoneChunkIds and finish. If the MAC
/// replay in SeedCompletedChunksAsync were wrong, this would fail with a MAC mismatch
/// (returns false) rather than a clean exception, so it's worth a real assertion on content.
/// </summary>
public class ResumableDownloadRestoreTests
{
    [Fact]
    public async Task Seeding_from_a_prior_sessions_progress_still_passes_the_integrity_check()
    {
        byte[] aesKey = RandomNumberGenerator.GetBytes(16);
        byte[] nonce = RandomNumberGenerator.GetBytes(8);
        byte[] plaintext = RandomNumberGenerator.GetBytes(2_000_000); // spans several of MEGA's ramp chunks

        byte[] ciphertext = (byte[])plaintext.Clone();
        using (var ctr = new AesCtr(aesKey, nonce)) ctr.Transform(ciphertext, ciphertext, 0);

        byte[] metaMac;
        using (var mac = new MegaMac(aesKey, nonce))
        {
            foreach (Chunk c in ChunkLayout.For(plaintext.Length))
                mac.AppendChunkMac(c.Id, mac.ComputeChunkMac(plaintext.AsSpan((int)c.Offset, (int)c.Size)));
            metaMac = mac.Finish();
        }

        FileKey fileKey = FileKey.FromBytes(PackRawKey(aesKey, nonce, metaMac));
        var info = new RemoteFileInfo("file.bin", plaintext.Length, "http://fake/file", fileKey);

        string tempDir = Path.Combine(Path.GetTempPath(), "megadl-resume-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        string destPath = Path.Combine(tempDir, "file.bin");
        try
        {
            using var cts = new CancellationTokenSource();
            var handler = new SlicingHandler(ciphertext, onSecondRequest: () => cts.Cancel());
            using var http = new HttpClient(handler);

            // Session 1: downloads one chunk, then "crashes" (cancelled) partway through the next.
            var download1 = new ResumableDownload(http, info, destPath, new DownloadOptions(Workers: 1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download1.RunAsync(cts.Token));

            List<long> doneChunkIds = [.. download1.DoneChunkIds];
            Assert.Single(doneChunkIds); // exactly chunk 1 finished before the cancel fired
            download1.Dispose();

            // Session 2: a fresh instance — as a real restart would create — seeded from session 1.
            var download2 = new ResumableDownload(http, info, destPath, new DownloadOptions(Workers: 1));
            await download2.SeedCompletedChunksAsync(doneChunkIds);
            bool ok = await download2.RunAsync(CancellationToken.None);
            download2.Dispose();

            Assert.True(ok, "Integrity check failed — the MAC replay in SeedCompletedChunksAsync produced a wrong meta-MAC.");
            Assert.Equal(plaintext, await File.ReadAllBytesAsync(destPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Inverts FileKey's unpacking (see FileKey.cs): given the real AES key/nonce/metaMac,
    /// produces the 32-byte raw key that FileKey.FromBytes would decode back to them.
    /// </summary>
    private static byte[] PackRawKey(byte[] aesKey, byte[] nonce, byte[] metaMac)
    {
        uint[] k = A32.FromBytes(aesKey);
        uint[] n = A32.FromBytes(nonce);
        uint[] m = A32.FromBytes(metaMac);
        return A32.ToBytes([k[0] ^ n[0], k[1] ^ n[1], k[2] ^ m[0], k[3] ^ m[1], n[0], n[1], m[0], m[1]]);
    }

    private sealed class SlicingHandler(byte[] ciphertext, Action onSecondRequest) : HttpMessageHandler
    {
        private int _count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _count) == 2) onSecondRequest();
            ct.ThrowIfCancellationRequested();

            string tail = request.RequestUri!.Segments[^1];
            string[] parts = tail.Split('-');
            long start = long.Parse(parts[0]);
            long endInclusive = parts.Length > 1 ? long.Parse(parts[1]) : ciphertext.Length - 1;
            byte[] slice = ciphertext.AsSpan((int)start, (int)(endInclusive - start + 1)).ToArray();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(slice) });
        }
    }
}
