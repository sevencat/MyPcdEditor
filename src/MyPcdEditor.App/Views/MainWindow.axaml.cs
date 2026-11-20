using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MyPcdEditor.App.Controls;
using MyPcdEditor.App.Rendering;
using MyPcdEditor.Core;
using MyPcdEditor.Core.Grid;
using MyPcdEditor.Core.PointCloud;
using MyPcdEditor.Rendering.Gles3;

namespace MyPcdEditor.App.Views;

public sealed partial class MainWindow : Window
{
    /// <summary>点云叠加层的抽样点数：足够画轮廓，又不会让每次重绘变慢。</summary>
    private const int OverlaySampleCount = 200_000;

    private string _detail = "未加载数据";
    private string _gridDetail = string.Empty;
    private MapPackage? _package;

    /// <summary>当前包是否带 PGM（自检据此判断地图视图该不该有内容）。</summary>
    internal bool HasGrid => _package?.PgmPath is not null;

    /// <summary>2D 地图视图（自检用）。</summary>
    internal MapView MapViewControl => Map;

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
                            gridDetail = DescribeGrid(package, grid);

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
        SetStatus(_detail + _gridDetail);
    }

    private void OnFrameStats(GlControlBase control)
    {
        string renderer = control.Info?.Renderer ?? "?";
        string size = $"{control.PixelWidth}×{control.PixelHeight}";
        string progress = control is PointCloudView { IsUploading: true } view
            ? $" · 上传 {view.UploadProgress:P0}"
            : string.Empty;

        SetStatus($"{_detail}{_gridDetail} · {size} · {control.Fps:F0} FPS{progress} · {renderer}");
    }

    /// <summary>栅格图概览（已读到的图直接复用，避免再解一遍 30 M px）。</summary>
    private static string DescribeGrid(MapPackage package, PgmImage pgm)
    {
        MapYaml? yaml = package.Yaml;
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
        bool visible = MapToggle.IsChecked == true;
        MapPanel.IsVisible = visible;
        MapSplitter.IsVisible = visible;

        // 光设 IsVisible 不够：星号列仍占位，必须把列宽一起归零
        ContentGrid.ColumnDefinitions[1].Width = visible ? new GridLength(4) : new GridLength(0);
        ContentGrid.ColumnDefinitions[2].Width =
            visible ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
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
}
