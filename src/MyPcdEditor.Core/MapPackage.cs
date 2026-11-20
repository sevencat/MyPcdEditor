using System.Globalization;
using System.Numerics;
using MyPcdEditor.Core.Grid;

namespace MyPcdEditor.Core;

/// <summary>地图包在 UTM 坐标系里的绝对锚点（<c>.projected_origin</c>）。</summary>
/// <remarks>本轮只读入不参与逻辑；后续多图合并用它对齐（docs/decisions.md ADR-008）。</remarks>
public sealed class ProjectedOrigin
{
    public required double X { get; init; }

    public required double Y { get; init; }

    public required double Z { get; init; }

    /// <summary>UTM 带号（样例为 21）。</summary>
    public int Zone { get; init; }

    public Vector3 Position => new((float)X, (float)Y, (float)Z);

    public static ProjectedOrigin ParseFile(string path)
    {
        double x = 0, y = 0, z = 0;
        var zone = 0;

        foreach (string raw in File.ReadAllLines(path))
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

            if (key == "origin")
            {
                string body = value.Trim('[', ']');
                string[] parts = body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length > 0)
                {
                    x = double.Parse(parts[0], CultureInfo.InvariantCulture);
                }

                if (parts.Length > 1)
                {
                    y = double.Parse(parts[1], CultureInfo.InvariantCulture);
                }

                if (parts.Length > 2)
                {
                    z = double.Parse(parts[2], CultureInfo.InvariantCulture);
                }
            }
            else if (key == "zone")
            {
                _ = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out zone);
            }
        }

        return new ProjectedOrigin { X = x, Y = y, Z = z, Zone = zone };
    }
}

/// <summary>地图包内的一个栅格图条目（<c>jueying_id_map.toml</c> 的 <c>[[grids]]</c>）。</summary>
public sealed record GridEntry(int Id, string Name);

/// <summary>栅格图索引（<c>jueying_id_map.toml</c>）。当前按单图处理，但保留多图结构。</summary>
public sealed class MapIndex
{
    public string? Path { get; init; }

    public required IReadOnlyList<GridEntry> Grids { get; init; }

    /// <summary>极简 TOML 解析：只认本项目用到的 <c>path</c> 与 <c>[[grids]]</c> 段。</summary>
    public static MapIndex ParseFile(string filePath)
    {
        string? directory = null;
        var grids = new List<GridEntry>();
        int? id = null;
        string? name = null;

        void Flush()
        {
            if (id is not null && name is not null)
            {
                grids.Add(new GridEntry(id.Value, name));
            }

            id = null;
            name = null;
        }

        foreach (string raw in File.ReadAllLines(filePath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("[[grids]]", StringComparison.Ordinal))
            {
                Flush();
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim().Trim('"');

            switch (key)
            {
                case "path":
                    directory = value;
                    break;
                case "id":
                    id = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
                    break;
                case "name":
                    name = value;
                    break;
            }
        }

        Flush();

        return new MapIndex { Path = directory, Grids = grids };
    }
}

/// <summary>
/// 一次巡检产出的「地图包」：一个目录里的 PCD + PGM + yaml + toml + projected_origin。
/// </summary>
public sealed class MapPackage
{
    private MapPackage(string directory)
    {
        Directory = directory;
        DisplayName = System.IO.Path.GetFileName(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
    }

    public string Directory { get; }

    public string DisplayName { get; }

    public string? PcdPath { get; private init; }

    public string? PgmPath { get; private init; }

    public string? YamlPath { get; private init; }

    public string? ProjectedOriginPath { get; private init; }

    public string? IndexPath { get; private init; }

    public MapYaml? Yaml { get; private init; }

    public ProjectedOrigin? ProjectedOrigin { get; private init; }

    public MapIndex? Index { get; private init; }

    /// <summary>打开目录（或目录内任意文件）。</summary>
    public static MapPackage Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string directory = System.IO.Directory.Exists(path)
            ? path
            : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))
              ?? throw new InvalidOperationException($"无法确定目录：{path}");

        if (!System.IO.Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"目录不存在：{directory}");
        }

        string[] files = System.IO.Directory.GetFiles(directory);
        string? pcd = Pick(files, ".pcd");
        string pcdStem = pcd is null ? string.Empty : System.IO.Path.GetFileNameWithoutExtension(pcd);

        // PGM/yaml 优先取与 PCD 同名 stem 的那份（一个目录里可能有多张图）
        string? yaml = Pick(files, ".yaml", pcdStem) ?? Pick(files, ".yml", pcdStem) ?? Pick(files, ".yaml") ?? Pick(files, ".yml");
        string? pgm = Pick(files, ".pgm", pcdStem) ?? Pick(files, ".pgm");

        string? projected = files.FirstOrDefault(
            f => string.Equals(System.IO.Path.GetFileName(f), ".projected_origin", StringComparison.OrdinalIgnoreCase));

        string? index = files.FirstOrDefault(
            f => System.IO.Path.GetFileName(f).EndsWith("_id_map.toml", StringComparison.OrdinalIgnoreCase));

        MapYaml? parsedYaml = null;
        if (yaml is not null)
        {
            try
            {
                parsedYaml = MapYaml.Load(yaml);
            }
            catch (Exception)
            {
                // yaml 坏了不该让整个包打不开
            }
        }

        ProjectedOrigin? projectedOrigin = null;
        if (projected is not null)
        {
            try
            {
                projectedOrigin = ProjectedOrigin.ParseFile(projected);
            }
            catch (Exception)
            {
            }
        }

        MapIndex? parsedIndex = null;
        if (index is not null)
        {
            try
            {
                parsedIndex = MapIndex.ParseFile(index);
            }
            catch (Exception)
            {
            }
        }

        return new MapPackage(directory)
        {
            PcdPath = pcd,
            PgmPath = pgm,
            YamlPath = yaml,
            ProjectedOriginPath = projected,
            IndexPath = index,
            Yaml = parsedYaml,
            ProjectedOrigin = projectedOrigin,
            Index = parsedIndex,
        };
    }

    private static string? Pick(IEnumerable<string> files, string extension, string? preferredStem = null)
    {
        List<string> candidates = files
            .Where(f => string.Equals(System.IO.Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static f => f, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(preferredStem))
        {
            string? match = candidates.FirstOrDefault(
                f => string.Equals(System.IO.Path.GetFileNameWithoutExtension(f), preferredStem, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return candidates[0];
    }
}
