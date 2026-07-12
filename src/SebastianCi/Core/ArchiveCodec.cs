using System.IO.Compression;

namespace SebastianCi.Core;

/// <summary>
/// エージェントへ転送するアーカイブの gzip 圧縮・展開だけを担当する。
/// 大きなリポジトリの転送量を減らすために使う。
/// </summary>
public static class ArchiveCodec
{
    public static byte[] Compress(byte[] data)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Optimal))
        {
            gzip.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    public static async Task<byte[]> DecompressAsync(Stream compressed, CancellationToken cancellationToken = default)
    {
        using GZipStream gzip = new(compressed, CompressionMode.Decompress);
        using MemoryStream output = new();
        await gzip.CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }
}
