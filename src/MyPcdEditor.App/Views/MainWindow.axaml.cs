using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MyPcdEditor.App.Controls;
using MyPcdEditor.App.Rendering;
using MyPcdEditor.Core;
using MyPcdEditor.Core.Grid;
using MyPcdEditor.Core.Merge;
using MyPcdEditor.Core.PointCloud;
using MyPcdEditor.Rendering.Gles3;

namespace MyPcdEditor.App.Views;

public sealed partial class MainWindow : Window
{
    /// <summary>点云叠加层的抽样点数：足够画轮廓，又不会让每次重绘变慢。</summary>
    private const int OverlaySampleCount = 200_000;

    private string _detail = "未加载数据";
    private string _gridDetail = string.Empty;
    private string _sampleDetail = string.Empty;
    private long _cloudPointCount;
    private MapPackage? _package;

    /// <summary>当前栅格图（加载的原图或优化后的结果，2D 面板与地面纹理都从这里取）。</summary>
    private PgmImage? _grid;

    /// <summary>
    /// 当前栅格图对应的 yaml。加载时等于 <see cref="_package"/>.Yaml；合并后栅格范围变了，
    /// 得用 <see cref="GridMergeResult.Meta"/>，所以不能一直读包里的那份。
    /// </summary>
    private MapYaml? _gridYaml;

    /// <summary>最近一次合并的对齐报告（工具栏「查看上次对齐报告」据此可用）。</summary>
    private string _lastMergeReport = string.Empty;

    /// <summary>合并的取消句柄：合并是按分钟计的，必须给得出口。</summary>
    private CancellationTokenSource? _mergeCancellation;

    /// <summary>优化/导出进行中：禁止并发换图与再处理（算子读的是正在显示的那份数据）。</summary>
    private bool _busy;

    /// <summary>当前包是否带 PGM（自检据此判断地图视图该不该有内容）。</summary>
    internal bool HasGrid => _package?.PgmPath is not null;

    /// <summary>2D 地图视图（自检用）。</summary>
    internal MapView MapViewControl => Map;

    /// <summary>合并报告的确认方式：默认弹窗，测试可替换成自动采用（无窗口环境弹不出对话框）。</summary>
    internal Func<string, Task<bool>> ConfirmMergeReport { get; set; } = _ => Task.FromResult(true);

    /// <summary>替换原包的确认方式：默认弹窗，测试可替换成自动确认/取消。</summary>
    /// <remarks>替换是唯一会改动原始文件的动作，必须二次确认（列出要写回的文件与备份说明）。</remarks>
    internal Func<string, Task<bool>> ConfirmReplace { get; set; } = _ => Task.FromResult(true);

    public MainWindow()
    {
        InitializeComponent();

        View.FrameStatsUpdated += OnFrameStats;
        View.CloudReady += OnCloudReady;
        Map.CursorMoved += OnMapCursor;

        OverlayCheck.PropertyChanged += (_, e) =>
        {
            if (e.Property == CheckBox.IsCheckedProperty)
            {
                Map.ShowOverlay = OverlayCheck.IsChecked == true;
            }
        };

        // PGV_NO_GROUND=1：基准测试里关掉地面纹理做 A/B，不影响正常启动
        if (Environment.GetEnvironmentVariable("PGV_NO_GROUND") == "1")
        {
            GroundCheck.IsChecked = false;
            View.ShowGround = false;
        }

        // PGV_NO_MAP=1：同上，收起 2D 面板，用来隔离它对帧率的影响
        if (Environment.GetEnvironmentVariable("PGV_NO_MAP") == "1")
        {
            MapToggle.IsChecked = SetMapPanelVisible(false);
        }

        GroundCheck.PropertyChanged += (_, e) =>
        {
            if (e.Property == CheckBox.IsCheckedProperty)
            {
                View.ShowGround = GroundCheck.IsChecked == true;
            }
        };

        PointSizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                View.PointSize = (float)PointSizeSlider.Value;
            }
        };

        ColorModeBox.SelectionChanged += (_, _) =>
            View.ColorMode = ColorModeBox.SelectedIndex == 1 ? PointColorMode.Mono : PointColorMode.Height;

        DetailBox.SelectedIndex = View.DrawStep switch { 2 => 1, 4 => 2, 8 => 3, _ => 0 };

        DetailBox.SelectionChanged += (_, _) =>
        {
            View.DrawStep = DetailBox.SelectedIndex switch
            {
                1 => 2,
                2 => 4,
                3 => 8,
                _ => 1,
            };
            UpdateDrawnDetail();
        };

        ConfirmMergeReport = report =>
            new ReportDialog("合并：对齐报告", report, "采用结果").ShowDialog<bool>(this);

        ConfirmReplace = summary =>
            new ReportDialog("替换：覆盖当前地图包", summary, "替换", "取消").ShowDialog<bool>(this);

        // 框选：按钮与 B 键是同一个开关，状态由视图发回来（按键切换时按钮要跟着变）
        BoxSelectToggle.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty)
            {
                View.SetBoxSelectMode(BoxSelectToggle.IsChecked == true);
            }
        };

        View.BoxSelectModeChanged += enabled =>
        {
            if (BoxSelectToggle.IsChecked != enabled)
            {
                BoxSelectToggle.IsChecked = enabled;
            }

            SetStatus(enabled
                ? "框选：左键拖出矩形，框内的点会被删掉（Shift + 拖 = 只保留框内）· 原始文件不动，重新加载即可撤销"
                : _detail + _gridDetail);
        };

        View.SelectionChanged += UpdateSelectionRect;
        View.BoxSelected += OnBoxSelected;

        Opened += OnOpened;

        if (StartupOptions.SelfTest)
        {
            // 自检要一个稳定的、足够大的渲染目标
            Width = 1280;
            Height = 800;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    /// <summary>支持 <c>MyPcdEditor.exe &lt;目录&gt;</c> 直接打开。</summary>
    private async void OnOpened(object? sender, EventArgs e)
    {
        if (StartupOptions.SelfTestDirectory is { } selfTestDirectory)
        {
            // 自检自己负责加载、判定与退出，不要走下面那条命令行目录的路径
            new SelfTest.SelfTestSession(this, View, selfTestDirectory).Start();
            return;
        }

        string[] args = Environment.GetCommandLineArgs();
        if (args.Length >= 2 && Directory.Exists(args[1]))
        {
            await LoadPackageAsync(args[1]);
        }
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "选择地图包目录（含 pcd / pgm / yaml）",
                AllowMultiple = false,
            });

        string? directory = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (directory is not null)
        {
            await LoadPackageAsync(directory);
        }
    }

    private void OnFitClick(object? sender, RoutedEventArgs e) => View.FitToCloud();

    private void OnResetClick(object? sender, RoutedEventArgs e) => View.ResetCamera();

    /// <summary>加载地图包；返回是否成功加载到点云（自检据此判定失败）。</summary>
    internal async Task<bool> LoadPackageAsync(string directory)
    {
        SetStatus("读取中…");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            (MapPackage Package, XyzCloud? Cloud, string GridDetail, PgmImage? Grid, Vector3[] Overlay,
                GroundTexture? Ground) result =
                await Task.Run(() =>
                {
                    MapPackage package = MapPackage.Open(directory);
                    XyzCloud? cloud = package.PcdPath is null ? null : XyzCloud.Open(package.PcdPath);

                    // 栅格只读一次（30 M px），摘要、2D 视图与地面纹理共用
                    PgmImage? grid = null;
                    GroundTexture? ground = null;
                    string gridDetail;
                    try
                    {
                        if (package.PgmPath is not null)
                        {
                            grid = PgmReader.Read(package.PgmPath);
                            gridDetail = DescribeGrid(package.Yaml, grid);

                            if (package.Yaml is not null)
                            {
                                // 降到 GPU 友好尺寸放在后台做：2048 上限 max-pooling 约 100 ms
                                var groundWatch = Stopwatch.StartNew();
                                ground = GroundTexture.Build(grid, package.Yaml);
                                Console.WriteLine($"[GL] 地面纹理构建：{grid.Width}×{grid.Height} → "
                                                  + $"{(ground is null ? "无" : $"{ground.Width}×{ground.Height}")}"
                                                  + $" / {groundWatch.ElapsedMilliseconds} ms");
                            }
                        }
                        else
                        {
                            gridDetail = string.Empty;
                        }
                    }
                    catch (Exception ex)
                    {
                        gridDetail = $" · 栅格读取失败({ex.GetType().Name})";
                    }

                    Vector3[] overlay = cloud?.SamplePoints(OverlaySampleCount) ?? [];
                    return (package, cloud, gridDetail, grid, overlay, ground);
                });

            stopwatch.Stop();
            _package = result.Package;
            _gridDetail = result.GridDetail;
            _grid = result.Grid;
            _gridYaml = result.Package.Yaml;

            if (result.Grid is not null && result.Package.Yaml is not null)
            {
                Map.SetMap(result.Grid, result.Package.Yaml);
                Map.SetOverlay(result.Overlay);
            }
            else
            {
                Map.ClearMap();
            }

            View.SetGroundTexture(result.Ground);

            if (result.Cloud is null)
            {
                _detail = $"{result.Package.DisplayName}：目录内没有 .pcd";
                SetStatus(_detail + _gridDetail);
                return false;
            }

            XyzCloud cloud = result.Cloud;
            Vector3 size = cloud.Bounds.Size;

            _detail = string.Create(CultureInfo.InvariantCulture,
                $"{result.Package.DisplayName} · {cloud.PointCount:N0} 点 · 读取 {stopwatch.ElapsedMilliseconds} ms · " +
                $"包围盒 {size.X:F0}×{size.Y:F0}×{size.Z:F0} m · " +
                $"{(cloud.IsZeroCopy ? "零拷贝" : "已解压")}");

            Title = $"MyPcdEditor — {result.Package.DisplayName}";
            View.SetCloud(cloud);
            SetStatus(_detail + " · 上传中…");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"打开失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void OnCloudReady(XyzCloud cloud)
    {
        _cloudPointCount = cloud.PointCount;
        UpdateDrawnDetail();
        SetStatus(_detail + _gridDetail);
    }

    // ── 框选删点 ─────────────────────────────────────────────────────────────
    //
    // 框是屏幕空间的矩形，在世界空间里是一个四棱锥；投影矩阵必须与渲染用的是同一个
    // （相机 + 视口宽高比都取视图当下的值），否则框和画面错位，删掉的就不是看到的那片点。

    /// <summary>把框选矩形画到 GL 视图上面那一层（归一化 → 像素）。</summary>
    private void UpdateSelectionRect(ScreenBox? box)
    {
        if (box is not { } area || area.IsEmpty)
        {
            SelectionRect.IsVisible = false;
            return;
        }

        double width = SelectionCanvas.Bounds.Width;
        double height = SelectionCanvas.Bounds.Height;
        Canvas.SetLeft(SelectionRect, area.MinU * width);
        Canvas.SetTop(SelectionRect, area.MinV * height);
        SelectionRect.Width = Math.Max(area.Width * width, 1);
        SelectionRect.Height = Math.Max(area.Height * height, 1);
        SelectionRect.IsVisible = true;
    }

    private async void OnBoxSelected(ScreenBox box, bool keepInside) => await ApplySelectionAsync(box, keepInside);

    /// <summary>把屏幕框选落到数据上：删掉框内的点（<paramref name="keepInside"/> = 只保留框内）。</summary>
    /// <param name="viewProjection">默认取视图当下的相机；测试传固定矩阵，避开相机状态。</param>
    /// <returns>是否真的改了数据。</returns>
    internal async Task<bool> ApplySelectionAsync(
        ScreenBox box, bool keepInside = false, Matrix4x4? viewProjection = null)
    {
        if (_busy)
        {
            return false;
        }

        if (View.CurrentCloud is not { } source)
        {
            SetStatus("没有点云可框选");
            return false;
        }

        if (box.IsEmpty)
        {
            SetStatus("框选范围太小，已忽略");
            return false;
        }

        Matrix4x4 viewProjectionMatrix = viewProjection ?? View.Camera.GetViewProjection(View.CurrentAspect);
        string name = keepInside ? "框选保留" : "框选删除";

        _busy = true;
        var watch = Stopwatch.StartNew();
        try
        {
            SetStatus($"{name}：处理中…");
            var progress = new Progress<double>(p => SetStatus($"{name}：{p:P0}"));

            XyzCloud result = await Task.Run(
                () => PointCloudOps.CropScreenBox(source, viewProjectionMatrix, box, keepInside, progress));
            watch.Stop();

            int removed = source.PointCount - result.PointCount;
            if (removed == 0)
            {
                SetStatus($"{name}：框内没有点（原数据不变）");
                return false;
            }

            if (result.PointCount == 0)
            {
                // 全删光等于把地图删了：多半是框拖大了，宁可不动
                SetStatus($"{name}：这会把点全删光，已放弃（换个更小的框，或改用「只保留框内」）");
                return false;
            }

            _detail = string.Create(CultureInfo.InvariantCulture,
                $"{_package?.DisplayName ?? "点云"} · {result.PointCount:N0} 点 · " +
                $"{name} -{removed:N0} {watch.ElapsedMilliseconds} ms");

            View.SetCloud(result);
            Map.SetOverlay(result.SamplePoints(OverlaySampleCount));
            SetStatus(_detail + _gridDetail + " · 上传中… · 撤销：优化 ▾ → 撤销全部优化（重新加载）");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"{name} 失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>状态栏的抽样提示：只在真的抽样时出现。</summary>
    private void UpdateDrawnDetail()
    {
        int step = Math.Max(View.DrawStep, 1);
        _sampleDetail = step <= 1 || _cloudPointCount <= 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture,
                $" · 绘制 1/{step}（{PointCloudRenderer.SamplePointCount((int)_cloudPointCount, step):N0} 点）");
    }

    private void OnFrameStats(GlControlBase control)
    {
        string renderer = control.Info?.Renderer ?? "?";
        string size = $"{control.PixelWidth}×{control.PixelHeight}";
        string progress = control is PointCloudView { IsUploading: true } view
            ? $" · 上传 {view.UploadProgress:P0}"
            : string.Empty;

        // 剔除真的省下东西时才提，平时「62/62」只是噪声
        string chunks = control is PointCloudView { TotalChunkCount: > 0 } culled
                        && culled.VisibleChunkCount < culled.TotalChunkCount
            ? $" · 块 {culled.VisibleChunkCount}/{culled.TotalChunkCount}"
            : string.Empty;

        SetStatus(
            $"{_detail}{_sampleDetail}{chunks}{_gridDetail} · {size} · {control.Fps:F0} FPS{progress} · {renderer}");
    }

    /// <summary>栅格图概览（已读到的图直接复用，避免再解一遍 30 M px）。</summary>
    private static string DescribeGrid(MapYaml? yaml, PgmImage pgm)
    {
        if (yaml is null)
        {
            return $" · 栅格 {pgm.Width}×{pgm.Height}";
        }

        (double minX, double minY, double maxX, double maxY) = yaml.WorldExtent(pgm.Width, pgm.Height);
        return $" · 栅格 {pgm.Width}×{pgm.Height}" + string.Create(CultureInfo.InvariantCulture,
            $" · 覆盖 {maxX - minX:F0}×{maxY - minY:F0} m @ {yaml.Resolution:F3} · 原点({minX:F0},{minY:F0})");
    }

    /// <summary>2D 地图面板开关（隐藏时把宽度让给 3D 视图）。</summary>
    private void OnMapToggleClick(object? sender, RoutedEventArgs e)
    {
        MapToggle.IsChecked = SetMapPanelVisible(MapToggle.IsChecked == true);
    }

    /// <summary>显式设置 2D 面板可见性，返回最终状态。</summary>
    private bool SetMapPanelVisible(bool visible)
    {
        MapPanel.IsVisible = visible;
        MapSplitter.IsVisible = visible;

        // 光设 IsVisible 不够：星号列仍占位，必须把列宽一起归零
        ContentGrid.ColumnDefinitions[1].Width = visible ? new GridLength(4) : new GridLength(0);
        ContentGrid.ColumnDefinitions[2].Width =
            visible ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        return visible;
    }

    private void OnMapCursor(MapCursorInfo info)
    {
        MapCursorText.Text = info.HasMap
            ? string.Create(CultureInfo.InvariantCulture,
                $"光标：x={info.WorldX:F1} y={info.WorldY:F1} m · 像素({info.Col},{info.Row}) · {DescribeCell(info.Cell)} · {Map.PixelsPerMeter:F1} px/m")
            : "光标：—";
    }

    private static string DescribeCell(OccupancyCell? cell) => cell switch
    {
        OccupancyCell.Occupied => "占用",
        OccupancyCell.Free => "空闲",
        OccupancyCell.Unknown => "未知",
        _ => "越界",
    };


    private void SetStatus(string text) => StatusText.Text = text;

    // ── M3 优化算子 ───────────────────────────────────────────────────────────
    //
    // 约定：
    //  · 原始文件一律不动，处理结果只替换视图里的数据（「撤销全部优化」= 重新加载）；
    //  · 算子都在后台线程跑，_busy 挡住并发（换图 / 再点菜单）；
    //  · 点云结果由 View.SetCloud 接管所有权，旧云在那里释放。

    private async void OnVoxelDownsampleClick(object? sender, RoutedEventArgs e)
    {
        if (View.Cloud is not { } cloud || _busy)
        {
            return;
        }

        string[]? values = await PromptAsync(
            "体素降采样",
            $"当前 {cloud.PointCount:N0} 点。每个体素只保留一个质心点，是最快的压缩手段。",
            ("体素边长（米）", "0.20", "0.1~0.5 常见；越大越稀"));

        if (values is null)
        {
            return;
        }

        if (!TryParseFloat(values[0], out float size) || size <= 0)
        {
            SetStatus("体素边长必须是正数");
            return;
        }

        await RunCloudOpAsync(
            $"体素降采样 {size:F2} m",
            (source, progress) => PointCloudOps.VoxelDownsample(source, size, progress));
    }

    private async void OnOutlierRemovalClick(object? sender, RoutedEventArgs e)
    {
        if (View.Cloud is not { } cloud || _busy)
        {
            return;
        }

        // 实测（8 核）：5.4 M 点 k=8 约 8 s，16.5 M 点约 25 s。再往上就该先降采样了
        if (cloud.PointCount > 10_000_000)
        {
            SetStatus($"统计去噪要做近邻搜索：{cloud.PointCount:N0} 点要跑几十秒，建议先做一次体素降采样再回来");
        }

        string[]? values = await PromptAsync(
            "统计去噪 (SOR)",
            "删掉「最近 k 个邻居的平均距离」明显偏大的点（阈值 = 均值 + 倍数 × 标准差）。",
            ("近邻个数 k", "8", "8~30 常用"),
            ("标准差倍数", "1.0", "越小删得越多；1.0 约删 10~15%"));

        if (values is null)
        {
            return;
        }

        if (!int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int neighbors)
            || neighbors is < 2 or > 128)
        {
            SetStatus("近邻个数必须是 2~128 的整数");
            return;
        }

        if (!TryParseFloat(values[1], out float ratio) || ratio < 0)
        {
            SetStatus("标准差倍数必须是非负数");
            return;
        }

        await RunCloudOpAsync(
            $"SOR k={neighbors} ×{ratio:F2}",
            (source, progress) => PointCloudOps.RemoveStatisticalOutliers(source, neighbors, ratio, progress));
    }

    private async void OnCropClick(object? sender, RoutedEventArgs e)
    {
        if (View.Cloud is not { } cloud || _busy)
        {
            return;
        }

        Bounds3 bounds = cloud.Bounds;
        string[]? values = await PromptAsync(
            "按范围裁剪",
            "只保留包围盒内的点（世界系坐标，单位米）。默认值就是当前点云的范围。",
            ("最小 X / Y / Z", $"{bounds.Min.X:F2}, {bounds.Min.Y:F2}, {bounds.Min.Z:F2}", "格式：x, y, z"),
            ("最大 X / Y / Z", $"{bounds.Max.X:F2}, {bounds.Max.Y:F2}, {bounds.Max.Z:F2}", "格式：x, y, z"));

        if (values is null)
        {
            return;
        }

        if (!TryParseVector(values[0], out Vector3 min) || !TryParseVector(values[1], out Vector3 max))
        {
            SetStatus("范围格式应为「x, y, z」");
            return;
        }

        if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
        {
            SetStatus("最小值不能大于最大值");
            return;
        }

        var box = new Bounds3(min, max);
        await RunCloudOpAsync(
            $"裁剪 X[{min.X:F0},{max.X:F0}] Y[{min.Y:F0},{max.Y:F0}]",
            (source, progress) => PointCloudOps.CropBox(source, box, progress: progress));
    }

    private async void OnDespeckleClick(object? sender, RoutedEventArgs e)
        => await RunGridOpAsync("去毛刺", (image, meta) => GridMorphology.Despeckle(image, meta));

    private async void OnCloseHolesClick(object? sender, RoutedEventArgs e)
        => await RunGridOpAsync("补洞", (image, meta) => GridMorphology.Close(image, meta));

    private async void OnRemoveIslandsClick(object? sender, RoutedEventArgs e)
    {
        if (_grid is null || _busy)
        {
            return;
        }

        string[]? values = await PromptAsync(
            "去孤岛",
            "面积小于阈值的占用连通块整块抹成未知（8 连通）。",
            ("最小面积（像素）", "25", "0.05 m/px 时 25 px ≈ 0.06 m²"));

        if (values is null)
        {
            return;
        }

        if (!int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minArea) || minArea < 0)
        {
            SetStatus("最小面积必须是整数");
            return;
        }

        await RunGridOpAsync(
            $"去孤岛 ≥{minArea} px",
            (image, meta) => GridMorphology.RemoveIslands(image, meta, minArea));
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "选择导出目录（写出完整地图包：pcd / pgm / yaml / 锚点 / toml）",
                AllowMultiple = false,
            });

        string? directory = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (directory is null)
        {
            return;
        }

        await ExportAsync(directory);
    }

    /// <summary>把当前视图里的数据导出成新的地图包（原始文件不动）。</summary>
    /// <remarks>
    /// 文件名 / 锚点 / toml 都参照当前打开的那个包，导出目录能被重新打开、也能继续参与合并
    /// （见 <see cref="MapPackageWriter"/>）。覆盖当前包不在导出范围内，那是「替换」。
    /// </remarks>
    internal async Task ExportAsync(string directory)
    {
        if (_busy)
        {
            return;
        }

        if (_package is not { } package)
        {
            SetStatus("先打开一个地图包——导出的文件名与锚点都参照它");
            return;
        }

        if (IsSameDirectory(directory, package.Directory))
        {
            SetStatus("导出目录就是当前打开的包：要覆盖原包请用「替换当前地图包」，导出请选另一个目录");
            return;
        }

        // 取最新的一份：换云之后、渲染帧之前 Cloud 还是旧的
        XyzCloud? cloud = View.CurrentCloud;
        PgmImage? grid = _grid;
        MapYaml? yaml = _gridYaml;

        _busy = true;
        try
        {
            SetStatus($"导出到 {directory} …");

            MapPackageWriteResult result = await Task.Run(
                () => MapPackageWriter.Write(directory, package, cloud, grid, yaml));

            if (result.Files.Count == 0)
            {
                SetStatus("导出失败：当前没有可写出的数据");
                return;
            }

            SetStatus($"已导出 {string.Join(" / ", result.Files)} → {directory}" +
                      (result.HasProjectedOrigin
                          ? " · 含锚点，可直接打开或继续合并"
                          : " · 原包没有锚点，导出件也无法参与自动对齐"));
        }
        catch (Exception ex)
        {
            SetStatus($"导出失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private static bool IsSameDirectory(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSameFile(string? left, string? right) =>
        !string.IsNullOrEmpty(left) &&
        !string.IsNullOrEmpty(right) &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private async void OnReplaceClick(object? sender, RoutedEventArgs e) => await ReplaceAsync();

    /// <summary>替换当前打开的包：把视图里的数据写回原目录（原文件改名 <c>*.bak</c> 备份）。</summary>
    /// <remarks>
    /// <para>
    /// 与「导出」的分工：导出写别的目录、原包不动；替换是**唯一会改动原始文件**的动作，
    /// 所以要二次确认、要备份，并且失败必须整包回滚（见 <see cref="MapPackageWriter.Replace"/>）。
    /// </para>
    /// <para>
    /// 零拷贝的云就是包里那份 pcd（打开后没优化过），文件还被映射着改不了名，内容也没变——
    /// 直接跳过 pcd，只写栅格与 yaml。优化 / 合并之后云是托管副本，没有文件占用，正常重写。
    /// </para>
    /// </remarks>
    internal async Task ReplaceAsync()
    {
        if (_busy)
        {
            return;
        }

        if (_package is not { } package)
        {
            SetStatus("先打开一个地图包——替换就是写回它的目录");
            return;
        }

        // 取最新的一份：换云之后、渲染帧之前 Cloud 还是旧的
        XyzCloud? cloud = View.CurrentCloud;
        PgmImage? grid = _grid;
        MapYaml? yaml = _gridYaml;

        if (cloud is null && grid is null && yaml is null)
        {
            SetStatus("没有可写回的数据");
            return;
        }

        bool skipPcd = cloud is { IsZeroCopy: true } && IsSameFile(cloud.SourcePath, package.PcdPath);

        if (!await ConfirmReplace(BuildReplaceSummary(package, cloud, grid, yaml, skipPcd)))
        {
            SetStatus("替换：已取消，原包未改动");
            return;
        }

        _busy = true;
        try
        {
            SetStatus($"替换 {package.Directory} …");

            MapPackageWriteResult result = await Task.Run(() => MapPackageWriter.Replace(
                package.Directory, package, skipPcd ? null : cloud, grid, yaml));

            // 点数 / 栅格范围都可能变了，重新指向磁盘上的真实状态
            _package = MapPackage.Open(package.Directory);
            _gridYaml = _package.Yaml ?? yaml;

            SetStatus($"已替换 {string.Join(" / ", result.Files)} → {package.Directory}" +
                      (skipPcd ? " · 点云没改过，未重写 pcd" : "") +
                      (result.Backups.Count > 0 ? $" · 备份 {string.Join(" ", result.Backups)}" : ""));
        }
        catch (Exception ex)
        {
            SetStatus($"替换失败，原包已还原：{ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>替换前的确认文案：要写什么、会不会动 pcd、备份放哪。</summary>
    private static string BuildReplaceSummary(
        MapPackage package, XyzCloud? cloud, PgmImage? grid, MapYaml? yaml, bool skipPcd)
    {
        var lines = new List<string>
        {
            $"目录：{package.Directory}",
            "",
            "写回内容：",
        };

        if (cloud is not null)
        {
            lines.Add(skipPcd
                ? $"  点云  {cloud.PointCount:N0} 点 · 与原文件一致，跳过 pcd"
                : $"  点云  {cloud.PointCount:N0} 点 → {Path.GetFileName(package.PcdPath ?? "map.pcd")}");
        }

        if (grid is not null)
        {
            lines.Add($"  栅格  {grid.Width}×{grid.Height} px" +
                      (yaml is null ? "" : $" @ {yaml.Resolution} m/px") +
                      $" → {Path.GetFileName(package.PgmPath ?? "map.pgm")}");
        }

        if (yaml is not null)
        {
            lines.Add($"  yaml  origin ({yaml.OriginX}, {yaml.OriginY}) → " +
                      $"{Path.GetFileName(package.YamlPath ?? "map.yaml")}");
        }

        lines.Add("");
        lines.Add("锚点与 id_map 沿用原包，原样保留。");
        lines.Add("原文件会先改名成 *.bak 备份（只保留最近一份）；任何一步失败都整包回滚。");
        return string.Join("\n", lines);
    }

    private async void OnRevertClick(object? sender, RoutedEventArgs e)
    {
        if (_package is null || _busy)
        {
            return;
        }

        await LoadPackageAsync(_package.Directory);
    }

    /// <summary>点云算子的统一执行：后台跑完替换视图数据，进度与耗时进状态栏。</summary>
    private async Task RunCloudOpAsync(string name, Func<XyzCloud, IProgress<double>, XyzCloud> operation)
    {
        if (View.CurrentCloud is not { } source)
        {
            SetStatus("没有点云可处理");
            return;
        }

        if (_busy)
        {
            return;
        }

        _busy = true;
        var watch = Stopwatch.StartNew();

        try
        {
            SetStatus($"{name}：处理中…");
            var progress = new Progress<double>(p => SetStatus($"{name}：{p:P0}"));

            XyzCloud result = await Task.Run(() => operation(source, progress));
            watch.Stop();

            if (result.PointCount == 0)
            {
                // 空结果不能 SetCloud：视图会变成"没有点云"，用户还以为是加载失败
                SetStatus($"{name}：参数把点全删光了，已放弃（原数据不变）");
                return;
            }

            Vector3 size = result.Bounds.Size;
            _detail = string.Create(CultureInfo.InvariantCulture,
                $"{_package?.DisplayName ?? "点云"} · {result.PointCount:N0} 点 · {name} {watch.ElapsedMilliseconds} ms · " +
                $"包围盒 {size.X:F0}×{size.Y:F0}×{size.Z:F0} m");

            View.SetCloud(result);
            Map.SetOverlay(result.SamplePoints(OverlaySampleCount));
            SetStatus(_detail + _gridDetail + " · 上传中…");
        }
        catch (Exception ex)
        {
            SetStatus($"{name} 失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>栅格算子的统一执行：换图 + 重建地面纹理（纹理构建也放后台，30 M px 要 200 ms）。</summary>
    private async Task RunGridOpAsync(string name, Func<PgmImage, MapYaml, GridMorphResult> operation)
    {
        if (_grid is not { } source || _gridYaml is not { } yaml)
        {
            SetStatus("没有栅格地图可处理");
            return;
        }

        if (_busy)
        {
            return;
        }

        _busy = true;
        var watch = Stopwatch.StartNew();

        try
        {
            SetStatus($"{name}：处理中…");

            GridMorphResult result = await Task.Run(() => operation(source, yaml));
            GroundTexture? ground = await Task.Run(() => GroundTexture.Build(result.Image, yaml));
            watch.Stop();

            _grid = result.Image;
            Map.SetMap(result.Image, yaml);
            View.SetGroundTexture(ground);

            _gridDetail = DescribeGrid(yaml, result.Image) + string.Create(CultureInfo.InvariantCulture,
                $" · {name} 改动 {result.ChangedPixels:N0} px {watch.ElapsedMilliseconds} ms");

            SetStatus(_detail + _gridDetail);
        }
        catch (Exception ex)
        {
            SetStatus($"{name} 失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    // ── M4 多包合并 ───────────────────────────────────────────────────────────
    //
    // 一次合并 = 两包的锚点定初值 → ICP 精配准 → 点云拼接（可选去重）+ 栅格合成。
    // 结果先出报告、由人确认后再替换视图：48 M 点的合并要一分多钟，不能让用户莫名其妙丢掉原视图；
    // 报告也是对齐质量的唯一凭据（初值来源、修正量、内点率），可复制进巡检记录。

    private async void OnMergeClick(object? sender, RoutedEventArgs e)
    {
        if (_package is null)
        {
            SetStatus("先打开一个地图包——它会作为合并的参考包");
            return;
        }

        if (_busy)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "选择要合并进来的地图包目录",
                AllowMultiple = false,
            });

        string? directory = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (directory is null)
        {
            return;
        }

        if (string.Equals(
                Path.GetFullPath(directory), Path.GetFullPath(_package.Directory), StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("不能把包和自己合并");
            return;
        }

        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        string[]? values = await PromptAsync(
            "合并地图包",
            $"把 {name} 并进当前包：先按 .projected_origin 的 UTM 锚点定初值，再跑点云 ICP 精配准。",
            ("重叠区去重体素（m）", "0.05", "0 = 保留两份点；0.05 左右能压掉重叠面上重复的点"));

        if (values is null)
        {
            return;
        }

        if (!TryParseFloat(values[0], out float dedupe) || dedupe < 0f)
        {
            SetStatus("去重体素必须是非负数");
            return;
        }

        await MergeAsync(directory, dedupe > 0f ? dedupe : null);
    }

    private void OnReportClick(object? sender, RoutedEventArgs e)
    {
        if (_lastMergeReport.Length > 0)
        {
            _ = new ReportDialog("上次合并：对齐报告", _lastMergeReport, "关闭").ShowDialog<bool>(this);
        }
    }

    private void OnCancelMergeClick(object? sender, RoutedEventArgs e)
    {
        if (_mergeCancellation is { IsCancellationRequested: false })
        {
            _mergeCancellation.Cancel();
            SetStatus("合并：正在取消…");
        }
    }

    /// <summary>把 <paramref name="directory"/> 的地图包并进当前参考包，返回是否替换了视图。</summary>
    /// <remarks>选目录与参数都在 <see cref="OnMergeClick"/> 里问，这里只做合并本身，便于无窗口测试调用。</remarks>
    internal async Task<bool> MergeAsync(string directory, float? dedupeVoxelSize)
    {
        if (_package is not { } reference)
        {
            SetStatus("先打开一个地图包——它会作为合并的参考包");
            return false;
        }

        if (_busy)
        {
            return false;
        }

        _busy = true;
        _mergeCancellation = new CancellationTokenSource();
        MergeButton.IsEnabled = false;
        CancelMergeButton.IsVisible = true;
        var watch = Stopwatch.StartNew();

        try
        {
            SetStatus($"合并：读取 {Path.GetFileName(Path.TrimEndingDirectorySeparator(directory))} …");

            var progress = new Progress<double>(p => SetStatus($"合并：{p:P0}（参考 {reference.DisplayName}）"));
            CancellationToken token = _mergeCancellation.Token;

            MapPackage other = MapPackage.Open(directory);
            var options = new MergeOptions { DedupeVoxelSize = dedupeVoxelSize };

            MergeOutcome outcome = await Task.Run(
                () => MapMerger.Merge(reference, other, options, progress, token), token);

            watch.Stop();
            _lastMergeReport = outcome.Report;
            ReportMenuItem.IsEnabled = true;

            if (!await ConfirmMergeReport(outcome.Report))
            {
                outcome.Dispose();
                SetStatus($"合并：已放弃结果（{watch.Elapsed.TotalSeconds:F0} s）· 当前数据不变");
                return false;
            }

            await ApplyMergeAsync(outcome, watch.Elapsed);
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("合并已取消 · 当前数据不变");
            return false;
        }
        catch (Exception ex)
        {
            SetStatus($"合并失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            _busy = false;
            _mergeCancellation?.Dispose();
            _mergeCancellation = null;
            MergeButton.IsEnabled = true;
            CancelMergeButton.IsVisible = false;
        }
    }

    /// <summary>把合并产物换进视图（点云所有权交给 <see cref="PointCloudView.SetCloud"/>）。</summary>
    private async Task ApplyMergeAsync(MergeOutcome outcome, TimeSpan elapsed)
    {
        XyzCloud merged = outcome.Cloud;
        Vector3 size = merged.Bounds.Size;

        _detail = string.Create(CultureInfo.InvariantCulture,
            $"{outcome.ReferenceName} + {outcome.SourceName} · {merged.PointCount:N0} 点 · " +
            $"合并 {elapsed.TotalSeconds:F0} s · 包围盒 {size.X:F0}×{size.Y:F0}×{size.Z:F0} m");
        Title = $"MyPcdEditor — {outcome.ReferenceName} + {outcome.SourceName}（已合并）";

        View.SetCloud(merged);

        if (outcome.Grid is { } grid)
        {
            _grid = grid.Map;
            _gridYaml = grid.Meta;
            _gridDetail = DescribeGrid(grid.Meta, grid.Map) + string.Create(CultureInfo.InvariantCulture,
                $" · 合并 重叠 {grid.OverlapCount:N0} px");

            Map.SetMap(grid.Map, grid.Meta);

            // 44 M px 的合并图降到 GPU 尺寸要一两百毫秒，别卡在 UI 线程
            MapYaml meta = grid.Meta;
            PgmImage image = grid.Map;
            GroundTexture? ground = await Task.Run(() => GroundTexture.Build(image, meta));
            View.SetGroundTexture(ground);
        }

        Map.SetOverlay(merged.SamplePoints(OverlaySampleCount));
        SetStatus(_detail + _gridDetail + " · 上传中… · 结果只在内存里，按「导出」存成新的地图包目录");
    }

    private async Task<string[]?> PromptAsync(string title, string description, params (string Label, string Value, string Hint)[] fields)
    {
        var dialog = new ParameterDialog(title, description, fields);
        return await dialog.ShowDialog<string[]?>(this);
    }

    private static bool TryParseFloat(string text, out float value)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseVector(string text, out Vector3 value)
    {
        value = default;
        string[] parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        if (!TryParseFloat(parts[0], out float x) || !TryParseFloat(parts[1], out float y) || !TryParseFloat(parts[2], out float z))
        {
            return false;
        }

        value = new Vector3(x, y, z);
        return true;
    }
}
