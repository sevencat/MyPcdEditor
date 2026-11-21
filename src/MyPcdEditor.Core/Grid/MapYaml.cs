using System.Globalization;

namespace MyPcdEditor.Core.Grid;

/// <summary>
/// ROS map_server 的 <c>map.yaml</c>。
/// </summary>
/// <remarks>
/// <para>
/// <c>image</c> 实测是绝对 Linux 路径（<c>/home/ysc/...</c>），直接用它必然找不到文件；
/// 因此这里只记录，实际加载按同目录同名 stem 匹配（docs/data-format.md §4）。
/// </para>
/// <para>不支持 yaw ≠ 0 的旋转地图（当前数据集 yaw 均为 0）。</para>
/// </remarks>
public sealed class MapYaml
{
    /// <summary>yaml 里写的图像路径（原样，不用于加载）。</summary>
    public string? Image { get; init; }

    /// <summary>每像素米数。</summary>
    public double Resolution { get; init; } = 0.05;

    /// <summary>地图左下角在世界系中的位置。</summary>
    public double OriginX { get; init; }

    public double OriginY { get; init; }

    /// <summary>原点朝向（当前只支持 0）。</summary>
    public double OriginYaw { get; init; }

    /// <summary>像素语义是否反转。</summary>
    public bool Negate { get; init; }

    public double OccupiedThresh { get; init; } = 0.65;

    public double FreeThresh { get; init; } = 0.196;

    public static MapYaml Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    public static MapYaml Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string? image = null;
        var resolution = 0.05;
        double originX = 0, originY = 0, yaw = 0;
        var negate = false;
        double occupied = 0.65, free = 0.196;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();

            switch (key)
            {
                case "image":
                    image = value;
                    break;

                case "resolution":
                    resolution = ParseDouble(value, key);
                    break;

                case "origin":
                    double[] parts = ParseArray(value, key);
                    if (parts.Length > 0)
                    {
                        originX = parts[0];
                    }

                    if (parts.Length > 1)
                    {
                        originY = parts[1];
                    }

                    if (parts.Length > 2)
                    {
                        yaw = parts[2];
                    }

                    break;

                case "negate":
                    negate = value is "1" or "true" or "True";
                    break;

                case "occupied_thresh":
                    occupied = ParseDouble(value, key);
                    break;

                case "free_thresh":
                    free = ParseDouble(value, key);
                    break;
            }
        }

        return new MapYaml
        {
            Image = image,
            Resolution = resolution,
            OriginX = originX,
            OriginY = originY,
            OriginYaw = yaw,
            Negate = negate,
            OccupiedThresh = occupied,
            FreeThresh = free,
        };
    }

    /// <summary>序列化成 map_server 认的 yaml 文本（<paramref name="imagePath"/> 原样写进 image 字段）。</summary>
    public string ToYaml(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var text = new System.Text.StringBuilder();
        text.Append("image: ").Append(imagePath).Append('\n');
        text.Append("resolution: ").Append(Resolution.ToString("G9", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("origin: [")
            .Append(OriginX.ToString("G9", CultureInfo.InvariantCulture)).Append(", ")
            .Append(OriginY.ToString("G9", CultureInfo.InvariantCulture)).Append(", ")
            .Append(OriginYaw.ToString("G9", CultureInfo.InvariantCulture)).Append("]\n");
        text.Append("negate: ").Append(Negate ? 1 : 0).Append('\n');
        text.Append("occupied_thresh: ").Append(OccupiedThresh.ToString("G9", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("free_thresh: ").Append(FreeThresh.ToString("G9", CultureInfo.InvariantCulture)).Append('\n');

        return text.ToString();
    }

    /// <summary>写出 yaml（与 <see cref="Load"/> 对称，供"导出优化结果"用）。</summary>
    public void Save(string path, string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllText(path, ToYaml(imagePath));
    }

    /// <summary>地图在世界系里的覆盖范围（左下 + 右上）。</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) WorldExtent(int width, int height)
        => (OriginX, OriginY, OriginX + (width * Resolution), OriginY + (height * Resolution));

    /// <summary>像素中心 → 世界坐标（行号向下增长，世界 y 向上）。</summary>
    public (double X, double Y) PixelCenterToWorld(int col, int row, int height)
        => (OriginX + ((col + 0.5) * Resolution),
            OriginY + ((height - row - 0.5) * Resolution));

    /// <summary>世界坐标 → 像素行列（世界 y 向上，行号向下）。</summary>
    public (int Col, int Row) WorldToPixel(double x, double y, int height)
        => ((int)Math.Floor((x - OriginX) / Resolution),
            (int)Math.Floor(height - ((y - OriginY) / Resolution)));

    private static double ParseDouble(string value, string key)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            ? result
            : throw new PgmFormatException($"map.yaml 的 {key} 不是数字：{value}");

    private static double[] ParseArray(string value, string key)
    {
        string body = value.Trim();
        if (body.StartsWith('['))
        {
            body = body[1..];
        }

        if (body.EndsWith(']'))
        {
            body = body[..^1];
        }

        string[] parts = body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            result[i] = ParseDouble(parts[i], key);
        }

        return result;
    }
}
