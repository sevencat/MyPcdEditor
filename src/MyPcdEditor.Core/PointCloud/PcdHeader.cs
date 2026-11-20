using System.Globalization;

namespace MyPcdEditor.Core.PointCloud;

/// <summary>PCD 文件格式错误。</summary>
public sealed class PcdFormatException(string message) : Exception(message);

/// <summary>数据段的编码方式。</summary>
public enum PcdDataType
{
    Ascii,
    Binary,
    BinaryCompressed,
}

/// <summary>
/// PCD 头部。字段表按 <c>FIELDS/SIZE/TYPE/COUNT</c> 通用展开，不硬编码 xyzrgb。
/// </summary>
/// <remarks>对应 docs/data-format.md §2.1。</remarks>
public sealed class PcdHeader
{
    /// <summary>防御性上限：头部不可能这么大，超了说明文件不是 PCD。</summary>
    private const int MaxHeaderBytes = 1 << 20;

    public string Version { get; init; } = "0.7";

    /// <summary>字段名（`COUNT` 未展开，与 `Sizes`/`Types`/`Counts` 一一对应）。</summary>
    public required string[] Fields { get; init; }

    /// <summary>每个字段单个元素的字节数。</summary>
    public required int[] Sizes { get; init; }

    /// <summary>每个字段的元素类型：<c>F</c> / <c>I</c> / <c>U</c>。</summary>
    public required char[] Types { get; init; }

    /// <summary>每个字段的元素个数。</summary>
    public required int[] Counts { get; init; }

    public int Width { get; init; }

    public int Height { get; init; } = 1;

    public int PointCount { get; init; }

    public double[] Viewpoint { get; init; } = [0, 0, 0, 1, 0, 0, 0];

    public PcdDataType Data { get; init; }

    /// <summary>头部字节数（含 <c>DATA</c> 行换行符），即数据段起始偏移。</summary>
    public int HeaderLength { get; init; }

    /// <summary>AOS 布局下单个点的字节数。</summary>
    public int PointStep
    {
        get
        {
            int step = 0;
            for (var i = 0; i < Fields.Length; i++)
            {
                step += Sizes[i] * Counts[i];
            }

            return step;
        }
    }

    /// <summary>是否是「纯 float32 xyz、binary、无附加字段」——零拷贝快路径的判定条件。</summary>
    public bool IsRawXyzBinary =>
        Data == PcdDataType.Binary
        && Fields.Length == 3
        && Fields[0] == "x" && Fields[1] == "y" && Fields[2] == "z"
        && Sizes[0] == 4 && Sizes[1] == 4 && Sizes[2] == 4
        && Types[0] == 'F' && Types[1] == 'F' && Types[2] == 'F'
        && Counts[0] == 1 && Counts[1] == 1 && Counts[2] == 1;

    /// <summary>从流的当前位置解析头部，流位置停在数据段起点。</summary>
    /// <exception cref="PcdFormatException">头部缺失或字段数不匹配。</exception>
    public static PcdHeader Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var lines = new List<string>();
        var line = new System.Text.StringBuilder();
        var consumed = 0;
        var reachedData = false;

        while (consumed < MaxHeaderBytes)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                break;
            }

            consumed++;
            if (b != '\n')
            {
                line.Append((char)b);
                continue;
            }

            string text = line.ToString().Trim('\r', ' ', '\t');
            line.Clear();

            if (text.Length == 0 || text[0] == '#')
            {
                continue;
            }

            lines.Add(text);

            // DATA 行之后立刻是二进制数据，不能继续按文本读
            if (text.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
            {
                reachedData = true;
                break;
            }
        }

        if (!reachedData)
        {
            throw new PcdFormatException("未找到 DATA 行，文件可能不是 PCD 或头部损坏。");
        }

        string[] fields = [];
        int[] sizes = [];
        char[] types = [];
        int[] counts = [];
        var width = 0;
        var height = 1;
        var points = -1;
        double[] viewpoint = [0, 0, 0, 1, 0, 0, 0];
        var dataType = PcdDataType.Ascii;
        var version = "0.7";

        foreach (string raw in lines)
        {
            int space = raw.IndexOf(' ');
            string key = space < 0 ? raw : raw[..space];
            string rest = space < 0 ? string.Empty : raw[(space + 1)..].Trim();
            string[] tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            switch (key.ToUpperInvariant())
            {
                case "VERSION":
                    version = rest;
                    break;

                case "FIELDS":
                    fields = tokens;
                    break;

                case "SIZE":
                    sizes = ParseInts(tokens, "SIZE");
                    break;

                case "TYPE":
                    types = tokens.Select(static t => t.Length == 1 ? t[0] : throw new PcdFormatException($"非法的 TYPE 项：{t}"))
                                 .ToArray();
                    break;

                case "COUNT":
                    counts = ParseInts(tokens, "COUNT");
                    break;

                case "WIDTH":
                    width = ParseInt(tokens, "WIDTH");
                    break;

                case "HEIGHT":
                    height = ParseInt(tokens, "HEIGHT");
                    break;

                case "POINTS":
                    points = ParseInt(tokens, "POINTS");
                    break;

                case "VIEWPOINT":
                    viewpoint = tokens.Select(static t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray();
                    break;

                case "DATA":
                    dataType = rest.ToLowerInvariant() switch
                    {
                        "ascii" => PcdDataType.Ascii,
                        "binary" => PcdDataType.Binary,
                        "binary_compressed" => PcdDataType.BinaryCompressed,
                        _ => throw new PcdFormatException($"不支持的 DATA 类型：{rest}"),
                    };
                    break;
            }
        }

        if (fields.Length == 0 || sizes.Length == 0 || types.Length == 0)
        {
            throw new PcdFormatException("头部缺少 FIELDS/SIZE/TYPE。");
        }

        if (counts.Length == 0)
        {
            // COUNT 是可选项，缺省为 1
            counts = Enumerable.Repeat(1, fields.Length).ToArray();
        }

        if (sizes.Length != fields.Length || types.Length != fields.Length || counts.Length != fields.Length)
        {
            throw new PcdFormatException(
                $"字段表长度不一致：FIELDS={fields.Length} SIZE={sizes.Length} TYPE={types.Length} COUNT={counts.Length}。");
        }

        if (points < 0)
        {
            points = width * height;
        }

        return new PcdHeader
        {
            Version = version,
            Fields = fields,
            Sizes = sizes,
            Types = types,
            Counts = counts,
            Width = width,
            Height = height,
            PointCount = points,
            Viewpoint = viewpoint,
            Data = dataType,
            HeaderLength = consumed,
        };
    }

    private static int[] ParseInts(string[] tokens, string key)
        => tokens.Select(t => ParseInt(t, key)).ToArray();

    private static int ParseInt(string[] tokens, string key)
    {
        if (tokens.Length == 0)
        {
            throw new PcdFormatException($"头部 {key} 字段为空。");
        }

        return ParseInt(tokens[0], key);
    }

    private static int ParseInt(string token, string key)
        => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new PcdFormatException($"头部 {key} 字段不是整数：{token}");
}
