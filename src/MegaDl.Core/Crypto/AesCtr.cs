using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MegaDl.Core.Crypto;

/// <summary>
/// AES-128-CTR, which .NET does not ship as a mode. CTR is just
/// "encrypt a counter with AES-ECB, XOR the result with the data", so we build it.
///
/// Counter block layout used by MEGA (16 bytes):
///   [ nonce (8 bytes) | big-endian block index (8 bytes) ]
/// where block index = byte offset / 16.
///
/// Because the counter only depends on the byte offset, any chunk of the file can be
/// decrypted independently — which is what makes parallel chunk downloads possible.
/// Java port note: CryptTools.forwardMEGALinkKeyIV + Cipher "AES/CTR/NoPadding".
/// </summary>
public sealed class AesCtr : IDisposable
{
    private const int BlockSize = 16;
    private readonly Aes _aes;
    private readonly byte[] _nonce;

    public AesCtr(byte[] key, byte[] nonce)
    {
        if (nonce.Length != 8) throw new ArgumentException("Nonce must be 8 bytes.", nameof(nonce));
        _aes = Aes.Create();
        _aes.Key = key;
        _nonce = (byte[])nonce.Clone();
    }

    public AesCtr(FileKey key) : this(key.AesKey, key.Nonce) { }

    /// <summary>
    /// Encrypts or decrypts (same operation in CTR) <paramref name="input"/> that sits at
    /// <paramref name="fileOffset"/> in the file. Offset does not need to be 16-aligned.
    /// </summary>
    public void Transform(ReadOnlySpan<byte> input, Span<byte> output, long fileOffset)
    {
        if (output.Length < input.Length) throw new ArgumentException("Output too small.", nameof(output));
        if (input.IsEmpty) return;

        long firstBlock = fileOffset / BlockSize;
        int skip = (int)(fileOffset % BlockSize);       // bytes to skip inside the first block
        int blockCount = (skip + input.Length + BlockSize - 1) / BlockSize;

        // Build all counter blocks, then AES-ECB them in one call (fast, uses AES-NI).
        byte[] counters = new byte[blockCount * BlockSize];
        for (int i = 0; i < blockCount; i++)
        {
            Span<byte> block = counters.AsSpan(i * BlockSize, BlockSize);
            _nonce.CopyTo(block);
            BinaryPrimitives.WriteUInt64BigEndian(block[8..], (ulong)(firstBlock + i));
        }

        byte[] keystream = new byte[counters.Length];
        _aes.EncryptEcb(counters, keystream, PaddingMode.None);

        for (int i = 0; i < input.Length; i++)
            output[i] = (byte)(input[i] ^ keystream[skip + i]);
    }

    public byte[] Transform(ReadOnlySpan<byte> input, long fileOffset)
    {
        var output = new byte[input.Length];
        Transform(input, output, fileOffset);
        return output;
    }

    public void Dispose() => _aes.Dispose();
}
