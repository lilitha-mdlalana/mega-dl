using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MegaDl.Core.Crypto;

/// <summary>
/// A node's metadata (file name etc.) is stored encrypted as
///   AES-CBC(key, IV = 0, "MEGA" + json, zero-padded to 16 bytes)
/// and sent base64url-encoded in the "at" field of API responses.
/// Java port note: MegaAPI._decAttr / _encAttr.
/// </summary>
public static class NodeAttributes
{
    private static readonly byte[] ZeroIv = new byte[16];

    public static string? TryDecryptName(string encryptedAttr, byte[] aesKey) =>
        TryDecrypt(encryptedAttr, aesKey) is { } doc && doc.RootElement.TryGetProperty("n", out var n)
            ? n.GetString()
            : null;

    public static JsonDocument? TryDecrypt(string encryptedAttr, byte[] aesKey)
    {
        try
        {
            using var aes = Aes.Create();
            aes.Key = aesKey;
            byte[] plain = aes.DecryptCbc(Base64Url.Decode(encryptedAttr), ZeroIv, PaddingMode.None);

            string text = Encoding.UTF8.GetString(plain).TrimEnd('\0');
            if (!text.StartsWith("MEGA", StringComparison.Ordinal)) return null; // wrong key
            return JsonDocument.Parse(text[4..]);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    public static string Encrypt(object attributes, byte[] aesKey)
    {
        byte[] json = Encoding.UTF8.GetBytes("MEGA" + JsonSerializer.Serialize(attributes));
        byte[] padded = new byte[(json.Length + 15) / 16 * 16];
        json.CopyTo(padded, 0);

        using var aes = Aes.Create();
        aes.Key = aesKey;
        return Base64Url.Encode(aes.EncryptCbc(padded, ZeroIv, PaddingMode.None));
    }
}
