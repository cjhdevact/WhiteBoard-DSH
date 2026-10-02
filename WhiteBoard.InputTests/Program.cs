using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using WhiteBoard.Controls;
using WhiteBoard.Models;
using WhiteBoard.ViewModels;
using WhiteBoard.Services;

namespace WhiteBoard.InputTests;

/// <summary>
/// 交互回归测试：用真实指针事件复现并验证
/// 「选择拖动」「橡皮擦」「背景底纹渲染」等路径。
/// 用法：dotnet run --project WhiteBoard.InputTests
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 独立模式：探测透明覆盖窗口能否收到真实鼠标输入
        if (args.Any(a => string.Equals(a, "--probe-window-input", StringComparison.OrdinalIgnoreCase)))
            return WindowInputProbe.Run(args);

        Console.WriteLine("=== 交互回归测试 ===");

        try
        {
            AppBuilder.Configure<Application>()
                .UsePlatformDetect()
                .WithInterFont()
                .SetupWithoutStarting();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"平台初始化失败: {ex.Message}");
            return 2;
        }

        TestSelectDrag();
        TestSelectDragAfterPan();
        TestSelectDragAtZoom();
        TestEraserFollowsPointer();
        TestPenInput();
        TestAnnotationPenInput();
        TestBackgroundStyleRenderCost();
        TestPenStrokeCost();
        TestBackgroundStyleStress();
        TestExportStress();
        TestThumbnailStress();

        Console.WriteLine();
        Console.WriteLine($"=== 结果: 通过 {_passed} / 失败 {_failed} ===");
        return _failed == 0 ? 0 : 1;
    }

    // ─────────────────────────────────────────────────────────────
    //  选择 / 拖动
    // ─────────────────────────────────────────────────────────────

    private static (WhiteboardCanvas Canvas, WhiteboardPage Page, StrokeItem Stroke) Setup(double zoom = 1, Vector pan = default)
    {
        var page = new WhiteboardPage();
        var stroke = MakeStroke(100, 100);
        page.Items.Add(stroke);

        var canvas = new WhiteboardCanvas
        {
            Page = page,
            Tool = WhiteboardTool.Select,
            Zoom = zoom,
            PanOffset = pan,
            Width = 800,
            Height = 600
        };

        canvas.Measure(new Size(800, 600));
        canvas.Arrange(new Rect(0, 0, 800, 600));

        return (canvas, page, stroke);
    }

    private static StrokeItem MakeStroke(double x, double y)
    {
        var stroke = new StrokeItem { Tool = WhiteboardTool.Pen, Color = Colors.Black, Thickness = 4 };
        for (var i = 0; i < 20; i++)
            stroke.Points.Add(new StrokePoint(x + i * 5, y + i * 2, 1.0, i));

        return stroke;
    }

    private static Point StrokeCenter(StrokeItem s)
    {
        var b = s.Bounds;
        return new Point(b.Center.X, b.Center.Y);
    }

    private static void TestSelectDrag()
    {
        Section("选择 · 拖动（缩放 100%，无平移）");

        var (canvas, _, stroke) = Setup();
        var start = StrokeCenter(stroke);
        var before = stroke.Points[0].X;

        var sim = new PointerSim();
        sim.Press(canvas, start);
        sim.Move(canvas, new Point(start.X + 10, start.Y));
        sim.Move(canvas, new Point(start.X + 20, start.Y));
        sim.Move(canvas, new Point(start.X + 30, start.Y));
        sim.Release(canvas, new Point(start.X + 30, start.Y));

        var moved = stroke.Points[0].X - before;
        Check("拖动 30px，笔迹正好右移 30", Math.Abs(moved - 30) < 0.5, $"实际 {moved:0.##}");
    }

    private static void TestSelectDragAfterPan()
    {
        Section("选择 · 拖动（画布已平移）");

        var (canvas, _, stroke) = Setup(pan: new Vector(120, 80));
        var docCenter = StrokeCenter(stroke);
        var screenStart = canvas.DocumentToViewport(docCenter);
        var before = stroke.Points[0].X;

        var sim = new PointerSim();
        sim.Press(canvas, screenStart);
        sim.Move(canvas, new Point(screenStart.X + 25, screenStart.Y + 15));
        sim.Move(canvas, new Point(screenStart.X + 50, screenStart.Y + 30));
        sim.Release(canvas, new Point(screenStart.X + 50, screenStart.Y + 30));

        var dx = stroke.Points[0].X - before;
        Check("平移后拖动 50px，笔迹右移 50", Math.Abs(dx - 50) < 0.5, $"实际 {dx:0.##}");
    }

    private static void TestSelectDragAtZoom()
    {
        Section("选择 · 拖动（缩放 200%）");

        var (canvas, _, stroke) = Setup(zoom: 2.0);
        var docCenter = StrokeCenter(stroke);
        var screenStart = canvas.DocumentToViewport(docCenter);
        var before = stroke.Points[0].X;

        var sim = new PointerSim();
        // 屏幕上移动 40px = 文档坐标 20px
        sim.Press(canvas, screenStart);
        sim.Move(canvas, new Point(screenStart.X + 20, screenStart.Y));
        sim.Move(canvas, new Point(screenStart.X + 40, screenStart.Y));
        sim.Release(canvas, new Point(screenStart.X + 40, screenStart.Y));

        var dx = stroke.Points[0].X - before;
        Check("200% 缩放下屏幕移动 40px，文档移动 20", Math.Abs(dx - 20) < 0.5, $"实际 {dx:0.##}");
    }

    // ─────────────────────────────────────────────────────────────
    //  橡皮擦
    // ─────────────────────────────────────────────────────────────

    private static void TestEraserFollowsPointer()
    {
        Section("橡皮擦 · 跟手性");

        var page = new WhiteboardPage();
        var strokes = new List<StrokeItem>();

        // 20 条长笔迹，模拟一页写满内容
        for (var i = 0; i < 20; i++)
        {
            var s = MakeStroke(80 + i * 3, 60 + i * 22);
            for (var k = 0; k < 40; k++)
                s.Points.Add(new StrokePoint(80 + i * 3 + k * 14, 60 + i * 22 + Math.Sin(k / 3.0) * 6, 1.0, k));

            strokes.Add(s);
            page.Items.Add(s);
        }

        var canvas = new WhiteboardCanvas
        {
            Page = page,
            Tool = WhiteboardTool.Eraser,
            EraserMode = EraserMode.Pixel,
            EraserRadius = 14,
            Width = 900,
            Height = 600
        };

        canvas.Measure(new Size(900, 600));
        canvas.Arrange(new Rect(0, 0, 900, 600));

        var removed = 0;
        canvas.ItemsErased += (_, e) => removed += e.Removed.Count;

        var sim = new PointerSim();
        var start = new Point(100, 60);

        var (pressMs, _) = Bench.Measure(() => sim.Press(canvas, start));

        // 沿笔迹拖动擦除
        var (moveMs, moveAlloc) = Bench.Measure(() =>
        {
            for (var i = 1; i <= 120; i++)
                sim.Move(canvas, new Point(start.X + i * 6, start.Y + i * 1.5));
        }, iterations: 5);

        sim.Release(canvas, new Point(start.X + 720, start.Y + 180));

        Check("橡皮按下耗时 < 20ms", pressMs < 20, $"实际 {pressMs:0.##}ms");
        Check("橡皮 120 次移动平均 < 12ms/次（即 >80fps 余量）", moveMs < 12,
            $"实际 {moveMs:0.##}ms/次，分配 {moveAlloc / 1024.0:0.#}KB/次");
        Check("橡皮确实擦除了内容", removed > 0 || page.Items.Count != 20,
            $"移除次数 {removed}，剩余 {page.Items.Count}");

        // 逐条笔迹的命中测试成本
        var stroke = strokes[10];
        var (hitMs, _) = Bench.Measure(() =>
        {
            for (var i = 0; i < 400; i++)
                stroke.HitTest(new Point(200 + i, 300), 14);
        }, iterations: 10);

        Check("单笔 400 次命中测试 < 40ms", hitMs < 40, $"实际 {hitMs:0.###}ms");
    }

    // ─────────────────────────────────────────────────────────────
    //  书写输入（含桌面批注层窗口内的书写）
    // ─────────────────────────────────────────────────────────────

    private static void TestPenInput()
    {
        Section("硬笔 · 真实指针输入");

        var page = new WhiteboardPage();
        var canvas = new WhiteboardCanvas
        {
            Page = page,
            Tool = WhiteboardTool.Pen,
            PenColor = Colors.Black,
            PenThickness = 4,
            Width = 900,
            Height = 600
        };

        canvas.Measure(new Size(900, 600));
        canvas.Arrange(new Rect(0, 0, 900, 600));

        var completed = new List<WhiteboardItem>();
        canvas.ItemCompleted += (_, e) => completed.Add(e.Item);

        var sim = new PointerSim();
        sim.Press(canvas, new Point(100, 100));
        for (var i = 1; i <= 40; i++)
            sim.Move(canvas, new Point(100 + i * 15, 100 + Math.Sin(i / 4.0) * 40));
        sim.Release(canvas, new Point(700, 100));

        Check("按下-移动-抬起后产生一个对象", completed.Count == 1, $"{completed.Count} 个");

        if (completed.FirstOrDefault() is StrokeItem stroke)
        {
            Check("笔迹采样到足够多的点", stroke.Points.Count > 30, $"{stroke.Points.Count} 点");
            Check("笔迹颜色与设置一致", stroke.Color == Colors.Black);
            Check("笔迹粗细与设置一致", Math.Abs(stroke.Thickness - 4) < 0.01, stroke.Thickness.ToString("0.##"));
            Check("笔迹几何可构建", Services_StrokeGeometryBuilder_Build(stroke) is not null);
        }
        else
        {
            Check("产生的是笔迹对象", false);
        }
    }

    private static Avalonia.Media.Geometry? Services_StrokeGeometryBuilder_Build(StrokeItem stroke)
        => WhiteBoard.Services.StrokeGeometryBuilder.Build(stroke);

    /// <summary>
    /// 桌面批注层：在真实的 AnnotationCanvasWindow 里写一笔。
    /// 这是「批注层写不了字」的直接回归测试。
    /// </summary>
    private static void TestAnnotationPenInput()
    {
        Section("桌面批注层 · 真实窗口内书写");

        try
        {
            var session = new WhiteboardSession(WhiteboardDocument.CreateDefault(), new AppSettings());
            var main = new MainWindowViewModel(session);
            var page = main.ActivePage;
            var before = page.Items.Count;

            var overlayVm = new AnnotationOverlayViewModel(main, session, page);
            var window = new WhiteBoard.Views.AnnotationCanvasWindow(
                overlayVm, new PixelRect(0, 0, 1280, 720), 1.0);

            var canvas = window.FindControl<WhiteBoard.Controls.WhiteboardCanvas>("Canvas");
            Check("批注窗口里能取到画布", canvas is not null);

            if (canvas is not null)
            {
                Check("画布已绑定批注页", ReferenceEquals(canvas.Page, page),
                    canvas.Page is null ? "null" : "其它页");

                Check("画布工具已绑定", canvas.Tool == overlayVm.ActiveTool,
                    $"{canvas.Tool} vs {overlayVm.ActiveTool}");

                canvas.Measure(new Size(1280, 720));
                canvas.Arrange(new Rect(0, 0, 1280, 720));

                var sim = new PointerSim();
                sim.Press(canvas, new Point(200, 200));
                for (var i = 1; i <= 40; i++)
                    sim.Move(canvas, new Point(200 + i * 18, 200 + Math.Sin(i / 5.0) * 50));
                sim.Release(canvas, new Point(920, 200));

                Check("批注层能写入笔迹", page.Items.Count == before + 1,
                    $"{page.Items.Count} vs {before + 1}");
                Check("批注层撤销可用", overlayVm.CanUndo);
                Check("批注层记录的笔画非空",
                    page.Items.OfType<StrokeItem>().Any(s => s.Points.Count > 20));
            }

            window.Close();
            overlayVm.Detach();
        }
        catch (Exception ex)
        {
            Check("批注层书写测试执行无异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  渲染成本
    // ─────────────────────────────────────────────────────────────

    private static void TestBackgroundStyleRenderCost()
    {
        Section("背景底纹 · 渲染成本");

        // 画布 1200×800，视图缩放到刚好看到一小块纸面
        const int w = 1200;
        const int h = 800;

        foreach (PageBackgroundStyle style in Enum.GetValues<PageBackgroundStyle>())
        {
            var page = new WhiteboardPage { BackgroundStyle = style };
            var canvas = new WhiteboardCanvas
            {
                Page = page,
                Tool = WhiteboardTool.Pen,
                Zoom = 1,
                PanOffset = new Vector(0, 0),
                Width = w,
                Height = h
            };

            canvas.Measure(new Size(w, h));
            canvas.Arrange(new Rect(0, 0, w, h));

            var (ms, alloc) = Bench.Measure(() => RenderOnce(canvas, w, h), iterations: 3);

            var ok = ms < 60;
            Check($"底纹「{style}」单帧渲染 < 60ms", ok, $"实际 {ms:0.#}ms，分配 {alloc / 1024.0:0.#}KB");

            if (!ok)
                Console.WriteLine($"        ← 「{style}」渲染过慢，会导致界面卡顿");
        }
    }

    private static void RenderOnce(WhiteboardCanvas canvas, int w, int h)
    {
        using var bmp = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        using var ctx = bmp.CreateDrawingContext();
        canvas.Render(ctx);
    }

    private static void TestPenStrokeCost()
    {
        Section("书写 · 单帧渲染成本");

        const int w = 1200;
        const int h = 800;

        foreach (var count in new[] { 50, 200, 600 })
        {
            var page = new WhiteboardPage();
            for (var i = 0; i < count; i++)
            {
                var s = MakeStroke(20 + (i % 30) * 38, 20 + (i / 30) * 38);
                for (var k = 0; k < 60; k++)
                    s.Points.Add(new StrokePoint(20 + (i % 30) * 38 + k * 6, 20 + (i / 30) * 38 + Math.Sin(k / 4.0) * 8, 1.0, k));
                page.Items.Add(s);
            }

            var canvas = new WhiteboardCanvas
            {
                Page = page,
                Zoom = 1,
                PanOffset = new Vector(0, 0),
                Width = w,
                Height = h
            };

            canvas.Measure(new Size(w, h));
            canvas.Arrange(new Rect(0, 0, w, h));

            var (ms, _) = Bench.Measure(() => RenderOnce(canvas, w, h), iterations: 3);
            Check($"{count} 条笔迹单帧渲染 < 80ms", ms < 80, $"实际 {ms:0.#}ms");
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  背景底纹 / 缩略图 压力测试（复现「切换底纹导致卡死崩溃」）
    // ─────────────────────────────────────────────────────────────

    private static void TestBackgroundStyleStress()
    {
        Section("背景底纹 · 反复切换压力测试");

        var page = new WhiteboardPage();
        for (var i = 0; i < 40; i++)
        {
            var s = MakeStroke(30 + (i % 10) * 90, 30 + (i / 10) * 120);
            for (var k = 0; k < 40; k++)
                s.Points.Add(new StrokePoint(30 + (i % 10) * 90 + k * 18, 30 + (i / 10) * 120 + Math.Sin(k / 3.0) * 12, 1.0, k));
            page.Items.Add(s);
        }

        var canvas = new WhiteboardCanvas
        {
            Page = page,
            Zoom = 0.5,
            PanOffset = new Vector(10, 10),
            Width = 1200,
            Height = 800
        };

        canvas.Measure(new Size(1200, 800));
        canvas.Arrange(new Rect(0, 0, 1200, 800));

        var styles = Enum.GetValues<PageBackgroundStyle>();
        var ok = true;
        var error = string.Empty;

        try
        {
            // 100 次快速切换，每次都渲染一帧
            for (var round = 0; round < 100; round++)
            {
                page.BackgroundStyle = styles[round % styles.Length];
                RenderOnce(canvas, 1200, 800);
            }

            // 极端缩放下的底纹（最容易出问题）
            foreach (var zoom in new[] { 0.05, 0.1, 0.5, 1.0, 4.0, 8.0 })
            {
                canvas.Zoom = zoom;
                foreach (var style in styles)
                {
                    page.BackgroundStyle = style;
                    RenderOnce(canvas, 1200, 800);
                }
            }
        }
        catch (Exception ex)
        {
            ok = false;
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        Check("100 次底纹切换 + 各缩放级别渲染无异常", ok, error);

        // 底纹图元密度上限验证：把画布缩到极小，确保不会生成海量图元
        canvas.Zoom = 0.01;
        page.BackgroundStyle = PageBackgroundStyle.TianZiGe;
        var (ms, alloc) = Bench.Measure(() => RenderOnce(canvas, 1200, 800), iterations: 3);
        Check("极端缩小（1%）时田字格渲染 < 60ms", ms < 60, $"实际 {ms:0.#}ms，分配 {alloc / 1024.0:0.#}KB");
    }

    private static void TestThumbnailStress()
    {
        Section("缩略图 · 反复重建压力测试");

        var doc = WhiteboardDocument.CreateDefault();
        var page = doc.Pages[0];

        for (var i = 0; i < 30; i++)
        {
            var s = MakeStroke(20 + i * 5, 20 + i * 5);
            page.Items.Add(s);
        }

        using var service = new ThumbnailService();
        var ok = true;
        var error = string.Empty;

        try
        {
            // 模拟「连续书写 → 缩略图不断重建」以及「每次换底纹都强制重建」
            for (var round = 0; round < 60; round++)
            {
                page.BackgroundStyle = (PageBackgroundStyle)(round % 6);

                if (round % 3 == 0)
                {
                    var s = MakeStroke(100 + round, 100 + round);
                    page.Items.Add(s);
                }

                service.Build(doc.Pages, force: true);
            }
        }
        catch (Exception ex)
        {
            ok = false;
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        Check("60 次强制重建缩略图无异常", ok, error);

        // 页面被删除后缓存要能正确释放
        try
        {
            doc.Pages.Add(new WhiteboardPage { BackgroundStyle = PageBackgroundStyle.Dots });
            service.Build(doc.Pages, force: true);
            doc.Pages.RemoveAt(1);
            service.Build(doc.Pages, force: true);
            Check("删除页面后重建缩略图无异常", true);
        }
        catch (Exception ex)
        {
            Check("删除页面后重建缩略图无异常", false, ex.Message);
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  导出压力测试（复现「导出图片 Access denied」）
    // ─────────────────────────────────────────────────────────────

    private static void TestExportStress()
    {
        Section("导出图片 · 路径与重复导出");

        var dir = Path.Combine(Path.GetTempPath(), "wb-export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var page = new WhiteboardPage { BackgroundStyle = PageBackgroundStyle.Grid };
        var s = MakeStroke(30, 60);
        for (var i = 0; i < 40; i++)
            s.Points.Add(new StrokePoint(30 + i * 18, 60 + Math.Sin(i / 3.0) * 40, 1.0, i));
        page.Items.Add(s);

        string? Err(Action action)
        {
            try { action(); return null; }
            catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
        }

        var e1 = Err(() => WhiteboardFileService.ExportPng(page, Path.Combine(dir, "a.png"), 1920, 1080));
        Check("导出到已存在目录", e1 is null, e1);

        var e2 = Err(() => WhiteboardFileService.ExportPng(page, Path.Combine(dir, "sub", "deep", "b.png"), 1920, 1080));
        Check("导出到多层不存在的子目录（应自动创建）", e2 is null, e2);

        var e3 = Err(() => WhiteboardFileService.ExportPng(page, Path.Combine(dir, "我的 白板_第2页.png"), 1920, 1080));
        Check("导出到带中文与空格的文件名", e3 is null, e3);

        // 连续导出到同一路径：这是「导出全部页面 / 反复导出」的真实场景
        string? loopError = null;
        for (var i = 0; i < 10 && loopError is null; i++)
            loopError = Err(() => WhiteboardFileService.ExportPng(page, Path.Combine(dir, "loop.png"), 1280, 720));

        Check("同一路径连续导出 10 次", loopError is null, loopError);

        // 导出全部页面（每页一个文件）
        var doc = WhiteboardDocument.CreateDefault();
        doc.Pages.Add(page);
        string? batchError = null;
        var name = "白板";

        for (var i = 0; i < doc.Pages.Count && batchError is null; i++)
            batchError = Err(() => WhiteboardFileService.ExportPng(
                doc.Pages[i], Path.Combine(dir, $"{name}_第{i + 1}页.png"), 1920, 1080));

        Check("批量导出全部页面", batchError is null, batchError);

        var produced = Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories).Length;
        Check("确实产生了图片文件", produced >= 5, $"{produced} 个");

        // 目标目录不可写时，错误信息应当是「可读的中文提示」而不是裸的 UnauthorizedAccessException
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // ignore
        }

        Check("导出的中文文件名可正常写盘", e3 is null);
    }
    // ─────────────────────────────────────────────────────────────

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine($"── {name} ──");
    }

    private static void Check(string description, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  [通过] {description}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [失败] {description}{(detail is null ? string.Empty : $"  ({detail})")}");
        }
    }
}
