using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using WhiteBoard.Models;
using WhiteBoard.Services;

namespace WhiteBoard.Controls;

/// <summary>一条即将提交的绘制结果。</summary>
public sealed class StrokeCompletedEventArgs : EventArgs
{
    public StrokeCompletedEventArgs(WhiteboardItem item)
    {
        Item = item;
    }

    /// <summary>刚刚绘制完成的对象。</summary>
    public WhiteboardItem Item { get; }
}

/// <summary>选中对象被拖动完成后触发，携带移动前后的状态用于撤销。</summary>
public sealed class SelectionMovedEventArgs : EventArgs
{
    public SelectionMovedEventArgs(IReadOnlyList<(WhiteboardItem Item, WhiteboardItem Before, WhiteboardItem After)> entries)
    {
        Entries = entries;
    }

    public IReadOnlyList<(WhiteboardItem Item, WhiteboardItem Before, WhiteboardItem After)> Entries { get; }
}

/// <summary>擦除请求（当前保留在撤销栈中）。</summary>
public sealed class EraseCompletedEventArgs : EventArgs{
    public EraseCompletedEventArgs(IReadOnlyList<WhiteboardItem> removed, IReadOnlyList<WhiteboardItem> added)
    {
        Removed = removed;
        Added = added;
    }

    public IReadOnlyList<WhiteboardItem> Removed { get; }

    public IReadOnlyList<WhiteboardItem> Added { get; }
}

/// <summary>擦除模式。</summary>
public enum EraserMode
{
    /// <summary>整笔擦除：点到哪里，整条笔画消失。</summary>
    Stroke = 0,

    /// <summary>像素橡皮：只擦掉笔尖经过的部分。</summary>
    Pixel = 1
}

/// <summary>
/// 白板画布控件：负责输入采集（鼠标 / 触控笔 / 触摸）与实时渲染。
/// 所有绘制对象都存在 <see cref="Page"/> 里，本控件只做交互与显示。
/// </summary>
public class WhiteboardCanvas : Control
{
    private const double LaserTailSeconds = 0.55;
    private const double LaserFadeSeconds = 1.15;

    private readonly List<StrokePoint> _activePoints = new();
    private readonly LaserTrail _laser = new();
    private readonly DispatcherTimer _laserTimer;

    private WhiteboardItem? _activeItem;
    private PageBackgroundStyle _previewBackgroundStyle = PageBackgroundStyle.Blank;
    private bool _hasPreviewStyle;

    private bool _isDrawing;
    private bool _isPanning;
    private bool _isMovingSelection;
    private bool _isMarquee;
    private bool _panWithMiddle;
    private Point _pointerStartScreen;
    private Point _pointerStartDoc;
    private Point _lastDocPoint;
    private Vector _panOrigin;
    private Point _marqueeStart;
    private Point _marqueeEnd;
    private Vector _moveAccum;
    private readonly Dictionary<WhiteboardItem, WhiteboardItem> _moveStartStates = new();
    private long _erasedTick;

    public WhiteboardCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        // 光标保持系统默认：一体机上用触屏 / 触控笔，改光标没有意义还容易引起困惑

        _laserTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnLaserTick);
        _laserTimer.Stop();

        // 输入事件只在「冒泡」阶段注册一次。
        // 注意：绝不能同时注册 Tunnel | Bubble —— 那样同一个事件会触发两次处理函数，
        // 拖动、书写、擦除都会被重复执行（曾经的拖动飞出去就是因为这个）。
        AddHandler(PointerPressedEvent, OnPointerPressedInternal, RoutingStrategies.Bubble);
        AddHandler(PointerMovedEvent, OnPointerMovedInternal, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnPointerReleasedInternal, RoutingStrategies.Bubble);
        AddHandler(PointerWheelChangedEvent, OnPointerWheelInternal, RoutingStrategies.Bubble);

        // 键盘事件允许 handledEventsToo：即使子控件处理过也要能响应快捷键
        AddHandler(KeyDownEvent, OnKeyDownInternal, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnKeyUpInternal, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    // ─────────────────────────────────────────────────────────────
    //  依赖属性
    // ─────────────────────────────────────────────────────────────

    public static readonly StyledProperty<WhiteboardPage?> PageProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, WhiteboardPage?>(nameof(Page));

    public static readonly StyledProperty<WhiteboardTool> ToolProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, WhiteboardTool>(nameof(Tool), WhiteboardTool.Pen);

    public static readonly StyledProperty<Color> PenColorProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, Color>(nameof(PenColor), Colors.Black);

    public static readonly StyledProperty<double> PenThicknessProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, double>(nameof(PenThickness), 3d);

    public static readonly StyledProperty<ShapeFill> ShapeFillProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, ShapeFill>(nameof(ShapeFill), ShapeFill.None);

    public static readonly StyledProperty<EraserMode> EraserModeProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, EraserMode>(nameof(EraserMode), EraserMode.Pixel);

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, double>(nameof(Zoom), 1d);

    public static readonly StyledProperty<Vector> PanOffsetProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, Vector>(nameof(PanOffset), default(Vector));

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, bool>(nameof(ShowGrid), true);

    public static readonly StyledProperty<bool> ShowLaserProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, bool>(nameof(ShowLaser), true);

    /// <summary>为桌面批注层隐藏“页面底色”，让背景图/桌面直接透出。</summary>
    public static readonly StyledProperty<bool> TransparentBackgroundProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, bool>(nameof(TransparentBackground), false);

    /// <summary>是否绘制页面的背景截图（普通白板页使用）。</summary>
    public static readonly StyledProperty<bool> CaptureBackgroundProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, bool>(nameof(CaptureBackground), true);

    /// <summary>
    /// 批注层的“冻结画面”底图。
    ///
    /// 注意：它只属于本次批注会话，**不会写进页面数据**。
    /// 早期版本把截图塞进 Page.BackgroundImage，导致退出批注后整页背景被
    /// 截图覆盖、原有批注看不见——现在改用这个临时覆盖层，问题从根上消失。
    /// </summary>
    public static readonly StyledProperty<IImage?> OverlayBackgroundProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, IImage?>(nameof(OverlayBackground));

    /// <summary>冻结底图在文档坐标里占据的范围。</summary>
    public static readonly StyledProperty<Rect?> OverlayBackgroundRectProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, Rect?>(nameof(OverlayBackgroundRect));

    /// <summary>是否允许拖动平移（桌面批注层通常关闭，避免误拖动）。</summary>
    public static readonly StyledProperty<bool> AllowPanProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, bool>(nameof(AllowPan), true);

    public static readonly StyledProperty<double> EraserRadiusProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, double>(nameof(EraserRadius), 12d);

    /// <summary>正在编辑的文本对象（由宿主放置 TextBox 使用）。</summary>
    public static readonly StyledProperty<TextItem?> EditingTextProperty =
        AvaloniaProperty.Register<WhiteboardCanvas, TextItem?>(nameof(EditingText));

    public WhiteboardPage? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public WhiteboardTool Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    public Color PenColor
    {
        get => GetValue(PenColorProperty);
        set => SetValue(PenColorProperty, value);
    }

    public double PenThickness
    {
        get => GetValue(PenThicknessProperty);
        set => SetValue(PenThicknessProperty, value);
    }

    public ShapeFill ShapeFill
    {
        get => GetValue(ShapeFillProperty);
        set => SetValue(ShapeFillProperty, value);
    }

    public EraserMode EraserMode
    {
        get => GetValue(EraserModeProperty);
        set => SetValue(EraserModeProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public Vector PanOffset
    {
        get => GetValue(PanOffsetProperty);
        set => SetValue(PanOffsetProperty, value);
    }

    public bool ShowGrid
    {
        get => GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public bool ShowLaser
    {
        get => GetValue(ShowLaserProperty);
        set => SetValue(ShowLaserProperty, value);
    }

    public bool TransparentBackground
    {
        get => GetValue(TransparentBackgroundProperty);
        set => SetValue(TransparentBackgroundProperty, value);
    }

    public bool CaptureBackground
    {
        get => GetValue(CaptureBackgroundProperty);
        set => SetValue(CaptureBackgroundProperty, value);
    }

    /// <summary>批注层临时底图（不写入文档）。</summary>
    public IImage? OverlayBackground
    {
        get => GetValue(OverlayBackgroundProperty);
        set => SetValue(OverlayBackgroundProperty, value);
    }

    public Rect? OverlayBackgroundRect
    {
        get => GetValue(OverlayBackgroundRectProperty);
        set => SetValue(OverlayBackgroundRectProperty, value);
    }

    public bool AllowPan
    {
        get => GetValue(AllowPanProperty);
        set => SetValue(AllowPanProperty, value);
    }

    public double EraserRadius
    {
        get => GetValue(EraserRadiusProperty);
        set => SetValue(EraserRadiusProperty, value);
    }

    public TextItem? EditingText
    {
        get => GetValue(EditingTextProperty);
        set => SetValue(EditingTextProperty, value);
    }

    /// <summary>当前正在绘制的对象（供宿主显示光标提示）。</summary>
    public WhiteboardItem? ActiveItem => _activeItem;

    // ─────────────────────────────────────────────────────────────
    //  事件
    // ─────────────────────────────────────────────────────────────

    /// <summary>一个对象绘制完成，可以提交到撤销栈。</summary>
    public event EventHandler<StrokeCompletedEventArgs>? ItemCompleted;

    /// <summary>擦除完成。</summary>
    public event EventHandler<EraseCompletedEventArgs>? ItemsErased;

    /// <summary>选中的对象被拖动完成（宿主据此提交撤销）。</summary>
    public event EventHandler<SelectionMovedEventArgs>? SelectionMoved;

    /// <summary>请求开始编辑文本对象。</summary>
    public event EventHandler<TextItem>? TextEditRequested;

    /// <summary>选择集发生变化。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>缩放发生变化。</summary>
    public event EventHandler? ZoomChanged;

    // ─────────────────────────────────────────────────────────────
    //  坐标换算
    // ─────────────────────────────────────────────────────────────

    private Matrix DocumentToScreen => Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(PanOffset.X, PanOffset.Y);

    private Point ToDocument(Point screen)
    {
        if (Math.Abs(Zoom) < 1e-6)
            return screen;

        return new Point((screen.X - PanOffset.X) / Zoom, (screen.Y - PanOffset.Y) / Zoom);
    }

    /// <summary>把内部文档坐标转换为屏幕（DIP）坐标。</summary>
    public Point DocumentToViewport(Point doc) => DocumentToScreen.Transform(doc);

    /// <summary>计算刚好把整页内容放进当前视口的缩放与偏移。</summary>
    public void FitToViewport(double margin = 40)
    {
        var page = Page;
        if (page is null || Bounds.Width < 10 || Bounds.Height < 10)
            return;

        var content = ContentBounds(page);
        if (content.Width < 1 || content.Height < 1)
        {
            Zoom = 1;
            PanOffset = new Vector(0, 0);
            return;
        }

        var sx = (Bounds.Width - margin * 2) / content.Width;
        var sy = (Bounds.Height - margin * 2) / content.Height;
        var scale = Math.Clamp(Math.Min(sx, sy), 0.05, 8.0);

        Zoom = scale;
        PanOffset = new Vector(
            margin - content.X * scale,
            margin - content.Y * scale);

        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>把缩放重置为 100%。</summary>
    public void ResetZoom()
    {
        Zoom = 1;
        PanOffset = new Vector(24, 24);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// 让指定的世界矩形正好铺满当前视口（含 <paramref name="margin"/> 边距）。
    /// 桌面批注层用它把“文档坐标”和“屏幕逻辑坐标”对齐成 1:1。
    /// </summary>
    public void FitWorldRectToViewport(Rect world, double margin = 0)
    {
        if (world.Width < 1 || world.Height < 1)
            return;

        var availableW = Math.Max(1, Bounds.Width - margin * 2);
        var availableH = Math.Max(1, Bounds.Height - margin * 2);

        var scale = Math.Min(availableW / world.Width, availableH / world.Height);

        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
            return;

        Zoom = scale;
        PanOffset = new Vector(
            margin - world.X * scale + (availableW - world.Width * scale) / 2,
            margin - world.Y * scale + (availableH - world.Height * scale) / 2);

        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private static Rect ContentBounds(WhiteboardPage page)
    {
        var hasAny = false;
        var rect = default(Rect);

        foreach (var item in page.Items)
        {
            var b = item.Bounds;
            if (b.Width <= 0 || b.Height <= 0)
                continue;

            rect = hasAny ? rect.Union(b) : b;
            hasAny = true;
        }

        return hasAny ? rect : new Rect(0, 0, 0, 0);
    }

    // ─────────────────────────────────────────────────────────────
    //  渲染
    // ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var page = Page;
        if (page is null)
            return;

        var viewport = new Rect(Bounds.Size);

        // 每帧开始：清空画刷 / 画笔缓存
        _renderCache.BeginFrame();

        // 批注层：底色透明，让桌面 / 截图直接透出
        if (!TransparentBackground)
            context.FillRectangle(_renderCache.Brush(page.Background), viewport);

        // 当前视口在文档坐标里的范围——底纹只画这一块
        var visible = VisibleDocumentRect();

        using (context.PushTransform(DocumentToScreen))
        {
            var style = _hasPreviewStyle ? _previewBackgroundStyle : page.BackgroundStyle;
            var hasImage = page.HasBackgroundImage;
            var bg = BackgroundImageCache.Get(page.BackgroundImageBase64);

            // 批注层的临时冻结底图：画在页面内容之下、桌面之上
            if (OverlayBackground is { } overlay)
            {
                var rect = OverlayBackgroundRect ?? page.BackgroundImageRect;
                context.DrawImage(overlay, rect);
            }

            // 文档逻辑区域：有背景图（桌面截图）时严格贴图，否则给足够大的纸张范围
            var docRect = hasImage
                ? page.BackgroundImageRect
                : new Rect(-4000, -4000, 12000, 12000);

            // 网格 / 横线等底纹始终绘制；纯色底只在非透明模式下铺
            if (style != PageBackgroundStyle.Blank || !TransparentBackground)
            {
                var backgroundPage = _hasPreviewStyle
                    ? new WhiteboardPage { Background = page.Background, BackgroundStyle = _previewBackgroundStyle }
                    : page;

                if (TransparentBackground && !_hasPreviewStyle)
                {
                    // 透明模式：不铺底色，只画底纹
                    backgroundPage = new WhiteboardPage
                    {
                        Background = Colors.Transparent,
                        BackgroundStyle = style
                    };
                }

                // 只有需要时才画背景截图（批注层的“冻结画面”模式）
                var image = hasImage && (CaptureBackground || _hasPreviewStyle) ? bg : null;

                WhiteboardRenderer.DrawPageBackground(
                    context, backgroundPage, docRect, image, _renderCache,
                    hasImage ? (Rect?)null : visible);
            }

            // 已提交对象（只画落在视口内的，一页内容很多时能省下大量绘制）
            var cull = visible.Inflate(8 / Math.Max(0.05, Zoom));

            foreach (var item in page.Items)
            {
                var bounds = item.Bounds;

                if (bounds.Width > 0
                    && (bounds.Right < cull.X || bounds.X > cull.Right
                        || bounds.Bottom < cull.Y || bounds.Y > cull.Bottom))
                {
                    continue;
                }

                WhiteboardRenderer.DrawItem(context, item, _renderCache);
            }

            // 正在绘制的对象
            if (_activeItem is not null && !ReferenceEquals(_activeItem, EditingText))
                WhiteboardRenderer.DrawItem(context, _activeItem, _renderCache);

            // 激光笔
            if (ShowLaser)
                _laser.Render(context, _renderCache);

            // 选区
            if (_isMarquee)
            {
                var marquee = new Rect(_marqueeStart, _marqueeEnd);
                var pen = _renderCache.DashedPen(Color.FromArgb(200, 0, 122, 204), 1,
                    4 / Math.Max(0.2, Zoom), 3 / Math.Max(0.2, Zoom));

                context.DrawRectangle(_renderCache.Brush(Color.FromArgb(36, 0, 122, 204)), pen, marquee);
            }

            // 选中对象的虚线框
            DrawSelectionAdorners(context);
        }

    }

    /// <summary>当前视口对应的文档坐标范围（用于裁剪底纹绘制）。</summary>
    private Rect VisibleDocumentRect()
    {
        if (Math.Abs(Zoom) < 1e-6)
            return new Rect(0, 0, Bounds.Width, Bounds.Height);

        var topLeft = ToDocument(new Point(0, 0));
        var bottomRight = ToDocument(new Point(Bounds.Width, Bounds.Height));

        return new Rect(topLeft, bottomRight).Inflate(2 / Zoom);
    }

    private readonly RenderCache _renderCache = new();

    private void DrawSelectionAdorners(DrawingContext context)
    {
        var page = Page;
        if (page is null)
            return;

        var pen = _renderCache.Pen(Color.FromArgb(220, 0, 122, 204), 1.2 / Math.Max(0.2, Zoom));
        var pad = 4 / Math.Max(0.2, Zoom);

        foreach (var item in page.Items)
        {
            if (!item.IsSelected)
                continue;

            var b = item.Bounds.Inflate(pad);
            context.DrawRectangle(null, pen, b);

            var handle = 4 / Math.Max(0.2, Zoom);
            var brush = _renderCache.Brush(Color.FromArgb(255, 0, 122, 204));
            foreach (var corner in new[]
                     {
                         b.TopLeft, b.TopRight, b.BottomLeft, b.BottomRight
                     })
            {
                context.FillRectangle(brush,
                    new Rect(corner.X - handle / 2, corner.Y - handle / 2, handle, handle));
            }
        }
    }

    private Point _lastScreenPointer;

    // ─────────────────────────────────────────────────────────────
    //  指针输入
    // ─────────────────────────────────────────────────────────────

    private void OnPointerPressedInternal(object? sender, PointerPressedEventArgs e)
    {
        var page = Page;
        if (page is null)
            return;

        Focus();

        var point = e.GetCurrentPoint(this);
        var screen = point.Position;
        _lastScreenPointer = screen;

        var props = point.Properties;
        var isRight = props.IsRightButtonPressed;
        var isMiddle = props.IsMiddleButtonPressed;
        var isLeft = props.IsLeftButtonPressed;

        // 中键 / 右键 / 空格：平移画布
        if (isMiddle || isRight || (_panWithMiddle && isLeft) || (AllowPan && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            _isPanning = true;
            _panOrigin = PanOffset;
            _pointerStartScreen = screen;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var doc = ToDocument(screen);
        _pointerStartScreen = screen;
        _pointerStartDoc = doc;
        _lastDocPoint = doc;

        var tool = Tool;

        if (tool == WhiteboardTool.Select)
        {
            HandleSelectPress(page, doc, e);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (tool == WhiteboardTool.Text)
        {
            var hit = page.Items.OfType<TextItem>().LastOrDefault(t => t.HitTest(doc, 4 / Math.Max(0.2, Zoom)));
            if (hit is not null)
            {
                EditingText = hit;
                TextEditRequested?.Invoke(this, hit);
            }

            e.Handled = true;
            return;
        }

        if (tool == WhiteboardTool.Eraser)
        {
            EraseAt(doc);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (tool == WhiteboardTool.Laser)
        {
            _laser.Clear();
            _laser.Add(doc);
            if (!_laserTimer.IsEnabled)
                _laserTimer.Start();

            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // 笔 / 荧光笔 / 图形：开始一个新对象
        _isDrawing = true;
        _activePoints.Clear();

        var pressure = props.Pressure is > 0 and <= 1 ? props.Pressure : 1.0;
        var thickness = Math.Max(0.6, PenThickness);

        if (IsShapeTool(tool))
        {
            _activeItem = new ShapeItem
            {
                Tool = tool,
                Color = PenColor,
                Thickness = Math.Max(1, thickness),
                Fill = ShapeFill,
                Start = doc,
                End = doc
            };
        }
        else
        {
            _activePoints.Add(new StrokePoint(doc.X, doc.Y, pressure, Now()));
            _activeItem = new StrokeItem
            {
                Tool = tool,
                Color = PenColor,
                Thickness = Math.Max(0.6, thickness)
            };
            ((StrokeItem)_activeItem).Points.Add(_activePoints[0]);
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    private void OnPointerMovedInternal(object? sender, PointerEventArgs e)
    {
        var page = Page;
        if (page is null)
            return;

        var point = e.GetCurrentPoint(this);
        var screen = point.Position;
        _lastScreenPointer = screen;

        if (_isPanning)
        {
            PanOffset = _panOrigin + (screen - _pointerStartScreen);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (Tool == WhiteboardTool.Eraser && point.Properties.IsLeftButtonPressed)
        {
            EraseAt(ToDocument(screen));
            e.Handled = true;
            return;
        }

        if (Tool == WhiteboardTool.Laser && point.Properties.IsLeftButtonPressed)
        {
            _laser.Add(ToDocument(screen));
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        if (Tool == WhiteboardTool.Select)
        {
            HandleSelectMove(page, ToDocument(screen), e);

            // 关键：拖动结束后必须推进基准点，否则下一帧会把「自按下以来的总位移」
            // 再叠加一次，笔迹会以 2 倍速度飞出去。
            _lastDocPoint = ToDocument(screen);

            e.Handled = true;
            return;
        }

        if (!_isDrawing || _activeItem is null)
        {
            if (Tool == WhiteboardTool.Eraser)
                InvalidateVisual();

            return;
        }

        var doc = ToDocument(screen);

        switch (_activeItem)
        {
            case ShapeItem shape:
            {
                // Shift 约束为正方形 / 水平竖直
                var end = doc;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    end = ConstrainShape(shape.Start, doc, shape.Tool);

                shape.End = end;
                break;
            }

            case StrokeItem stroke:
            {
                var pressure = point.Properties.Pressure is > 0 and <= 1 ? point.Properties.Pressure : 1.0;

                // 快速移动时按距离插值补点，轨迹不会出现折角
                var dx = doc.X - _lastDocPoint.X;
                var dy = doc.Y - _lastDocPoint.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);

                var spacing = Math.Max(0.8, stroke.Thickness * 0.35);
                var steps = dist > spacing ? (int)Math.Min(64, dist / spacing) : 0;

                for (var i = 1; i <= steps; i++)
                {
                    var t = (double)i / (steps + 1);
                    stroke.Points.Add(new StrokePoint(
                        _lastDocPoint.X + dx * t,
                        _lastDocPoint.Y + dy * t,
                        pressure,
                        Now()));
                }

                stroke.Points.Add(new StrokePoint(doc.X, doc.Y, pressure, Now()));

                // 点太多时顺手压一压，避免内存无限增长
                if (stroke.Points.Count > 4000)
                {
                    var simplified = StrokeGeometryBuilder.Simplify(stroke.Points, 0.25);
                    stroke.Points.Clear();
                    stroke.Points.AddRange(simplified);
                }

                break;
            }
        }

        _lastDocPoint = doc;
        e.Handled = true;
        InvalidateVisual();
    }

    private void OnPointerReleasedInternal(object? sender, PointerReleasedEventArgs e)
    {
        var page = Page;
        if (page is null)
            return;

        e.Pointer.Capture(null);

        if (_isPanning)
        {
            _isPanning = false;
            e.Handled = true;
            return;
        }

        if (Tool == WhiteboardTool.Select)
        {
            FinishSelect();
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        if (Tool == WhiteboardTool.Eraser)
        {
            e.Handled = true;
            return;
        }

        if (Tool == WhiteboardTool.Laser)
        {
            e.Handled = true;
            return;
        }

        if (!_isDrawing || _activeItem is null)
            return;

        _isDrawing = false;

        var item = _activeItem;
        _activeItem = null;

        // 太短的手写点（误触）直接丢弃
        if (item is StrokeItem stroke)
        {
            if (stroke.Points.Count == 1)
            {
                var p = stroke.Points[0];
                var r = Math.Max(1.0, stroke.Thickness * 0.5);
                stroke.Points.Add(new StrokePoint(p.X + r * 0.35, p.Y + r * 0.35, p.Pressure, p.T));
            }

            if (stroke.Tool != WhiteboardTool.Laser && stroke.Points.Count > 2)
            {
                var simplified = StrokeGeometryBuilder.Simplify(stroke.Points, 0.35 / Math.Max(0.2, Zoom));
                stroke.Points.Clear();
                stroke.Points.AddRange(simplified);
            }
        }

        if (item is ShapeItem shapeItem)
        {
            // 点一下没拖动：取消这个图形
            var d = shapeItem.End - shapeItem.Start;
            if (Math.Abs(d.X) < 2 && Math.Abs(d.Y) < 2)
            {
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        ItemCompleted?.Invoke(this, new StrokeCompletedEventArgs(item));
        e.Handled = true;
        InvalidateVisual();
    }

    private void OnPointerWheelInternal(object? sender, PointerWheelEventArgs e)
    {
        if (!AllowPan)
            return;

        var screen = e.GetPosition(this);

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl + 滚轮：以光标为中心缩放
            var oldZoom = Zoom;
            var factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
            var newZoom = Math.Clamp(oldZoom * factor, 0.1, 8.0);

            if (Math.Abs(newZoom - oldZoom) > 1e-6)
            {
                var docPoint = ToDocument(screen);
                Zoom = newZoom;
                PanOffset = new Vector(
                    screen.X - docPoint.X * newZoom,
                    screen.Y - docPoint.Y * newZoom);

                ZoomChanged?.Invoke(this, EventArgs.Empty);
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        PanOffset = new Vector(PanOffset.X + e.Delta.X * 40, PanOffset.Y + e.Delta.Y * 40);
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnKeyDownInternal(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                _panWithMiddle = true;
                break;

            case Key.Delete:
            case Key.Back:
            {
                var page = Page;
                if (page is not null)
                {
                    var selected = page.Items.Where(i => i.IsSelected).ToList();
                    if (selected.Count > 0)
                    {
                        ItemsErased?.Invoke(this, new EraseCompletedEventArgs(selected, Array.Empty<WhiteboardItem>()));
                        e.Handled = true;
                    }
                }

                break;
            }

            case Key.Escape:
            {
                var page = Page;
                if (page is not null)
                {
                    var any = false;
                    foreach (var item in page.Items)
                    {
                        if (item.IsSelected)
                        {
                            item.IsSelected = false;
                            any = true;
                        }
                    }

                    if (any)
                        SelectionChanged?.Invoke(this, EventArgs.Empty);
                }

                break;
            }
        }
    }

    private void OnKeyUpInternal(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
            _panWithMiddle = false;
    }

    private static double Now() => Environment.TickCount64;

    private static bool IsShapeTool(WhiteboardTool tool)
        => tool is WhiteboardTool.Line or WhiteboardTool.Arrow or WhiteboardTool.Rectangle or WhiteboardTool.Ellipse;

    private static Point ConstrainShape(Point start, Point end, WhiteboardTool tool)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;

        if (tool is WhiteboardTool.Line or WhiteboardTool.Arrow)
        {
            // 约束到 0° / 45° / 90°
            var angle = Math.Atan2(dy, dx);
            var step = Math.PI / 4;
            var snapped = Math.Round(angle / step) * step;
            var len = Math.Sqrt(dx * dx + dy * dy);
            return new Point(start.X + Math.Cos(snapped) * len, start.Y + Math.Sin(snapped) * len);
        }

        var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new Point(start.X + Math.Sign(dx == 0 ? 1 : dx) * side,
                         start.Y + Math.Sign(dy == 0 ? 1 : dy) * side);
    }

    // ─────────────────────────────────────────────────────────────
    //  选择 / 移动
    // ─────────────────────────────────────────────────────────────

    private void HandleSelectPress(WhiteboardPage page, Point doc, PointerPressedEventArgs e)
    {
        var tolerance = 6 / Math.Max(0.2, Zoom);
        var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        WhiteboardItem? hit = null;
        for (var i = page.Items.Count - 1; i >= 0; i--)
        {
            if (page.Items[i].HitTest(doc, tolerance))
            {
                hit = page.Items[i];
                break;
            }
        }

        if (hit is not null)
        {
            if (additive)
            {
                hit.IsSelected = !hit.IsSelected;
            }
            else if (!hit.IsSelected)
            {
                foreach (var item in page.Items)
                    item.IsSelected = false;

                hit.IsSelected = true;
            }

            _isMovingSelection = page.Items.Any(i => i.IsSelected);
            _moveAccum = default;

            // 记录拖动前的状态，松开鼠标时用于生成撤销记录
            _moveStartStates.Clear();
            foreach (var item in page.Items.Where(i => i.IsSelected))
                _moveStartStates[item] = item.Clone();

            SelectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!additive)
        {
            foreach (var item in page.Items)
                item.IsSelected = false;

            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        _isMarquee = true;
        _marqueeStart = doc;
        _marqueeEnd = doc;
    }

    private void HandleSelectMove(WhiteboardPage page, Point doc, PointerEventArgs e)
    {
        // 诊断开关：设置 WB_DEBUG_DRAG=1 时输出每次移动的坐标换算，便于排查拖动异常
        var debug = Environment.GetEnvironmentVariable("WB_DEBUG_DRAG") == "1";

        if (debug)
        {
            Console.WriteLine($"[drag] doc=({doc.X:0.##},{doc.Y:0.##}) " +
                              $"last=({_lastDocPoint.X:0.##},{_lastDocPoint.Y:0.##}) " +
                              $"delta=({doc.X - _lastDocPoint.X:0.##},{doc.Y - _lastDocPoint.Y:0.##}) " +
                              $"marquee={_isMarquee} moving={_isMovingSelection} " +
                              $"zoom={Zoom:0.##} pan=({PanOffset.X:0.##},{PanOffset.Y:0.##})");
        }

        if (_isMarquee)
        {
            _marqueeEnd = doc;
            InvalidateVisual();
            return;
        }

        if (!_isMovingSelection)
            return;

        var delta = doc - _lastDocPoint;
        if (Math.Abs(delta.X) < 1e-9 && Math.Abs(delta.Y) < 1e-9)
            return;

        foreach (var item in page.Items)
        {
            if (item.IsSelected)
                item.Translate(delta);
        }

        _moveAccum += delta;
        InvalidateVisual();
    }

    private void FinishSelect()
    {
        var page = Page;
        if (page is null)
            return;

        if (_isMarquee)
        {
            _isMarquee = false;
            var rect = new Rect(_marqueeStart, _marqueeEnd);

            if (rect.Width > 2 || rect.Height > 2)
            {
                foreach (var item in page.Items)
                {
                    if (rect.Intersects(item.Bounds))
                        item.IsSelected = true;
                }
            }

            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        _isMovingSelection = false;

        if (_moveAccum.Length > 0.5 && _moveStartStates.Count > 0)
        {
            var entries = _moveStartStates
                .Select(kv => (Item: kv.Key, Before: kv.Value, After: kv.Key.Clone()))
                .ToList();

            SelectionMoved?.Invoke(this, new SelectionMovedEventArgs(entries));
        }

        _moveStartStates.Clear();
        _moveAccum = default;
    }

    // ─────────────────────────────────────────────────────────────
    //  橡皮擦
    // ─────────────────────────────────────────────────────────────

    private void EraseAt(Point doc)
    {
        // 每移动几个像素才做一次擦除计算，避免高频重复
        var tick = Environment.TickCount64;
        if (tick - _erasedTick < 8)
            return;

        _erasedTick = tick;

        var page = Page;
        if (page is null)
            return;

        var tolerance = EraserRadius;
        var removed = new List<WhiteboardItem>();
        var added = new List<WhiteboardItem>();

        for (var i = page.Items.Count - 1; i >= 0; i--)
        {
            var item = page.Items[i];
            if (!item.HitTest(doc, tolerance))
                continue;

            if (EraserMode == EraserMode.Stroke || item is not StrokeItem stroke)
            {
                removed.Add(item);
                continue;
            }

            // 像素橡皮：按命中位置把笔画切成前后两段
            var cut = FindCutIndex(stroke, doc, tolerance);
            if (cut < 0)
                continue;

            removed.Add(item);

            var before = cut >= 0 ? stroke.Points.Take(cut).ToList() : new List<StrokePoint>();
            var after = stroke.Points.Skip(cut + 1).ToList();

            added.AddRange(MakeFragment(stroke, before));
            added.AddRange(MakeFragment(stroke, after));
        }

        if (removed.Count == 0 && added.Count == 0)
            return;

        ItemsErased?.Invoke(this, new EraseCompletedEventArgs(removed, added));
        InvalidateVisual();
    }

    private static int FindCutIndex(StrokeItem stroke, Point p, double tolerance)
    {
        var reach = stroke.Thickness * 0.5 + tolerance;
        var reachSq = reach * reach;

        var best = -1;
        var bestDist = double.MaxValue;

        for (var i = 0; i < stroke.Points.Count; i++)
        {
            var d = StrokeItem.DistanceSquared(stroke.Points[i].ToPoint(), p);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }

        if (best < 0)
            return -1;

        // 只擦掉局部：如果最近的点也超出范围，说明没真正碰到
        return bestDist <= reachSq * 4 ? best : -1;
    }

    private static IEnumerable<WhiteboardItem> MakeFragment(StrokeItem source, List<StrokePoint> points)
    {
        if (points.Count == 0)
            yield break;

        if (points.Count == 1)
        {
            // 单点残段没有意义，丢掉
            yield break;
        }

        var fragment = (StrokeItem)source.Clone();
        fragment.Points.Clear();
        fragment.Points.AddRange(points);
        yield return fragment;
    }

    // ─────────────────────────────────────────────────────────────
    //  激光笔
    // ─────────────────────────────────────────────────────────────

    private void OnLaserTick(object? sender, EventArgs e)
    {
        if (_laser.IsEmpty || !ShowLaser)
        {
            if (_laser.IsEmpty)
                _laserTimer.Stop();
        }

        _laser.Update();
        InvalidateVisual();
    }

    /// <summary>清空激光轨迹。</summary>
    public void ClearLaser()
    {
        _laser.Clear();
        _laserTimer.Stop();
        InvalidateVisual();
    }

    /// <summary>设置背景样式预览（用于选项栏中的即时预览）。</summary>
    public void SetBackgroundPreview(PageBackgroundStyle? style)
    {
        _hasPreviewStyle = style.HasValue;
        _previewBackgroundStyle = style ?? PageBackgroundStyle.Blank;
        InvalidateVisual();
    }

    // 激光轨迹数据
    private sealed class LaserTrail
    {
        private readonly List<StrokePoint> _points = new();

        public bool IsEmpty => _points.Count == 0;

        public void Clear() => _points.Clear();

        public void Add(Point p) => _points.Add(new StrokePoint(p.X, p.Y, 1.0, Now()));

        public void Update()
        {
            var cutoff = Now() - (LaserTailSeconds + LaserFadeSeconds) * 1000;
            _points.RemoveAll(p => p.T < cutoff);

            if (_points.Count > 3000)
                _points.RemoveRange(0, _points.Count - 3000);
        }

        public void Render(DrawingContext context, RenderCache cache)
        {
            if (_points.Count < 2)
            {
                if (_points.Count == 1)
                {
                    var age = (Now() - _points[0].T) / 1000.0;
                    var alpha = AlphaFor(age);
                    if (alpha <= 0)
                        return;

                    context.DrawEllipse(
                        cache.Brush(Color.FromArgb((byte)(alpha * 255), 0xFF, 0x2D, 0x55)),
                        null, _points[0].ToPoint(), 5, 5);
                }

                return;
            }

            var now = Now();

            for (var i = 1; i < _points.Count; i++)
            {
                var age = (now - _points[i].T) / 1000.0;
                var alpha = AlphaFor(age);
                if (alpha <= 0.01)
                    continue;

                var t = Math.Clamp(1.0 - age / (LaserTailSeconds + LaserFadeSeconds), 0, 1);
                var width = 1.5 + 5.5 * t;

                var color = Color.FromArgb((byte)(alpha * 235), 0xFF, 0x2D, 0x55);
                var pen = cache.Pen(color, width, PenLineCap.Round, PenLineJoin.Round);

                context.DrawLine(pen, _points[i - 1].ToPoint(), _points[i].ToPoint());
            }
        }

        private static double AlphaFor(double ageSeconds)
        {
            if (ageSeconds <= LaserTailSeconds)
                return 1.0;

            var t = (ageSeconds - LaserTailSeconds) / LaserFadeSeconds;
            return t >= 1 ? 0 : 1 - t;
        }
    }
}
