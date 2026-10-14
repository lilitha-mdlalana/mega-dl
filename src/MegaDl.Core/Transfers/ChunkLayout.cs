namespace MegaDl.Core.Transfers;

/// <summary>A byte range of a file: [Offset, Offset + Size).</summary>
public readonly record struct Chunk(long Id, long Offset, long Size)
{
    public long End => Offset + Size; // exclusive
}

/// <summary>
/// MEGA's chunk boundaries. The first 7 chunks grow (128K, 256K, ... 896K = 3.5MB total),
/// then every chunk is 1MB. MEGA's integrity MAC is computed over exactly these
/// boundaries, so they're part of the protocol, not a tuning choice.
///
/// <paramref name="multiplier"/> lets downloads use bigger network chunks after the
/// first 3.5MB (MegaBasterd uses 20 → 20MB). Because 20MB is a whole number of 1MB
/// MAC chunks, the big chunks still line up with the MAC boundaries.
///
/// Java port note: ChunkWriterManager.calculateChunkOffset / calculateChunkSize.
/// Chunk ids are 1-based to match the Java code.
/// </summary>
public static class ChunkLayout
{
    private const long K = 1024;
    private const long RampBytes = 3584 * K; // sum of the first 7 chunks

    public static long OffsetOf(long chunkId, int multiplier = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkId, 1);
        if (chunkId <= 7) return 128 * K * (chunkId - 1) * chunkId / 2; // 128K * triangular number
        return RampBytes + (chunkId - 8) * K * K * multiplier;
    }

    public static long NominalSizeOf(long chunkId, int multiplier = 1) =>
        chunkId <= 7 ? chunkId * 128 * K : K * K * multiplier;

    /// <summary>All chunks covering a file of <paramref name="fileSize"/> bytes.</summary>
    public static IEnumerable<Chunk> For(long fileSize, int multiplier = 1)
    {
        if (fileSize <= 0)
        {
            yield return new Chunk(1, 0, 0);
            yield break;
        }

        for (long id = 1; ; id++)
        {
            long offset = OffsetOf(id, multiplier);
            if (offset >= fileSize) yield break;
            long size = Math.Min(NominalSizeOf(id, multiplier), fileSize - offset);
            yield return new Chunk(id, offset, size);
        }
    }
}
