using SebastianCi.Core;

namespace SebastianCi.Tests;

public class ArchiveCodecTests
{
    [Fact]
    public async Task Compress_ThenDecompress_RoundTripsData()
    {
        byte[] original = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("繰り返し ", 500)));

        byte[] compressed = ArchiveCodec.Compress(original);
        using MemoryStream stream = new(compressed);
        byte[] restored = await ArchiveCodec.DecompressAsync(stream);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void Compress_ReducesSizeForRepetitiveData()
    {
        byte[] repetitive = System.Text.Encoding.UTF8.GetBytes(new string('A', 10000));

        byte[] compressed = ArchiveCodec.Compress(repetitive);

        Assert.True(compressed.Length < repetitive.Length / 10);
    }
}
