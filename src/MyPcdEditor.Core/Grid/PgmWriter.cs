using System.Globalization;
using System.Text;

namespace MyPcdEditor.Core.Grid;

/// <summary>PGM 写出（P5 二进制），与 <see cref="PgmReader"/> 对称。</summary>
public static class PgmWriter
{
    /// <summary>写出单通道灰度图。</summary>
    /// <remarks>只支持 8 位（MaxValue 255）：样例与本工具的处理结果都是 8 位，16 位 PGM 不在范围内。</remarks>
    public static void Write(string path, PgmImage image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(image);

        if (image.MaxValue is <= 0 or > 255)
        {
            throw new PgmFormatException($"只支持 8 位 PGM 写出，当前 MaxValue={image.MaxValue}。");
        }

        int expected = image.Width * image.Height;
        if (image.Pixels.Length < expected)
        {
            throw new PgmFormatException($"像素数不足：{image.Pixels.Length} < {expected}。");
        }

        string header = string.Create(CultureInfo.InvariantCulture, $"P5\n{image.Width} {image.Height}\n{image.MaxValue}\n");
        byte[] head = Encoding.ASCII.GetBytes(header);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        stream.Write(head);
        stream.Write(image.Pixels, 0, expected);
    }
}
