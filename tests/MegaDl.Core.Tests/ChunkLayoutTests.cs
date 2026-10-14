using MegaDl.Core.Transfers;

namespace MegaDl.Core.Tests;

public class ChunkLayoutTests
{
    /// <summary>Straight copy of ChunkWriterManager.calculateChunkOffset/Size from the Java code.</summary>
    private static (long Offset, long Size) Java(long id, int mult)
    {
        long[] offs = [0, 128, 384, 768, 1280, 1920, 2688];
        long offset = (id <= 7 ? offs[id - 1] : 3584 + (id - 8) * 1024 * mult) * 1024;
        long size = id is >= 1 and <= 7 ? id * 128 * 1024 : 1024L * 1024 * mult;
        return (offset, size);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public void Matches_the_java_formulas(int multiplier)
    {
        for (long id = 1; id <= 50; id++)
        {
            var (offset, size) = Java(id, multiplier);
            Assert.Equal(offset, ChunkLayout.OffsetOf(id, multiplier));
            Assert.Equal(size, ChunkLayout.NominalSizeOf(id, multiplier));
        }
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(131_072L)]
    [InlineData(3_670_016L)]     // exactly the 7-chunk ramp
    [InlineData(1_000_000_007L)]
    public void Chunks_tile_the_file_with_no_gaps_or_overlap(long fileSize)
    {
        long expectedOffset = 0;
        foreach (var chunk in ChunkLayout.For(fileSize, 20))
        {
            Assert.Equal(expectedOffset, chunk.Offset);
            Assert.True(chunk.Size > 0);
            expectedOffset = chunk.End;
        }
        Assert.Equal(fileSize, expectedOffset);
    }

    [Fact]
    public void Empty_file_has_a_single_empty_chunk() =>
        Assert.Equal(new[] { new Chunk(1, 0, 0) }, ChunkLayout.For(0).ToArray());

    [Fact]
    public void Big_download_chunks_line_up_with_mac_chunks()
    {
        // Every 20MB download chunk must start on a 1MB MAC-chunk boundary,
        // otherwise per-chunk MAC computation during download is impossible.
        var macStarts = ChunkLayout.For(500_000_000).Select(c => c.Offset).ToHashSet();
        Assert.All(ChunkLayout.For(500_000_000, 20), c => Assert.Contains(c.Offset, macStarts));
    }
}
