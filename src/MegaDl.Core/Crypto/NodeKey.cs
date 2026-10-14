using System.Security.Cryptography;

namespace MegaDl.Core.Crypto;

/// <summary>
/// Node keys inside a MEGA folder listing ("f" command) arrive AES-ECB "wrapped" with
/// the folder's share key (the key half of a folder link) — one 16-byte block per AES
/// block, no padding. A folder node's own key is one block (used directly to decrypt
/// that folder's "at" attributes); a file node's key is two blocks, the same 32-byte
/// packing as <see cref="FileKey"/>.
///
/// Java port note: MegaAPI.decryptKey (aes_ecb_decrypt_nopadding).
/// </summary>
public static class NodeKey
{
    public static byte[] Unwrap(ReadOnlySpan<byte> wrapped, byte[] folderKey)
    {
        using var aes = Aes.Create();
        aes.Key = folderKey;
        return aes.DecryptEcb(wrapped, PaddingMode.None);
    }
}
