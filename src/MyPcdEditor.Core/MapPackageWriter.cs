using MyPcdEditor.Core.Grid;
using MyPcdEditor.Core.PointCloud;

namespace MyPcdEditor.Core;

/// <summary>一次写包的产物。文件名不含目录，直接拿去拼状态栏文案。</summary>
/// <param name="Files">写出的文件名。</param>
/// <param name="Backups">替换时留下的备份文件名（<c>*.bak</c>），导出时为空。</param>
public sealed record MapPackageWriteResult(IReadOnlyList<string> Files, IReadOnlyList<string> Backups)
{
    /// <summary>是否写入了 <c>.projected_origin</c>（没有它就只能靠手工对齐）。</summary>
    public bool HasProjectedOrigin => Files.Contains(".projected_origin", StringComparer.OrdinalIgnoreCase);

    /// <summary>是否写入了 <c>*_id_map.toml</c>。</summary>
    public bool HasIndex => Files.Any(static f => f.EndsWith("_id_map.toml", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 把当前数据写成一个**完整的地图包目录**，结构对齐 <see cref="MapPackage"/> 的读取约定。
/// </summary>
/// <remarks>
/// <para>
/// 文件名、锚点、toml 全部沿用 <paramref name="reference"/>（当前打开的那个包）：
/// 写出的目录能被 <see cref="MapPackage.Open"/> 原样打开，里面还带着 <c>.projected_origin</c>，
/// 所以**能继续拿去和第三个包合并**。
/// </para>
/// <para>
/// 这一点是必须的，不是锦上添花：早先只写 pcd/pgm/yaml，导出的合并结果没有锚点，
/// 再合并时只能退回手工对齐（而手工对齐还没做）。
/// </para>
/// <para>
/// 合并后的栅格范围变了，<paramref name="gridYaml"/> 要传合并结果的那份（<see cref="Merge.GridMergeResult.Meta"/>），
/// 不能读包里的 yaml。
/// </para>
/// <para>
/// 写盘分两步（见 <see cref="Prepare"/> / <see cref="MapPackageWriteBatch.Commit"/>）：
/// 先把每个文件写成 <c>.tmp</c>，全部成功后才动目标目录里的文件。
/// 这样几百 MB 的 pcd 写到一半失败，目标目录里的旧包仍然完整。
/// </para>
/// </remarks>
public static class MapPackageWriter
{
    /// <summary>把一个地图包写到另一个目录（导出）。</summary>
    /// <param name="directory">目标目录（不存在会创建；不会清空目录里的其它文件）。</param>
    /// <param name="reference">当前打开的包：文件名约定、锚点、toml 从它来。</param>
    /// <param name="cloud">点云；null 则不写 pcd。</param>
    /// <param name="grid">栅格；null 则不写 pgm/yaml。</param>
    /// <param name="gridYaml">栅格对应的 yaml（合并后是合并结果那份）；null 则不写 yaml。</param>
    /// <param name="overwrite">目标里已有同名文件时是否覆盖。</param>
    /// <returns>写出的文件名。</returns>
    public static MapPackageWriteResult Write(
        string directory,
        MapPackage reference,
        XyzCloud? cloud,
        PgmImage? grid,
        MapYaml? gridYaml,
        bool overwrite = true)
    {
        using MapPackageWriteBatch batch = Prepare(directory, reference, cloud, grid, gridYaml, overwrite);
        return batch.Commit(overwrite);
    }

    /// <summary>
    /// 把结果写回<paramref name="directory"/>本身（替换原包），原文件先改名为 <c>*.bak</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="Write"/> 的区别只有提交方式：每个原有文件先改名成 <c>&lt;name&gt;.bak</c>（元数据操作，
    /// 几百 MB 也不额外拷一遍），再把新文件改名就位；任何一步失败都把 <c>.bak</c> 改回去。
    /// 所以替换失败时原包是**完整还原**的，不是「一半新一半旧」。
    /// </para>
    /// <para>
    /// 目标文件被别的程序（或本进程的零拷贝映射）占着时改名会失败并回滚——调用方应先放手 pcd
    /// （见 <c>MainWindow.ReplaceAsync</c>：数据没变过的零拷贝云直接跳过 pcd）。
    /// </para>
    /// </remarks>
    public static MapPackageWriteResult Replace(
        string directory,
        MapPackage reference,
        XyzCloud? cloud,
        PgmImage? grid,
        MapYaml? gridYaml)
    {
        using MapPackageWriteBatch batch = Prepare(directory, reference, cloud, grid, gridYaml);
        return batch.Commit(overwrite: true, keepBackup: true);
    }

    /// <summary>
    /// 只往目标目录写 <c>.tmp</c>，不碰已有文件。配合 <see cref="MapPackageWriteBatch.Commit"/> 使用。
    /// </summary>
    /// <param name="directory">目标目录。</param>
    /// <param name="reference">当前打开的包。</param>
    /// <param name="cloud">点云；null 则不写 pcd。</param>
    /// <param name="grid">栅格；null 则不写 pgm/yaml。</param>
    /// <param name="gridYaml">栅格对应的 yaml；null 则不写 yaml。</param>
    /// <param name="overwrite">false 时目标已有同名文件就直接拒绝（在写任何字节之前）。</param>
    public static MapPackageWriteBatch Prepare(
        string directory,
        MapPackage reference,
        XyzCloud? cloud,
        PgmImage? grid,
        MapYaml? gridYaml,
        bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(reference);

        var pending = new List<(string Final, string Temp)>();

        // 没有任何可写的数据时不要建目录：空目录会让人以为导出成功了
        if (cloud is null && grid is null && gridYaml is null)
        {
            return new MapPackageWriteBatch(pending);
        }

        Directory.CreateDirectory(directory);

        string pcdName = Named(reference.PcdPath, "map.pcd");
        string pgmName = Named(reference.PgmPath, Path.ChangeExtension(pcdName, ".pgm"));
        string yamlName = Named(reference.YamlPath, Path.ChangeExtension(pcdName, ".yaml"));

        if (cloud is not null)
        {
            Stage(Path.Combine(directory, pcdName), overwrite, pending,
                temp => PcdWriter.WriteBinaryXyz(temp, cloud));
        }

        if (grid is not null)
        {
            Stage(Path.Combine(directory, pgmName), overwrite, pending,
                temp => PgmWriter.Write(temp, grid));
        }

        if (gridYaml is not null)
        {
            // image 写同目录文件名（原包 yaml 里是另一台机器的绝对路径，带过去必然指错）
            Stage(Path.Combine(directory, yamlName), overwrite, pending,
                temp => gridYaml.Save(temp, grid is null ? yamlName : pgmName));
        }

        // 锚点：合并结果的局部坐标系就是参考包的局部坐标系，所以原样带过去
        if (reference.ProjectedOriginPath is { } origin && File.Exists(origin))
        {
            Stage(Path.Combine(directory, ".projected_origin"), overwrite, pending,
                temp => File.Copy(origin, temp, overwrite: true));
        }

        // 栅格索引：id/name 不变，整份复制（[[grids]] 段里的 path 指向目录，与本机路径无关）
        if (reference.IndexPath is { } index && File.Exists(index))
        {
            Stage(Path.Combine(directory, Path.GetFileName(index)), overwrite, pending,
                temp => File.Copy(index, temp, overwrite: true));
        }

        return new MapPackageWriteBatch(pending);
    }

    /// <summary>把一份内容写到 <c>&lt;目标&gt;.tmp</c>，登记待提交。</summary>
    private static void Stage(string finalPath, bool overwrite, List<(string, string)> pending, Action<string> write)
    {
        if (!overwrite && File.Exists(finalPath))
        {
            throw new IOException($"目标已存在同名文件：{finalPath}（如需覆盖请显式允许）");
        }

        string temp = finalPath + ".tmp";
        try
        {
            write(temp);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        pending.Add((finalPath, temp));
    }

    private static string Named(string? path, string fallback) =>
        string.IsNullOrEmpty(path) ? fallback : Path.GetFileName(path);

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// 一批已经写成 <c>.tmp</c>、还没就位的文件。<see cref="Commit"/> 才动目标目录里的原文件。
/// </summary>
/// <remarks>
/// 存在的理由只有一个：替换原包时不能「写成功一个换一个」。
/// 那样 pcd 换新、pgm 换失败，包就变成一半新一半旧，比不改还糟。
/// </remarks>
public sealed class MapPackageWriteBatch : IDisposable
{
    private readonly List<(string Final, string Temp)> _pending;
    private bool _committed;

    internal MapPackageWriteBatch(List<(string Final, string Temp)> pending) => _pending = pending;

    /// <summary>待写出的文件名（不含目录）。</summary>
    public IReadOnlyList<string> Files => [.. _pending.Select(static p => Path.GetFileName(p.Final))];

    /// <summary>一个文件都没有（没什么可写）。</summary>
    public bool IsEmpty => _pending.Count == 0;

    /// <summary>
    /// 让所有新文件就位。先写出的内容已经在 <c>.tmp</c> 里，这里只做改名。
    /// </summary>
    /// <param name="overwrite">目标已有同名文件时是否覆盖；false 且目标存在时抛 <see cref="IOException"/>。</param>
    /// <param name="keepBackup">覆盖时把原文件改名成 <c>&lt;name&gt;.bak</c> 保留（替换原包用）。</param>
    /// <exception cref="IOException">目标文件被占用或改名失败；此时已回滚到替换前的状态。</exception>
    public MapPackageWriteResult Commit(bool overwrite = true, bool keepBackup = false)
    {
        if (_committed)
        {
            throw new InvalidOperationException("这批文件已经提交过了。");
        }

        if (!overwrite)
        {
            foreach ((string final, _) in _pending)
            {
                if (File.Exists(final))
                {
                    throw new IOException($"目标已存在同名文件：{final}（如需覆盖请显式允许）");
                }
            }
        }

        var backups = new List<(string Final, string Backup)>();
        var installed = new List<string>();

        try
        {
            // 阶段 1：原文件让位。同目录改名是元数据操作，几百 MB 也不额外拷一遍
            foreach ((string final, _) in _pending)
            {
                if (!File.Exists(final))
                {
                    continue;
                }

                string backup = final + ".bak";
                File.Delete(backup); // 上一次替换留下的备份，只保留最近一份
                File.Move(final, backup);
                backups.Add((final, backup));
            }

            // 阶段 2：新文件就位
            foreach ((string final, string temp) in _pending)
            {
                File.Move(temp, final);
                installed.Add(final);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 回滚：新文件删掉，备份改回去。宁可整包还是旧的，也不要一半新一半旧
            foreach (string final in installed)
            {
                MapPackageWriter.TryDelete(final);
            }

            foreach ((string final, string backup) in backups)
            {
                if (File.Exists(backup) && !File.Exists(final))
                {
                    File.Move(backup, final, overwrite: true);
                }
            }

            throw new IOException($"写包失败，已回滚为替换前的文件：{ex.Message}", ex);
        }

        _committed = true;

        if (!keepBackup)
        {
            foreach ((_, string backup) in backups)
            {
                MapPackageWriter.TryDelete(backup);
            }
        }

        return new MapPackageWriteResult(
            [.. _pending.Select(static p => Path.GetFileName(p.Final))],
            keepBackup ? [.. backups.Select(static b => Path.GetFileName(b.Backup))] : []);
    }

    /// <summary>没有提交就丢弃已写出的 <c>.tmp</c>。</summary>
    public void Dispose()
    {
        if (_committed)
        {
            return;
        }

        foreach ((_, string temp) in _pending)
        {
            MapPackageWriter.TryDelete(temp);
        }
    }
}
