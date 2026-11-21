using System.Globalization;
using System.Text;

namespace MyPcdEditor.Core.PointCloud;

/// <summary>
/// PCD 写出：只写「float32 xyz + binary」，即零拷贝快路径认的那种布局（<see cref="PcdHeader.IsRawXyzBinary"/>）。
/// </summary>
/// <remarks>
/// 不保留源文件的附加字段（intensity / rgb）：算子只处理几何，写回带未知语义的列会误导下游。
/// 需要保留原字段的场景走「导出到新目录、原文件不动」的路子。
/// </remarks>
public static class PcdWriter
{
    /// <summary>把点云写成 PCD（binary xyz）。</summary>
    public static void WriteBinaryXyz(string path, XyzCloud cloud)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(cloud);

        string header = string.Create(CultureInfo.InvariantCulture, $"""
            # .PCD v0.7 - Point Cloud Data file format
            VERSION 0.7
            FIELDS x y z
            SIZE 4 4 4
            TYPE F F F
            COUNT 1 1 1
            WIDTH {cloud.PointCount}
            HEIGHT 1
            VIEWPOINT 0 0 0 1 0 0 0
            POINTS {cloud.PointCount}
            DATA binary

            """).ReplaceLineEndings("\n");

        byte[] head = new UTF8Encoding(false).GetBytes(header);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        stream.Write(head);

        // 分片写：32 M 点一次拿 389 MB 的 span 没必要，也躲开单次 Write 的长度上限心理负担
        const int chunkBytes = 1 << 22;
        long offset = 0;
        long total = cloud.ByteLength;

        while (offset < total)
        {
            int length = (int)Math.Min(chunkBytes, total - offset);
            stream.Write(cloud.GetBytes(offset, length));
            offset += length;
        }
    }
}
