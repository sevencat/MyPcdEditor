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
    private string _detail = "未加载数据";
    private string _gridDetail = string.Empty;
    private MapPackage? _package;

    public MainWindow()
    {
        InitializeComponent();

        View.FrameStatsUpdated += OnFrameStats;
        View.CloudReady += OnCloudReady;

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
    }

    /// <summary>支持 <c>MyPcdEditor.exe &lt;目录&gt;</c> 直接打开。</summary>
    private async void OnOpened(object? sender, EventArgs e)
    {
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

    private async Task LoadPackageAsync(string directory)
    {
        SetStatus("读取中…");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            (MapPackage Package, XyzCloud? Cloud, string GridDetail) result = await Task.Run(() =>
            {
                MapPackage package = MapPackage.Open(directory);
                XyzCloud? cloud = package.PcdPath is null ? null : XyzCloud.Open(package.PcdPath);

                // 栅格摘要只算一次：PGM 有 30 M px，不能跟着 FPS 刷新
                return (package, cloud, DescribeGrid(package));
            });

            stopwatch.Stop();
            _package = result.Package;
            _gridDetail = result.GridDetail;

            if (result.Cloud is null)
            {
                _detail = $"{result.Package.DisplayName}：目录内没有 .pcd";
                SetStatus(_detail + _gridDetail);
                return;
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
        }
        catch (Exception ex)
        {
            SetStatus($"打开失败：{ex.GetType().Name}: {ex.Message}");
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

    /// <summary>栅格图概览（M2 会把图本身画出来，这里先给读数）。</summary>
    private static string DescribeGrid(MapPackage? package)
    {
        if (package?.PgmPath is null)
        {
            return string.Empty;
        }

        try
        {
            PgmImage pgm = PgmReader.Read(package.PgmPath);
            MapYaml? yaml = package.Yaml;
            string extent = yaml is null
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture,
                    $" · 覆盖 {yaml.WorldExtent(pgm.Width, pgm.Height).MaxX - yaml.OriginX:F0}×{yaml.WorldExtent(pgm.Width, pgm.Height).MaxY - yaml.OriginY:F0} m @ {yaml.Resolution:F3}");
            return $" · 栅格 {pgm.Width}×{pgm.Height}{extent}";
        }
        catch (Exception ex)
        {
            return $" · 栅格读取失败({ex.GetType().Name})";
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
