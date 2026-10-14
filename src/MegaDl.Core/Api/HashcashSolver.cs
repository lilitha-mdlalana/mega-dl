using System.Security.Cryptography;
using MegaDl.Core.Crypto;

namespace MegaDl.Core.Api;

/// <summary>
/// Solver for MEGA's X-Hashcash anti-bot proof-of-work challenge.
///
/// Since 2024 MEGA's /cs endpoint may respond to a request with HTTP 402 "Payment
/// Required" plus an X-Hashcash header like:
///
///   1:192:1778778076:VEZuswYJ22eveR5NVuFepExSEAuQCxQTnlvRpQkW9IR0D03EGZD3DvD3tfOAVArD
///   ^v ^easiness ^ts ^token (64 b64url chars = 48 bytes)
///
/// The client finds a 32-bit nonce whose SHA-256 satisfies a threshold, then re-sends
/// the same request with header "1:&lt;token&gt;:&lt;base64url nonce bytes, big-endian&gt;".
///
/// Algorithm taken verbatim from the official MEGA SDK (meganz/sdk, src/hashcash.cpp)
/// via MegaBasterd's HashcashSolver.java. Message fed to SHA-256 (12,582,916 bytes):
/// [4 bytes nonce big-endian][token (48 bytes)] repeated 262,144 times.
///
/// Threshold = (((easiness &amp; 63) &lt;&lt; 1) + 1) &lt;&lt; (((easiness &gt;&gt; 6) &amp; 3) * 7 + 3).
/// Condition: first 4 bytes of SHA-256(message), read as big-endian uint32, &lt;= threshold.
/// </summary>
public static class HashcashSolver
{
    private const int TokenBytes = 48;
    private const int PrefixBytes = 4;
    private const int Repeat = 262_144;
    private const int BufSize = PrefixBytes + Repeat * TokenBytes; // 12,582,916 bytes
    private const int MaxWorkers = 8;

    public static readonly TimeSpan DefaultSolveTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Parses the full challenge header value, solves the proof-of-work, and returns
    /// the value to send back in the retried request's X-Hashcash header.
    /// </summary>
    public static string BuildSolutionHeader(string challenge, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        string[] parts = challenge.Split(':');
        if (parts.Length != 4)
            throw new FormatException($"hashcash header must have 4 fields, got {parts.Length}: {challenge}");
        if (parts[0] != "1")
            throw new FormatException($"hashcash version not supported: {parts[0]}");
        if (!int.TryParse(parts[1], out int easiness) || easiness is < 0 or > 255)
            throw new FormatException($"hashcash easiness out of [0,255]: {parts[1]}");

        string token = parts[3];
        if (token.Length != 64)
            throw new FormatException($"hashcash token must be 64 b64url chars, got {token.Length}");

        byte[] tokenBin = Base64Url.Decode(token);
        if (tokenBin.Length != TokenBytes)
            throw new FormatException($"hashcash token must decode to 48 bytes, got {tokenBin.Length}");

        string nonceB64 = Solve(tokenBin, easiness, timeout ?? DefaultSolveTimeout);
        return $"1:{token}:{nonceB64}";
    }

    /// <summary>Returns the base64url 4-byte big-endian nonce that satisfies the threshold.</summary>
    private static string Solve(byte[] tokenBin, int easiness, TimeSpan timeout)
    {
        // For easiness=192 this is 0x01000000 (~256 hashes expected per worker stride).
        uint threshold = (uint)((((easiness & 63) << 1) + 1) << (((easiness >> 6) & 3) * 7 + 3));

        byte[] buf = new byte[BufSize];
        Buffer.BlockCopy(tokenBin, 0, buf, PrefixBytes, TokenBytes);
        int filled = TokenBytes;
        int totalTokenArea = Repeat * TokenBytes;
        while (filled < totalTokenArea)
        {
            int copy = Math.Min(filled, totalTokenArea - filled);
            Buffer.BlockCopy(buf, PrefixBytes, buf, PrefixBytes + filled, copy);
            filled += copy;
        }

        int workers = Math.Min(MaxWorkers, Math.Max(1, Environment.ProcessorCount));
        using var stopSource = new CancellationTokenSource(timeout);
        var stopToken = stopSource.Token;
        string? winner = null;
        var winnerLock = new object();

        var tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            int start = w;
            int stride = workers;
            tasks[w] = Task.Run(() =>
            {
                string? sol = Worker(buf, threshold, start, stride, stopToken);
                if (sol is null) return;
                lock (winnerLock)
                {
                    winner ??= sol;
                }
                stopSource.Cancel();
            });
        }

        try { Task.WaitAll(tasks); }
        catch (AggregateException) { /* cancellation from a winning worker is expected */ }

        return winner ?? throw new TimeoutException($"Hashcash solve timed out after {timeout}.");
    }

    private static string? Worker(byte[] tokenArea, uint threshold, int start, int stride, CancellationToken stopToken)
    {
        byte[] block = new byte[64];
        // First 64-byte SHA block is [nonce(4) | tokenArea[4..63]]; the tail is constant.
        Buffer.BlockCopy(tokenArea, PrefixBytes, block, PrefixBytes, 64 - PrefixBytes);
        ReadOnlySpan<byte> rest = tokenArea.AsSpan(64, BufSize - 64);
        Span<byte> hash = stackalloc byte[32];

        const long maxNonce = 1L << 32;
        for (long n = start; n < maxNonce; n += stride)
        {
            if (stopToken.IsCancellationRequested) return null;

            block[0] = (byte)((n >> 24) & 0xff);
            block[1] = (byte)((n >> 16) & 0xff);
            block[2] = (byte)((n >> 8) & 0xff);
            block[3] = (byte)(n & 0xff);

            using (var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                incremental.AppendData(block);
                incremental.AppendData(rest);
                incremental.GetHashAndReset(hash);
            }

            uint firstWord = (uint)((hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3]);
            if (firstWord <= threshold)
                return Base64Url.Encode(block.AsSpan(0, 4));
        }
        return null;
    }
}
