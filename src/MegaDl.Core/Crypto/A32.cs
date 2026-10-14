using System.Buffers.Binary;

namespace MegaDl.Core.Crypto;

/// <summary>
/// MEGA thinks in "a32" arrays: byte buffers viewed as big-endian 32-bit words.
/// Java port note: MegaBasterd's MiscTools.bin2i32a / i32a2bin. Java uses signed
/// int; we use uint so XOR and comparisons never trip over sign bits.
/// </summary>
public static class A32
{
    public static uint[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        // Pad to a multiple of 4 like the Java version does.
        int words = (bytes.Length + 3) / 4;
        if (bytes.Length % 4 != 0)
        {
            byte[] padded = new byte[words * 4];
            bytes.CopyTo(padded);
            bytes = padded;
        }

        var result = new uint[words];
        for (int i = 0; i < words; i++)
            result[i] = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i * 4, 4));
        return result;
    }

    public static byte[] ToBytes(ReadOnlySpan<uint> words)
    {
        var result = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(i * 4, 4), words[i]);
        return result;
    }
}
