using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MyPcdEditor.Core.Grid;
using Vector = Avalonia.Vector;

namespace MyPcdEditor.App.Controls;

/// <summary>地图面板的光标读数（世界坐标 + 栅格值）。</summary>
public readonly record struct MapCursorInfo(
    bool HasMap,
    double WorldX,
    double WorldY,
    int Col,
    int Row,
    OccupancyCell? Cell);

/// <summary>
/// 2D occupancy 栅格视图：视口最近邻采样（不把 32.7 M px 整图扩成 RGBA），支持缩放/平移/点云叠加。
/// </summary>
/// <remarks>
/// 只在交互或尺寸变化时重绘（<see cref="Render"/> 里按需重建一张视口大小的位图），
/// 空闲时零开销；叠加层按世界 XY 投影，因此始终和栅格对齐。
/// </remarks>
public sealed class MapView : Control
{
    private const double OverlayAlpha = 0.72;

    private readonly MapViewTransform _transform = new();

    private PgmImage? _image;
    private MapYaml? _meta;
    private WriteableBitmap? _bitmap;
    private byte[] _frame = [];

    /// <summary>灰度值 → <see cref="OccupancyCell"/> 的查找表（0 空闲 / 1 未知 / 2 占用）。</summary>
    private byte[] _classes = [];
    private Vector3[] _overlay = [];
    private Point? _lastPointer;
    private bool _needFit;
    private bool _showOverlay = true;

    public MapView() => Focusable = true;

    /// <summary>是否叠加点云的 XY 投影。</summary>
    public bool ShowOverlay
    {
        get => _showOverlay;
        set
        {
            if (_showOverlay == value)
            {
                return;
            }

            _showOverlay = value;
            InvalidateVisual();
        }
    }

    public bool HasMap => _image is not null && _meta is not null;

    /// <summary>叠加层的点数（诊断用）。</summary>
    public int OverlayCount => _overlay.Length;

    /// <summary>当前缩放（每米像素数），供状态栏显示。</summary>
    public double PixelsPerMeter => _transform.PixelsPerMeter;

    /// <summary>光标移动读数（UI 线程）。</summary>
    public event Action<MapCursorInfo>? CursorMoved;

    /// <summary>换栅格图；<paramref name="meta"/> 决定配色与像素→世界换算。</summary>
    public void SetMap(PgmImage image, MapYaml meta)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(meta);

        _image = image;
        _meta = meta;
        _classes = BuildClassLut(meta);
        _needFit = true;
        InvalidateVisual();
    }

    public void ClearMap()
    {
        _image = null;
        _meta = null;
        _overlay = [];
        InvalidateVisual();
    }

    /// <summary>点云叠加层（世界坐标 XY，世界 Z 忽略）。</summary>
    public void SetOverlay(Vector3[]? points)
    {
        _overlay = points ?? [];
        InvalidateVisual();
    }

    /// <summary>按栅格覆盖范围整体适配；无图时什么都不做。</summary>
    public void FitToMap()
    {
        if (!TryFitCore())
        {
            return;
        }

        InvalidateVisual();
    }

    /// <summary>适配计算本体；返回是否有图可适配。</summary>
    private bool TryFitCore()
    {
        if (_image is null || _meta is null)
        {
            return false;
        }

        _needFit = false;
        (double minX, double minY, double maxX, double maxY) =
            _meta.WorldExtent(_image.Width, _image.Height);
        _transform.FitToWorld(minX, minY, maxX, maxY);
        return true;
    }

    public override void Render(DrawingContext context)
    {
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int pixelWidth = (int)Math.Round(Bounds.Width * scaling);
        int pixelHeight = (int)Math.Round(Bounds.Height * scaling);

        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return;
        }

        _transform.Resize(pixelWidth, pixelHeight);

        if (_needFit)
        {
            // 渲染过程中不能 InvalidateVisual，直接算
            _ = TryFitCore();
        }

        EnsureBitmap(pixelWidth, pixelHeight);

        if (_image is null || _meta is null)
        {
            DrawPlaceholder(context, pixelWidth, pixelHeight);
            return;
        }

        FillFrame(pixelWidth, pixelHeight);
        Upload();

        context.DrawImage(
            _bitmap!,
            new Rect(0, 0, pixelWidth / scaling, pixelHeight / scaling));
    }

    /// <summary>
    /// 按视口采样填满 <see cref="_frame"/>，再叠加点云。
    /// </summary>
    /// <remarks>
    /// 缩小时一个屏幕像素覆盖多个源像素，纯最近邻会把 1~2 px 的墙整段漏掉（实测只剩几十个亮像素）。
    /// 这里在每个屏幕像素覆盖的源矩形里取不超过 4×4 个采样点、按"占用 &gt; 未知 &gt; 空闲"取最大值，
    /// 相当于缩小用的 max-pooling：墙体不会丢，代价是 16 次读/像素。
    /// </remarks>
    private void FillFrame(int pixelWidth, int pixelHeight)
    {
        PgmImage image = _image!;
        MapYaml meta = _meta!;

        (int Start, int End)[] columns = BuildSourceRanges(pixelWidth, image.Width, meta, horizontal: true);
        (int Start, int End)[] rows = BuildSourceRanges(pixelHeight, image.Height, meta, horizontal: false);

        byte[] pixels = image.Pixels;
        byte[] classes = _classes;
        int width = image.Width;

        for (var y = 0; y < pixelHeight; y++)
        {
            (int rowStart, int rowEnd) = rows[y];
            int offset = y * pixelWidth * 4;

            if (rowStart > rowEnd)
            {
                // 视口越出栅格范围：填面板底色
                FillOutside(_frame, offset, pixelWidth);
                continue;
            }

            int rowStep = Step(rowStart, rowEnd, out int rowCount);

            for (var x = 0; x < pixelWidth; x++)
            {
                (int colStart, int colEnd) = columns[x];

                if (colStart > colEnd)
                {
                    CopyColor(OccupancyCell.Free, _frame, offset);
                    offset += 4;
                    continue;
                }

                int colStep = Step(colStart, colEnd, out int colCount);
                var worst = 0;

                for (int r = 0, ry = rowStart; r < rowCount; r++, ry += rowStep)
                {
                    int rowBase = ry * width;
                    for (int c = 0, cx = colStart; c < colCount; c++, cx += colStep)
                    {
                        int cls = classes[pixels[rowBase + cx]];
                        if (cls > worst)
                        {
                            worst = cls;
                        }
                    }
                }

                CopyColor((OccupancyCell)worst, _frame, offset);
                offset += 4;
            }
        }

        if (ShowOverlay)
        {
            BlendOverlay(pixelWidth, pixelHeight);
        }
    }

    /// <summary>每个屏幕像素覆盖的源像素下标区间（含端点，越界记 Start &gt; End）。</summary>
    private (int Start, int End)[] BuildSourceRanges(int pixelCount, int sourceCount, MapYaml meta, bool horizontal)
    {
        var ranges = new (int Start, int End)[pixelCount];
        double scale = _transform.PixelsPerMeter;

        for (var i = 0; i < pixelCount; i++)
        {
            double a = horizontal
                ? _transform.CenterX + ((i - (pixelCount * 0.5)) / scale)
                : _transform.CenterY - ((i - (pixelCount * 0.5)) / scale);
            double b = horizontal
                ? _transform.CenterX + ((i + 1 - (pixelCount * 0.5)) / scale)
                : _transform.CenterY - ((i + 1 - (pixelCount * 0.5)) / scale);

            double origin = horizontal ? meta.OriginX : meta.OriginY;
            int s = (int)Math.Floor((Math.Min(a, b) - origin) / meta.Resolution);
            int e = (int)Math.Floor((Math.Max(a, b) - origin) / meta.Resolution);

            if (!horizontal)
            {
                // 行号与世界 Y 反向
                (s, e) = (sourceCount - 1 - e, sourceCount - 1 - s);
            }

            s = Math.Max(s, 0);
            e = Math.Min(e, sourceCount - 1);

            ranges[i] = s <= e ? (s, e) : (0, -1);
        }

        return ranges;
    }

    /// <summary>区间内最多取 4 个采样点，返回步长与数量。</summary>
    private static int Step(int start, int end, out int count)
    {
        int span = end - start;
        count = Math.Min(span + 1, 4);
        return count <= 1 ? 1 : span / (count - 1);
    }

    private static void CopyColor(OccupancyCell cell, byte[] frame, int offset)
    {
        (byte b, byte g, byte r, byte a) = OccupancyPalette.ToBgra(cell);
        frame[offset] = b;
        frame[offset + 1] = g;
        frame[offset + 2] = r;
        frame[offset + 3] = a;
    }

    private void BlendOverlay(int pixelWidth, int pixelHeight)
    {
        double halfWidth = pixelWidth * 0.5;
        double halfHeight = pixelHeight * 0.5;
        double scale = _transform.PixelsPerMeter;
        double centerX = _transform.CenterX;
        double centerY = _transform.CenterY;

        foreach (Vector3 point in _overlay)
        {
            int x = (int)(((point.X - centerX) * scale) + halfWidth);
            int y = (int)(((centerY - point.Y) * scale) + halfHeight);

            if ((uint)x >= (uint)pixelWidth || (uint)y >= (uint)pixelHeight)
            {
                continue;
            }

            int offset = ((y * pixelWidth) + x) * 4;
            _frame[offset] = Blend(_frame[offset], OccupancyPalette.OverlayB);
            _frame[offset + 1] = Blend(_frame[offset + 1], OccupancyPalette.OverlayG);
            _frame[offset + 2] = Blend(_frame[offset + 2], OccupancyPalette.OverlayR);
            _frame[offset + 3] = 255;
        }
    }

    private static byte Blend(byte baseValue, byte overlay)
        => (byte)((baseValue * (1 - OverlayAlpha)) + (overlay * OverlayAlpha));

    private static void FillOutside(byte[] frame, int offset, int pixelWidth)
    {
        for (var i = 0; i < pixelWidth; i++)
        {
            frame[offset] = 24;
            frame[offset + 1] = 26;
            frame[offset + 2] = 30;
            frame[offset + 3] = 255;
            offset += 4;
        }
    }

    /// <summary>预把 256 个灰度值分类；阈值来自 yaml，换图才需要重建。</summary>
    private static byte[] BuildClassLut(MapYaml meta)
    {
        var classes = new byte[256];
        for (var value = 0; value < 256; value++)
        {
            classes[value] = (byte)OccupancyPalette.Classify((byte)value, meta);
        }

        return classes;
    }

    private void EnsureBitmap(int pixelWidth, int pixelHeight)
    {
        if (_bitmap is { } existing && existing.PixelSize.Width == pixelWidth &&
            existing.PixelSize.Height == pixelHeight)
        {
            return;
        }

        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(
            new PixelSize(pixelWidth, pixelHeight),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        _frame = new byte[pixelWidth * pixelHeight * 4];
    }

    private void Upload()
    {
        int pixelWidth = _bitmap!.PixelSize.Width;

        using ILockedFramebuffer buffer = _bitmap.Lock();
        for (int y = 0; y < _bitmap.PixelSize.Height; y++)
        {
            Marshal.Copy(_frame, y * pixelWidth * 4, IntPtr.Add(buffer.Address, y * buffer.RowBytes), pixelWidth * 4);
        }
    }

    private void DrawPlaceholder(DrawingContext context, int pixelWidth, int pixelHeight)
    {
        var text = new FormattedText(
            "未加载 PGM 栅格图",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            13,
            Brushes.Gray);

        context.DrawText(text, new Point((Bounds.Width - text.Width) * 0.5, (Bounds.Height - text.Height) * 0.5));
    }

    // ---- 交互 ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!IsPointerOver || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _lastPointer = e.GetPosition(this);
        e.Pointer.Capture(this);
        Focus(); // 让 F（适配）/ R（复位）快捷键可用
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        Point position = e.GetPosition(this);
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

        if (_lastPointer is { } last && _image is not null)
        {
            _transform.Pan((position.X - last.X) * scaling, (position.Y - last.Y) * scaling);
            _lastPointer = position;
            InvalidateVisual();
        }

        RaiseCursor(position, scaling);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        _lastPointer = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (_image is null)
        {
            return;
        }

        Point position = e.GetPosition(this);
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        double factor = Math.Pow(1.25, e.Delta.Y);

        _transform.ZoomAt(position.X * scaling, position.Y * scaling, factor);
        InvalidateVisual();
        RaiseCursor(position, scaling);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.F:
                FitToMap();
                break;
            case Key.R:
                FitToMap();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>把屏幕点换算成世界坐标与栅格值，抛给状态栏。</summary>
    private void RaiseCursor(Point position, double scaling)
    {
        if (_image is null || _meta is null)
        {
            CursorMoved?.Invoke(new MapCursorInfo(false, 0, 0, -1, -1, null));
            return;
        }

        (double worldX, double worldY) = _transform.ScreenToWorld(position.X * scaling, position.Y * scaling);
        (int col, int row) = _meta.WorldToPixel(worldX, worldY, _image.Height);

        OccupancyCell? cell = (uint)col < (uint)_image.Width && (uint)row < (uint)_image.Height
            ? OccupancyPalette.Classify(_image[col, row], _meta)
            : null;

        CursorMoved?.Invoke(new MapCursorInfo(true, worldX, worldY, col, row, cell));
    }
}
