using System.Globalization;
using System.Text;

namespace MyPcdEditor.Core.Grid;

/// <summary>PGM 读取（P2 ASCII / P5 二进制）。</summary>
/// <remarks>头部 token 分隔符可能是任意空白，且允许 <c>#</c> 注释行（docs/data-format.md §3）。</remarks>
public static class PgmReader
{
    public static PgmImage Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Read(stream);
    }

    public static PgmImage Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        string magic = NextToken(stream, out _);
        bool binary = magic switch
        {
            "P5" => true,
            "P2" => false,
            _ => throw new PgmFormatException($"不支持的 PGM 魔数：{magic}（只支持 P2/P5）。"),
        };

        int width = ParseInt(NextToken(stream, out _), "宽度");
        int height = ParseInt(NextToken(stream, out _), "高度");
        int maxValue = ParseInt(NextToken(stream, out char terminator), "maxval");

        if (width <= 0 || height <= 0)
        {
            throw new PgmFormatException($"非法的 PGM 尺寸：{width}x{height}。");
        }

        if (maxValue is <= 0 or > 65535)
        {
            throw new PgmFormatException($"非法的 PGM maxval：{maxValue}。");
        }

        var pixels = new byte[checked(width * height)];

        if (binary)
        {
            // maxval 后的单个空白已被 NextToken 吃掉；若是 CRLF，还得再吃一个 LF，否则它会变成第一个像素
            if (terminator == '\r' && stream.ReadByte() is not '\n')
            {
                throw new PgmFormatException("P5 头部 CR 后缺少 LF。");
            }

            if (maxValue < 256)
            {
                ReadExactly(stream, pixels);
            }
            else
            {
                // 16 位大端：只取高字节（查看用途足够，写回时保持原 maxval 语义）
                var buffer = new byte[checked(width * height * 2)];
                ReadExactly(stream, buffer);
                for (var i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = buffer[(i * 2) + 1];
                }
            }
        }
        else
        {
            for (var i = 0; i < pixels.Length; i++)
            {
                int value = ParseInt(NextToken(stream, out _), "像素");
                pixels[i] = value > 255 ? (byte)(value * 255 / maxValue) : (byte)Math.Clamp(value, 0, 255);
            }
        }

        return new PgmImage
        {
            Width = width,
            Height = height,
            MaxValue = maxValue,
            Pixels = pixels,
        };
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException ex)
        {
            throw new PgmFormatException($"像素数据不足：期望 {buffer.Length} 字节。{ex.Message}");
        }
    }

    /// <summary>读一个头部 token；<paramref name="terminator"/> 返回结束它的那个空白字符。</summary>
    private static string NextToken(Stream stream, out char terminator)
    {
        terminator = '\0';

        int b;
        while (true)
        {
            b = stream.ReadByte();
            if (b < 0)
            {
                throw new PgmFormatException("头部提前结束。");
            }

            if (b == '#')
            {
                // 注释到行尾
                while ((b = stream.ReadByte()) >= 0 && b != '\n')
                {
                }

                continue;
            }

            if (!IsWhite(b))
            {
                break;
            }
        }

        var sb = new StringBuilder();
        while (b >= 0 && !IsWhite(b))
        {
            sb.Append((char)b);
            b = stream.ReadByte();
        }

        if (b >= 0)
        {
            terminator = (char)b;
        }

        return sb.ToString();
    }

    private static bool IsWhite(int b) => b is ' ' or '\t' or '\r' or '\n' or '\f' or '\v';

    private static int ParseInt(string token, string what)
        => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new PgmFormatException($"PGM {what} 不是整数：{token}");
}
