namespace MegaDl.Core.Crypto;

/// <summary>
/// The 32-byte key at the end of a MEGA file link, unpacked.
///
///   words: [k0 k1 k2 k3 | n0 n1 | m0 m1]
///   AES key   = [k0^n0, k1^n1, k2^m0, k3^m1]   (16 bytes)
///   Nonce     = [n0, n1]                       (first half of the CTR counter block)
///   MetaMac   = [m0, m1]                       (expected integrity checksum)
///
/// Java port note: CryptTools.initMEGALinkKey / initMEGALinkKeyIV and the first
/// lines of Download.verifyFileCBCMAC.
/// </summary>
public sealed class FileKey
{
    public byte[] AesKey { get; }
    public byte[] Nonce { get; }     // 8 bytes
    public byte[] MetaMac { get; }   // 8 bytes
    public byte[] Raw { get; }       // the original 32 bytes

    private FileKey(byte[] raw)
    {
        if (raw.Length != 32)
            throw new ArgumentException($"A MEGA file key must be 32 bytes, got {raw.Length}.", nameof(raw));

        Raw = raw;
        uint[] w = A32.FromBytes(raw);
        AesKey = A32.ToBytes([w[0] ^ w[4], w[1] ^ w[5], w[2] ^ w[6], w[3] ^ w[7]]);
        Nonce = A32.ToBytes([w[4], w[5]]);
        MetaMac = A32.ToBytes([w[6], w[7]]);
    }

    public static FileKey FromBytes(byte[] raw) => new(raw);

    public static FileKey FromBase64Url(string key) => new(Base64Url.Decode(key));
}
