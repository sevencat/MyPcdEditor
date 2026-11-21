using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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

    /// <summary>笔刷光标最近的位置（归一化）：调半径时要按它重画圆环。</summary>
    private Vector2? _brushCursor;

    /// <summary>地面纹理重建的版本号：连涂几笔时只让最后发起的那次结果上屏。</summary>
    private int _groundVersion;

    /// <summary>撤销栈（最近 <see cref="UndoDepth"/> 步编辑）。</summary>
    private readonly List<EditSnapshot> _undo = [];

    /// <summary>撤销深度：再多也记不住，点云快照占的是真内存。</summary>
    private const int UndoDepth = 8;

    /// <summary>撤销栈的内存预算：过了就丢最旧的一步（48 M 点的副本一份就是 580 MB）。</summary>
    private const long UndoBudgetBytes = 2L << 30;

    /// <summary>当前包是否带 PGM（自检据此判断地图视图该不该有内容）。</summary>
    internal bool HasGrid => _package?.PgmPath is not null;

    /// <summary>2D 地图视图（自检用）。</summary>
    internal MapView MapViewControl => Map;

    /// <summary>当前栅格图（测试用来断言「撤销把像素换回去了」）。</summary>
    internal PgmImage? CurrentGrid => _grid;

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

        // 笔刷：按钮与 E 键是同一个开关；滑块直接改视图的像素半径
        BrushToggle.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty)
            {
                View.SetBrushMode(BrushToggle.IsChecked == true);
            }
        };

        BrushSizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                View.BrushRadiusPixels = BrushSizeSlider.Value;
                Map.BrushRadiusPixels = BrushSizeSlider.Value;
                UpdateBrushCursor(_brushCursor);
            }
        };

        View.BrushModeChanged += enabled =>
        {
            if (BrushToggle.IsChecked != enabled)
            {
                BrushToggle.IsChecked = enabled;
            }

            if (!enabled)
            {
                BrushCursor.IsVisible = false;
            }

            SetStatus(enabled
                ? "笔刷：按住左键涂抹，涂到的点会被删掉 · 右边滑块调半径 · 原始文件不动，重新加载即可撤销"
                : _detail + _gridDetail);
        };

        View.BrushStrokeChanged += UpdateBrushStroke;
        View.BrushPointerChanged += UpdateBrushCursor;
        View.BrushCompleted += OnBrushCompleted;

        // 2D 栅格擦除：同一个工具栏开关，G 键在地图上也能切
        GridEraseToggle.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty)
            {
                Map.SetEraseMode(GridEraseToggle.IsChecked == true);
            }
        };

        Map.EraseModeChanged += enabled =>
        {
            if (GridEraseToggle.IsChecked != enabled)
            {
                GridEraseToggle.IsChecked = enabled;
            }

            SetStatus(enabled
                ? "栅格擦除：在右侧地图上按住左键涂抹（右键平移、滚轮缩放）· 勾「擦成空闲」改刷空闲 · 原始文件不动，重新加载即可撤销"
                : _detail + _gridDetail);
        };

        Map.EraseCompleted += OnGridEraseCompleted;

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

            // 换了包：撤销栈里的旧数据跟新包没关系了
            ClearUndo();

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
        if (box.IsEmpty)
        {
            SetStatus("框选范围太小，已忽略");
            return false;
        }

        Matrix4x4 viewProjectionMatrix = viewProjection ?? View.Camera.GetViewProjection(View.CurrentAspect);
        return await ApplyCloudEditAsync(
            keepInside ? "框选保留" : "框选删除",
            "框内没有点（原数据不变）",
            (source, progress) => PointCloudOps.CropScreenBox(
                source, viewProjectionMatrix, box, keepInside, progress));
    }

    /// <summary>把一条笔迹落到数据上：删掉折线（圆头）覆盖的点。</summary>
    /// <param name="viewProjection">默认取视图当下的相机；测试传固定矩阵，避开相机状态。</param>
    /// <param name="aspect">默认取视图当下的宽高比；测试固定成 1，避开窗口尺寸。</param>
    /// <returns>是否真的改了数据。</returns>
    internal async Task<bool> ApplyBrushAsync(
        Vector2[] path, float radius, Matrix4x4? viewProjection = null, float? aspect = null)
    {
        if (path.Length == 0)
        {
            SetStatus("笔迹为空，已忽略");
            return false;
        }

        Matrix4x4 viewProjectionMatrix = viewProjection ?? View.Camera.GetViewProjection(View.CurrentAspect);
        float aspectValue = aspect ?? View.CurrentAspect;
        return await ApplyCloudEditAsync(
            "笔刷擦除",
            "笔刷范围内没有点（原数据不变）",
            (source, progress) => PointCloudOps.CropScreenBrush(
                source, viewProjectionMatrix, path, radius, aspectValue, progress));
    }

    /// <summary>把一次屏幕编辑算子落到数据上：busy 闸门 + 进度 + 全删保护 + 换云上屏。</summary>
    /// <param name="name">状态栏里的算子名（如「框选删除」「笔刷擦除」）。</param>
    /// <param name="emptyHint">一个点都没删掉时的说明。</param>
    private async Task<bool> ApplyCloudEditAsync(
        string name, string emptyHint, Func<XyzCloud, IProgress<double>, XyzCloud> edit)
    {
        if (_busy)
        {
            return false;
        }

        if (View.CurrentCloud is not { } source)
        {
            SetStatus("没有点云可编辑：先打开地图包");
            return false;
        }

        _busy = true;
        var watch = Stopwatch.StartNew();
        try
        {
            SetStatus($"{name}：处理中…");
            var progress = new Progress<double>(p => SetStatus($"{name}：{p:P0}"));

            XyzCloud result = await Task.Run(() => edit(source, progress));
            watch.Stop();

            int removed = source.PointCount - result.PointCount;
            if (removed <= 0)
            {
                SetStatus($"{name}：{emptyHint}");
                return false;
            }

            if (result.PointCount == 0)
            {
                // 全删光等于把地图删了：多半是框拖大了/笔刷太大，宁可不动
                SetStatus($"{name}：这会把点全删光，已放弃（换个更小的范围）");
                return false;
            }

            // 先把旧云的所有权收进撤销栈，再换新云：SetCloud 会在上传时释放旧云
            PushUndo(name, takeCloud: true);

            _cloudPointCount = result.PointCount;
            _detail = string.Create(CultureInfo.InvariantCulture,
                $"{_package?.DisplayName ?? "点云"} · {result.PointCount:N0} 点 · " +
                $"{name} -{removed:N0} {watch.ElapsedMilliseconds} ms");

            View.SetCloud(result);
            Map.SetOverlay(result.SamplePoints(OverlaySampleCount));
            UpdateDrawnDetail();
            SetStatus(_detail + _gridDetail + " · 上传中… · 撤销：Ctrl+Z");
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

    // ── 撤销栈 ───────────────────────────────────────────────────────────────
    //
    // 编辑类算子（框选/笔刷/栅格擦除/优化）动手前把「改动前的状态」压栈：
    // 点云是不可变对象，压栈只是拿个引用（零拷贝的旧云还挂在文件映射上，几乎不占内存）；
    // 所有权从视图转到撤销栈，撤销那一刻再交回视图。重新加载/合并会清空。
    //
    // 不做重做：撤销之后再编辑会把后面的步数丢掉，和大多数编辑器一致。

    /// <summary>一步可撤销的编辑：改动前的点云、栅格与状态栏文案。</summary>
    /// <param name="Cloud">改动前的点云（<c>null</c> = 这一步没动点云，只动了栅格）。</param>
    private sealed record EditSnapshot(
        string Label, XyzCloud? Cloud, PgmImage? Grid, MapYaml? GridYaml,
        long PointCount, string Detail, string GridDetail);

    /// <summary>把「改动前」的状态压进撤销栈。调用点必须在数据被改之前。</summary>
    /// <param name="label">状态栏上显示的这一步叫什么。</param>
    /// <param name="takeCloud">是否从视图取走点云的所有权（只动栅格的编辑传 false）。</param>
    private void PushUndo(string label, bool takeCloud)
    {
        XyzCloud? cloud = takeCloud ? View.DetachCloud() : null;

        _undo.Add(new EditSnapshot(label, cloud, _grid, _gridYaml, _cloudPointCount, _detail, _gridDetail));

        DropOldSnapshots();
        UpdateUndoButton();
    }

    /// <summary>丢掉过深/超预算的旧快照。丢掉的那份云要自己释放：视图已经不管它了。</summary>
    private void DropOldSnapshots()
    {
        while (_undo.Count > UndoDepth)
        {
            _undo[0].Cloud?.Dispose();
            _undo.RemoveAt(0);
        }

        // 8 步 × 48 M 点的副本能吃掉几个 G：按实际占用再砍一遍，至少留一步
        while (_undo.Count > 1 && UndoBytes() > UndoBudgetBytes)
        {
            _undo[0].Cloud?.Dispose();
            _undo.RemoveAt(0);
        }
    }

    /// <summary>快照当前占用的内存：栅格图按像素算，点云只算非零拷贝的（映射文件不算真占用）。</summary>
    private long UndoBytes()
    {
        long bytes = 0;
        foreach (EditSnapshot snapshot in _undo)
        {
            if (snapshot.Cloud is { IsZeroCopy: false } cloud)
            {
                bytes += cloud.ByteLength;
            }

            if (snapshot.Grid is { } grid)
            {
                bytes += (long)grid.Width * grid.Height;
            }
        }

        return bytes;
    }

    /// <summary>清空撤销栈（换包、合并这类「历史全作废」的场合）。</summary>
    private void ClearUndo()
    {
        foreach (EditSnapshot snapshot in _undo)
        {
            snapshot.Cloud?.Dispose();
        }

        _undo.Clear();
        UpdateUndoButton();
    }

    private void UpdateUndoButton()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        UndoButton.Content = _undo.Count > 0 ? $"撤销 {_undo[^1].Label} (Ctrl+Z)" : "撤销 (Ctrl+Z)";
    }

    private async void OnUndoClick(object? sender, RoutedEventArgs e) => await UndoAsync();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _ = UndoAsync();
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>撤销最近一次编辑：把点云/栅格换回改动前的那份。</summary>
    /// <returns>是否真的撤销了一步。</returns>
    internal async Task<bool> UndoAsync()
    {
        if (_busy)
        {
            return false;
        }

        if (_undo.Count == 0)
        {
            SetStatus("没有可撤销的编辑");
            return false;
        }

        EditSnapshot snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _busy = true;

        try
        {
            if (snapshot.Cloud is not null)
            {
                // 所有权交回视图：它会在下次换云时释放
                View.SetCloud(snapshot.Cloud);
                Map.SetOverlay(snapshot.Cloud.SamplePoints(OverlaySampleCount));
            }

            _cloudPointCount = snapshot.PointCount;
            _detail = snapshot.Detail;
            _gridDetail = snapshot.GridDetail;

            if (snapshot.Grid is not null && snapshot.GridYaml is not null)
            {
                _grid = snapshot.Grid;
                _gridYaml = snapshot.GridYaml;
                Map.SetMap(_grid, _gridYaml, fit: false);
                await RebuildGroundAsync(_grid, _gridYaml);
            }
            else if (_grid is not null)
            {
                // 快照里没栅格 = 那时也没有栅格，清掉 2D 面板与地面
                _grid = null;
                Map.ClearMap();
                View.SetGroundTexture(null);
            }

            UpdateDrawnDetail();
            SetStatus($"已撤销：{snapshot.Label} · {_detail}{_gridDetail}");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"撤销失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            _busy = false;
            UpdateUndoButton();
        }
    }

    // ── 笔刷 ─────────────────────────────────────────────────────────────────
    //
    // 拖动中只画折线（Avalonia 叠加层），松手才动数据：每帧重建 48 M 点的云不现实，
    // 一条笔迹攒成折线后一次性判定，和框选共用同一个投影矩阵。

    /// <summary>笔迹：粗折线（圆头圆角），几何上与 Core 判定的「折线胶囊」一致。</summary>
    private void UpdateBrushStroke(IReadOnlyList<Vector2> path, double radius)
    {
        double width = SelectionCanvas.Bounds.Width;
        double height = SelectionCanvas.Bounds.Height;

        if (path.Count < 2)
        {
            // 单点还没形成线段：留着圆环就够了
            BrushStroke.IsVisible = false;
            return;
        }

        BrushStroke.StrokeThickness = Math.Max(radius * width * 2, 2);
        BrushStroke.Points.Clear();
        foreach (Vector2 point in path)
        {
            BrushStroke.Points.Add(new Point(point.X * width, point.Y * height));
        }

        BrushStroke.IsVisible = true;
    }

    /// <summary>笔刷光标圆环：半径是像素值，宽高一致才是圆（与 Core 的 aspect 折算等价）。</summary>
    private void UpdateBrushCursor(Vector2? position)
    {
        _brushCursor = position;

        if (position is not { } at || !View.BrushMode)
        {
            BrushCursor.IsVisible = false;
            return;
        }

        double width = SelectionCanvas.Bounds.Width;
        double height = SelectionCanvas.Bounds.Height;
        double radius = View.BrushRadiusPixels;

        Canvas.SetLeft(BrushCursor, (at.X * width) - radius);
        Canvas.SetTop(BrushCursor, (at.Y * height) - radius);
        BrushCursor.Width = Math.Max(radius * 2, 2);
        BrushCursor.Height = Math.Max(radius * 2, 2);
        BrushCursor.IsVisible = true;
    }

    private async void OnBrushCompleted(Vector2[] path, double radius) => await ApplyBrushAsync(path, (float)radius);

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

            PushUndo(name, takeCloud: true);

            Vector3 size = result.Bounds.Size;
            _cloudPointCount = result.PointCount;
            _detail = string.Create(CultureInfo.InvariantCulture,
                $"{_package?.DisplayName ?? "点云"} · {result.PointCount:N0} 点 · {name} {watch.ElapsedMilliseconds} ms · " +
                $"包围盒 {size.X:F0}×{size.Y:F0}×{size.Z:F0} m");

            View.SetCloud(result);
            Map.SetOverlay(result.SamplePoints(OverlaySampleCount));
            UpdateDrawnDetail();
            SetStatus(_detail + _gridDetail + " · 上传中… · 撤销：Ctrl+Z");
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
            watch.Stop();

            if (result.ChangedPixels == 0)
            {
                // 一个像素都没改：别往撤销栈里塞一步「什么都没变」
                SetStatus($"{name}：没有可改的像素（原数据不变）");
                return;
            }

            GroundTexture? ground = await Task.Run(() => GroundTexture.Build(result.Image, yaml));

            PushUndo(name, takeCloud: false);

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

    // ── 2D 栅格擦除 ───────────────────────────────────────────────────────────
    //
    // 与 3D 笔刷同构：拖动只画预览，松手才改像素。栅格改完立刻刷新小地图，
    // 地面纹理跟着重建（30.7 M px 实测 38 ms，等得起），否则 3D 里的地面和栅格会对不上。

    private async void OnGridEraseCompleted(Vector2[] path, double radiusGrid)
        => await ApplyGridEraseAsync(path, radiusGrid, SelectedEraseTarget);

    /// <summary>工具栏勾「擦成空闲」= 刷空闲，否则刷未知。</summary>
    private OccupancyCell SelectedEraseTarget
        => GridEraseFreeCheck.IsChecked == true ? OccupancyCell.Free : OccupancyCell.Unknown;

    /// <summary>把一条栅格笔迹落到数据上：把笔迹覆盖的像素刷成未知 / 空闲。</summary>
    /// <param name="path">栅格像素坐标折线（连续值）。</param>
    /// <param name="radiusGrid">笔刷半径（栅格像素）。</param>
    /// <returns>是否真的改了栅格。</returns>
    internal async Task<bool> ApplyGridEraseAsync(
        Vector2[] path, double radiusGrid, OccupancyCell target = OccupancyCell.Unknown)
    {
        if (_busy)
        {
            return false;
        }

        if (_grid is not { } source || _gridYaml is not { } yaml)
        {
            SetStatus("没有栅格地图可擦除：先打开地图包");
            return false;
        }

        var watch = Stopwatch.StartNew();

        try
        {
            GridMorphResult result = await Task.Run(
                () => GridEraser.Erase(source, yaml, path, radiusGrid, target));
            watch.Stop();

            if (result.ChangedPixels == 0)
            {
                SetStatus($"栅格擦除：这片已经是{DescribeCell(target)}（原数据不变）");
                return false;
            }

            PushUndo($"栅格擦除→{DescribeCell(target)}", takeCloud: false);

            _grid = result.Image;
            // fit: false —— 每涂一笔都把画面缩回全图的话根本没法连续作业
            Map.SetMap(result.Image, yaml, fit: false);

            _gridDetail = DescribeGrid(yaml, result.Image) + string.Create(CultureInfo.InvariantCulture,
                $" · 栅格擦除→{DescribeCell(target)} {result.ChangedPixels:N0} px {watch.ElapsedMilliseconds} ms");

            SetStatus(_detail + _gridDetail);
            await RebuildGroundAsync(result.Image, yaml);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"栅格擦除失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string DescribeCell(OccupancyCell cell) => cell switch
    {
        OccupancyCell.Free => "空闲",
        OccupancyCell.Occupied => "占用",
        _ => "未知",
    };

    /// <summary>
    /// 按栅格重建地面纹理。<paramref name="yaml"/> 与栅格一起给：中途换了包的话重建的就不是当前这张。
    /// 实测 6811×4503（30.7 M px）只要 38 ms，直接等它，不值得为这点时间做一套合并逻辑；
    /// 连涂几笔时用版本号保证只有最后发起的那次结果上屏（前面的结果已经过时）。
    /// </summary>
    private async Task RebuildGroundAsync(PgmImage grid, MapYaml yaml)
    {
        int version = ++_groundVersion;

        try
        {
            GroundTexture? ground = await Task.Run(() => GroundTexture.Build(grid, yaml));
            if (version == _groundVersion)
            {
                View.SetGroundTexture(ground);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"地面纹理重建失败：{ex.GetType().Name}: {ex.Message} · 栅格已改，重新加载可复原");
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

        // 合并同时换了包与栅格坐标系，撤销栈里那些快照没法还原（重新加载才是退路）
        ClearUndo();

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
