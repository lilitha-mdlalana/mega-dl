using System.Security.Cryptography;
using MegaDl.Core.Transfers;

namespace MegaDl.Core.Crypto;

/// <summary>
/// MEGA's file integrity check ("meta MAC"), a two-level CBC-MAC:
///
///   1. For each MEGA chunk (see <see cref="ChunkLayout"/> with multiplier 1):
///        chunkMac = last block of AES-CBC(plaintext chunk, IV = nonce||nonce), zero-padded
///   2. fileMac = 0;  for each chunkMac in order: fileMac = AES-ECB(fileMac XOR chunkMac)
///   3. metaMac = [f0^f1, f2^f3]  (8 bytes) — must equal the last 8 bytes of the link key.
///
/// Trick vs. the Java version: MegaBasterd XORs and encrypts one 16-byte block at a
/// time. A CBC-MAC *is* the last block of a CBC encryption, so we hand the whole
/// chunk to .NET's hardware-accelerated EncryptCbc and keep the tail. Same answer,
/// much faster.
///
/// Step 1 is independent per chunk (parallelizable). Step 2 must run in chunk order.
/// Java port note: Download.verifyFileCBCMAC, UploadMACGenerator.
/// </summary>
public sealed class MegaMac : IDisposable
{
    private const int BlockSize = 16;
    private readonly Aes _aes;
    private readonly byte[] _chunkIv;          // nonce || nonce
    private readonly byte[] _fileMac = new byte[BlockSize];
    private long _nextChunkId = 1;

    public MegaMac(byte[] aesKey, byte[] nonce)
    {
        _aes = Aes.Create();
        _aes.Key = aesKey;
        _chunkIv = [.. nonce, .. nonce];
    }

    public MegaMac(FileKey key) : this(key.AesKey, key.Nonce) { }

    /// <summary>CBC-MAC of one plaintext MEGA chunk. Thread-safe; call in parallel if you like.</summary>
    public byte[] ComputeChunkMac(ReadOnlySpan<byte> plaintextChunk)
    {
        int padded = Math.Max(BlockSize, (plaintextChunk.Length + BlockSize - 1) / BlockSize * BlockSize);
        byte[] buffer = new byte[padded];                // zero padding at the tail
        plaintextChunk.CopyTo(buffer);

        using var aes = Aes.Create();                    // own instance → thread-safe
        aes.Key = _aes.Key;
        byte[] cipher = aes.EncryptCbc(buffer, _chunkIv, PaddingMode.None);
        return cipher[^BlockSize..];
    }

    /// <summary>Folds the next chunk's MAC into the file MAC. Must be called in chunk order.</summary>
    public void AppendChunkMac(long chunkId, ReadOnlySpan<byte> chunkMac)
    {
        if (chunkId != _nextChunkId)
            throw new InvalidOperationException($"Chunk MACs must be appended in order: expected {_nextChunkId}, got {chunkId}.");

        Span<byte> x = stackalloc byte[BlockSize];
        for (int i = 0; i < BlockSize; i++) x[i] = (byte)(_fileMac[i] ^ chunkMac[i]);
        _aes.EncryptEcb(x, _fileMac, PaddingMode.None);
        _nextChunkId++;
    }

    /// <summary>The 8-byte meta MAC, to compare with <see cref="FileKey.MetaMac"/>.</summary>
    public byte[] Finish()
    {
        uint[] f = A32.FromBytes(_fileMac);
        return A32.ToBytes([f[0] ^ f[1], f[2] ^ f[3]]);
    }

    /// <summary>Convenience: computes the meta MAC of a whole plaintext stream.</summary>
    public static async Task<byte[]> ComputeAsync(Stream plaintext, FileKey key, long length, CancellationToken ct = default)
    {
        using var mac = new MegaMac(key);
        foreach (Chunk chunk in ChunkLayout.For(length))
        {
            byte[] buffer = new byte[chunk.Size];
            await plaintext.ReadExactlyAsync(buffer, ct);
            mac.AppendChunkMac(chunk.Id, mac.ComputeChunkMac(buffer));
        }
        return mac.Finish();
    }

    public void Dispose() => _aes.Dispose();
}
