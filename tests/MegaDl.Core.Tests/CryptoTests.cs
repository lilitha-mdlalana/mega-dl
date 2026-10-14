using System.Security.Cryptography;
using MegaDl.Core.Crypto;
using MegaDl.Core.Transfers;

namespace MegaDl.Core.Tests;

/// <summary>
/// Expected values were produced by an independent reference (Python + pycryptodome,
/// with the MAC ported block-by-block from MegaBasterd's Download.verifyFileCBCMAC),
/// not by this C# code. If these break, the C# is wrong — not the vectors.
///
/// Vector: plaintext[i] = (i * 31) % 251, length 1,234,567
///         AES key = 00 01 .. 0f, nonce = a0 a1 .. a7
/// </summary>
public class CryptoTests
{
    private const int Size = 1_234_567;
    private const string LinkKey = "oKCgoKCgoKAYgxK_Bhx6qKChoqOkpaanEIoYtAoRdKc";
    private const string MetaMacHex = "108a18b40a1174a7";
    private const string CiphertextSha256 = "3a853eaad42c25dd92fef9919559cde15bd887ef32b10b315df3c7a90a2f99df";

    private static byte[] Plaintext() => Enumerable.Range(0, Size).Select(i => (byte)(i * 31 % 251)).ToArray();

    [Fact]
    public void FileKey_unpacks_aes_key_nonce_and_meta_mac()
    {
        var key = FileKey.FromBase64Url(LinkKey);
        Assert.Equal("000102030405060708090a0b0c0d0e0f", Convert.ToHexStringLower(key.AesKey));
        Assert.Equal("a0a1a2a3a4a5a6a7", Convert.ToHexStringLower(key.Nonce));
        Assert.Equal(MetaMacHex, Convert.ToHexStringLower(key.MetaMac));
    }

    [Fact]
    public void AesCtr_matches_reference_ciphertext()
    {
        using var ctr = new AesCtr(FileKey.FromBase64Url(LinkKey));
        byte[] cipher = ctr.Transform(Plaintext(), fileOffset: 0);
        Assert.Equal(CiphertextSha256, Convert.ToHexStringLower(SHA256.HashData(cipher)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public void AesCtr_chunks_decrypt_independently_in_any_order(int multiplier)
    {
        byte[] plain = Plaintext();
        using var ctr = new AesCtr(FileKey.FromBase64Url(LinkKey));
        byte[] cipher = ctr.Transform(plain, 0);

        byte[] assembled = new byte[Size];
        foreach (Chunk c in ChunkLayout.For(Size, multiplier).Reverse()) // deliberately out of order
            ctr.Transform(cipher.AsSpan((int)c.Offset, (int)c.Size), assembled.AsSpan((int)c.Offset), c.Offset);

        Assert.Equal(plain, assembled);
    }

    [Theory]
    [InlineData(7, 13)]
    [InlineData(131_071, 40)]   // crosses the first chunk boundary
    public void AesCtr_works_at_unaligned_offsets(int offset, int length)
    {
        byte[] plain = Plaintext();
        using var ctr = new AesCtr(FileKey.FromBase64Url(LinkKey));
        byte[] cipher = ctr.Transform(plain, 0);

        byte[] slice = ctr.Transform(cipher.AsSpan(offset, length), offset);
        Assert.Equal(plain.AsSpan(offset, length).ToArray(), slice);
    }

    [Fact]
    public async Task MegaMac_matches_reference()
    {
        var key = FileKey.FromBase64Url(LinkKey);
        using var stream = new MemoryStream(Plaintext());
        byte[] mac = await MegaMac.ComputeAsync(stream, key, Size);
        Assert.Equal(MetaMacHex, Convert.ToHexStringLower(mac));
    }

    [Fact]
    public async Task MegaMac_detects_a_single_flipped_bit()
    {
        var key = FileKey.FromBase64Url(LinkKey);
        byte[] plain = Plaintext();
        plain[Size / 2] ^= 0x01;
        using var stream = new MemoryStream(plain);
        Assert.NotEqual(key.MetaMac, await MegaMac.ComputeAsync(stream, key, Size));
    }

    [Fact]
    public void MegaMac_rejects_out_of_order_chunks()
    {
        using var mac = new MegaMac(FileKey.FromBase64Url(LinkKey));
        Assert.Throws<InvalidOperationException>(() => mac.AppendChunkMac(2, new byte[16]));
    }

    [Fact]
    public void NodeAttributes_decrypts_reference_attribute()
    {
        byte[] key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        Assert.Equal("holiday video.mp4", NodeAttributes.TryDecryptName("J9-kCH9TvXmwnM-fv14qi8cP98FdkYIYgHIChZ9OEbM", key));
    }

    [Fact]
    public void NodeAttributes_returns_null_for_wrong_key()
    {
        Assert.Null(NodeAttributes.TryDecryptName("J9-kCH9TvXmwnM-fv14qi8cP98FdkYIYgHIChZ9OEbM", new byte[16]));
    }

    [Fact]
    public void NodeAttributes_roundtrip_with_unicode()
    {
        byte[] key = RandomNumberGenerator.GetBytes(16);
        string at = NodeAttributes.Encrypt(new { n = "héllo wörld 🎬.mkv" }, key);
        Assert.Equal("héllo wörld 🎬.mkv", NodeAttributes.TryDecryptName(at, key));
    }
}
