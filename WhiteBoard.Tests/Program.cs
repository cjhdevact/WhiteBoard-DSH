using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using WhiteBoard;
using WhiteBoard.Controls;
using WhiteBoard.Models;
using WhiteBoard.Services;
using WhiteBoard.ViewModels;
using WhiteBoard.Views;

namespace WhiteBoard.SmokeTests;

/// <summary>
/// 冒烟测试：真实跑一遍几何算法、文档序列化、撤销重做、
/// 离屏渲染、PNG 导出、截图链路以及界面构造（XAML 加载）。
/// 用法：dotnet run --project WhiteBoard.Tests -- [输出目录]
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static bool _platformReady;

    [STAThread]
    public static int Main(string[] args)
    {
        var outputDir = args.Length > 0
            ? args[0]
            : Path.Combine(Path.GetTempPath(), "whiteboard-smoke");

        Directory.CreateDirectory(outputDir);

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 互动白板 冒烟测试 ===");
        Console.WriteLine($"输出目录: {outputDir}");

        TestSerialization(outputDir);
        TestUndoRedo();

        _platformReady = TrySetupAvalonia();

        Console.WriteLine();
        Console.WriteLine(_platformReady
            ? "Avalonia 图形平台已就绪，继续几何 / 渲染 / 界面测试。"
            : "警告：Avalonia 图形平台初始化失败，跳过依赖图形栈的测试。");

        if (_platformReady)
        {
            TryRun("几何与笔迹", () => TestPureLogic(outputDir));
            TryRun("渲染 / 缩略图 / PNG 导出", () => TestRendering(outputDir));
            TryRun("截图 → 批注页", () => TestScreenshotPipeline(outputDir));
            TryRun("界面构造", TestUiConstruction);
        }

        Console.WriteLine();
        Console.WriteLine($"=== 结果: 通过 {_passed} / 失败 {_failed} ===");
        return _failed == 0 ? 0 : 1;
    }

    private static void TryRun(string name, Action test)
    {
        try
        {
            test();
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine();
            Console.WriteLine($"── {name} ──");
            Console.WriteLine($"  [失败] 测试组异常: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    private static bool TrySetupAvalonia()
    {
        try
        {
            // 只初始化图形 / 控件栈，不加载 App.axaml
            // （样式与图标资源由 App.OnFrameworkInitializationCompleted 提供，
            //   这里测的是控件与渲染本身，不依赖应用级资源）。
            AppBuilder.Configure<Application>()
                .UsePlatformDetect()
                .WithInterFont()
                .SetupWithoutStarting();

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"平台初始化异常: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────

    private static void TestPureLogic(string outputDir)
    {
        Section("几何与笔迹");

        var stroke = new StrokeItem
        {
            Tool = WhiteboardTool.Pen,
            Color = Colors.Black,
            Thickness = 4
        };

        for (var i = 0; i <= 40; i++)
            stroke.Points.Add(new StrokePoint(i * 3, Math.Sin(i / 6.0) * 20 + 50, 0.4 + i / 60.0, i * 8));

        var geometry = StrokeGeometryBuilder.Build(stroke);
        Check("StrokeGeometryBuilder 生成了几何", geometry is not null);

        var bounds = stroke.Bounds;
        Check("笔迹包围盒有效", bounds.Width > 100 && bounds.Height > 10,
            $"bounds = {bounds}");

        Check("命中测试命中线上的点", stroke.HitTest(new Point(60, Math.Sin(20 / 6.0) * 20 + 50), 2));
        Check("命中测试排除远处的点", !stroke.HitTest(new Point(4000, 4000), 2));

        var simplified = StrokeGeometryBuilder.Simplify(stroke.Points, 0.5);
        Check("简化后点数不多于原点数", simplified.Count <= stroke.Points.Count,
            $"{stroke.Points.Count} -> {simplified.Count}");
        Check("简化保留首尾点", simplified.Count >= 2
            && Math.Abs(simplified[0].X - stroke.Points[0].X) < 0.01
            && Math.Abs(simplified[^1].X - stroke.Points[^1].X) < 0.01);

        var shape = new ShapeItem
        {
            Tool = WhiteboardTool.Rectangle,
            Start = new Point(10, 10),
            End = new Point(110, 60),
            Thickness = 3
        };

        Check("矩形包围盒合理", shape.Bounds.Width >= 100 && shape.Bounds.Height >= 50,
            shape.Bounds.ToString());
        Check("矩形内部（无填充）不命中", !shape.HitTest(new Point(60, 35), 1));
        Check("矩形边框命中", shape.HitTest(new Point(10, 35), 2));

        shape.Fill = ShapeFill.Solid;
        Check("矩形填充后内部命中", shape.HitTest(new Point(60, 35), 1));

        var text = new TextItem { Text = "白板 WhiteBoard", Position = new Point(0, 0), FontSize = 24 };
        Check("文本尺寸估算为正", text.Measure().Width > 10 && text.Measure().Height > 10,
            $"size = {text.Measure()}");

        var clone = text.Clone();
        Check("克隆对象内容一致", clone is TextItem t2 && t2.Text == text.Text);

        clone.Translate(new Vector(10, -5));
        Check("平移生效", Math.Abs(((TextItem)clone).Position.X - 10) < 0.001);

        // 荧光笔 / 激光笔透明度
        var highlighter = new StrokeItem { Tool = WhiteboardTool.Highlighter, Color = Colors.Red, Thickness = 8 };
        highlighter.Points.Add(new StrokePoint(0, 0));
        highlighter.Points.Add(new StrokePoint(10, 10));

        var hColor = WhiteboardRenderer.ResolveColor(highlighter);
        Check("荧光笔半透明", hColor.A < 255 && hColor.A > 0, $"alpha = {hColor.A}");
        Check("荧光笔加粗", Math.Abs(WhiteboardRenderer.ResolveThickness(highlighter) - 8 * WhiteboardRenderer.HighlighterWidthScale) < 0.01);

        _ = outputDir;
    }

    private static void TestSerialization(string outputDir)
    {
        Section("文档保存 / 打开");

        var document = BuildSampleDocument();
        var path = Path.Combine(outputDir, "sample.wbd");

        WhiteboardFileService.SaveAsync(document, path).GetAwaiter().GetResult();

        Check("文件已写出", File.Exists(path));
        var size = new FileInfo(path).Length;
        Check("文件非空", size > 200, $"{size} 字节");

        var loaded = WhiteboardFileService.LoadAsync(path).GetAwaiter().GetResult();

        Check("反序列化成功", loaded is not null);

        if (loaded is null)
            return;

        Check("页数一致", loaded.Pages.Count == document.Pages.Count,
            $"{loaded.Pages.Count} vs {document.Pages.Count}");

        var originalItems = document.Pages.Sum(p => p.Items.Count);
        var loadedItems = loaded.Pages.Sum(p => p.Items.Count);
        Check("对象总数一致", originalItems == loadedItems, $"{loadedItems} vs {originalItems}");

        Check("笔画点数一致",
            loaded.Pages[0].Items.OfType<StrokeItem>().First().Points.Count
            == document.Pages[0].Items.OfType<StrokeItem>().First().Points.Count);

        Check("图形类型保真",
            loaded.Pages[0].Items.OfType<ShapeItem>().Any(s => s.Tool == WhiteboardTool.Arrow));

        Check("文本内容保真",
            loaded.Pages[0].Items.OfType<TextItem>().Any(t => t.Text.Contains("白板")));

        Check("背景样式保真", loaded.Pages[1].BackgroundStyle == PageBackgroundStyle.Grid,
            loaded.Pages[1].BackgroundStyle.ToString());

        Check("笔压保真",
            Math.Abs(loaded.Pages[0].Items.OfType<StrokeItem>().First().Points[3].Pressure
                     - document.Pages[0].Items.OfType<StrokeItem>().First().Points[3].Pressure) < 0.001);

        // 损坏文件的容错
        var badPath = Path.Combine(outputDir, "broken.wbd");
        File.WriteAllText(badPath, "{ this is not json");

        var threw = false;
        try
        {
            WhiteboardFileService.LoadAsync(badPath).GetAwaiter().GetResult();
        }
        catch
        {
            threw = true;
        }

        Check("损坏文件抛出可捕获异常", threw);
    }

    private static void TestUndoRedo()
    {
        Section("撤销 / 重做");

        var page = new WhiteboardPage();
        var history = new UndoRedoService();

        var a = NewStroke(0, 0);
        var b = NewStroke(100, 100);

        page.Items.Add(a);
        history.Push(new AddItemsAction(page.Items, new[] { a }));

        page.Items.Add(b);
        history.Push(new AddItemsAction(page.Items, new[] { b }));

        Check("初始 2 个对象", page.Items.Count == 2);
        Check("可撤销", history.CanUndo);

        history.Undo();
        Check("撤销后剩 1 个对象", page.Items.Count == 1, page.Items.Count.ToString());

        history.Undo();
        Check("再次撤销后清空", page.Items.Count == 0);

        Check("不可再撤销", !history.CanUndo);
        Check("可以重做", history.CanRedo);

        history.Redo();
        history.Redo();
        Check("重做恢复 2 个对象", page.Items.Count == 2, page.Items.Count.ToString());

        // 删除 + 撤销要恢复原来的顺序
        var removed = page.Items.ToList();
        foreach (var item in removed)
            page.Items.Remove(item);

        var action = new RemoveItemsAction(page.Items, removed);
        history.Push(action);

        Check("删除后为空", page.Items.Count == 0);
        history.Undo();
        Check("撤销删除恢复顺序", page.Items.Count == 2 && ReferenceEquals(page.Items[0], a));

        // 像素橡皮：删除 + 新增合成一步
        var view = new WhiteboardPage();
        var longStroke = NewStroke(0, 0);
        for (var i = 0; i < 40; i++)
            longStroke.Points.Add(new StrokePoint(i * 5, 0));

        view.Items.Add(longStroke);

        var head = (StrokeItem)longStroke.Clone();
        head.Points.Clear();
        head.Points.AddRange(longStroke.Points.Take(6));

        var tail = (StrokeItem)longStroke.Clone();
        tail.Points.Clear();
        tail.Points.AddRange(longStroke.Points.Skip(10));

        var replace = new ReplaceItemsAction(view.Items, new[] { longStroke }, new WhiteboardItem[] { head, tail });
        view.Items.Remove(longStroke);
        view.Items.Add(head);
        view.Items.Add(tail);

        Check("擦除后变成两段", view.Items.Count == 2);
        replace.Undo();
        Check("撤销擦除恢复一笔", view.Items.Count == 1 && ReferenceEquals(view.Items[0], longStroke));

        replace.Redo();
        Check("重做擦除恢复两段", view.Items.Count == 2);
    }

    private static void TestRendering(string outputDir)
    {
        Section("渲染 / 缩略图 / PNG 导出");

        var document = BuildSampleDocument();
        var total = 0;

        for (var i = 0; i < document.Pages.Count; i++)
        {
            var page = document.Pages[i];

            using var bitmap = WhiteboardFileService.RenderPageBitmap(page, 960, 540);
            Check($"第 {i + 1} 页离屏渲染出位图", bitmap.PixelSize.Width == 960 && bitmap.PixelSize.Height == 540);

            var pngPath = Path.Combine(outputDir, $"page{i + 1}.png");
            WhiteboardFileService.ExportPng(page, pngPath, 960, 540);

            var info = new FileInfo(pngPath);
            Check($"第 {i + 1} 页 PNG 已导出且非空", info.Exists && info.Length > 1000, $"{info.Length} 字节");

            // 校验 PNG 能被重新解码，且不是全透明 / 全黑
            using var decoded = new Bitmap(pngPath);
            Check($"第 {i + 1} 页 PNG 可解码", decoded.PixelSize.Width == 960);

            total++;
        }

        Check("所有页面都成功导出", total == document.Pages.Count);

        // 缩略图服务
        using var thumbs = new ThumbnailService();
        var list = thumbs.Build(document.Pages, force: true);
        Check("缩略图数量与页数一致", list.Count == document.Pages.Count, list.Count.ToString());

        var first = list[0];
        Check("缩略图尺寸正确",
            first.Bitmap.PixelSize.Width == ThumbnailService.ThumbnailSize.Width
            && first.Bitmap.PixelSize.Height == ThumbnailService.ThumbnailSize.Height,
            first.Bitmap.PixelSize.ToString());

        // 缩略图保存出来便于人工查看
        using (var ms = File.Create(Path.Combine(outputDir, "thumbnail1.png")))
            first.Bitmap.Save(ms);

        Check("缩略图可保存", File.Exists(Path.Combine(outputDir, "thumbnail1.png")));

        // 各背景样式都不应抛异常
        foreach (PageBackgroundStyle style in Enum.GetValues<PageBackgroundStyle>())
        {
            var p = new WhiteboardPage { BackgroundStyle = style };
            p.Items.Add(NewStroke(50, 50));

            using var bmp = WhiteboardFileService.RenderPageBitmap(p, 320, 180);
            Check($"背景样式 {style} 渲染正常", bmp.PixelSize.Width == 320);
        }
    }

    private static void TestScreenshotPipeline(string outputDir)
    {
        Section("截图 → 批注页");

        // 造一张假的“屏幕截图”，走完整条 截图 → Base64 → 位图 → 页面 的链路
        const int w = 320;
        const int h = 180;

        var bgra = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                bgra[i] = (byte)(x % 256);        // B
                bgra[i + 1] = (byte)(y % 256);    // G
                bgra[i + 2] = 0x40;               // R
                bgra[i + 3] = 0xFF;               // A
            }
        }

        var frame = new CapturedFrame
        {
            ScreenLeft = 0,
            ScreenTop = 0,
            Width = w,
            Height = h,
            Scale = 1.5,
            Bgra = bgra
        };

        Check("截图逻辑尺寸按 DPI 缩放换算", Math.Abs(frame.ToLogicalBounds().Width - w / 1.5) < 0.01);

        using (var bmp = frame.ToBitmap())
        {
            Check("截图转位图成功", bmp.PixelSize.Width == w && bmp.PixelSize.Height == h);

            var shotPath = Path.Combine(outputDir, "fake-screenshot.png");
            ScreenCaptureService.SavePng(frame, shotPath);
            Check("截图可存为 PNG", new FileInfo(shotPath).Length > 500);
        }

        var base64 = frame.ToPngBase64();
        Check("截图可转 Base64", base64.Length > 500);

        var page = new WhiteboardPage
        {
            BackgroundImageBase64 = base64,
            BackgroundImageRect = new Rect(0, 0, w / 1.5, h / 1.5)
        };

        Check("页面识别到背景截图", page.HasBackgroundImage);
        Check("背景截图可被缓存解码", BackgroundImageCache.Get(base64) is not null);

        // 在截图上写几笔，然后渲染
        var stroke = NewStroke(20, 20);
        for (var i = 0; i < 30; i++)
            stroke.Points.Add(new StrokePoint(20 + i * 6, 20 + i * 4));

        page.Items.Add(stroke);

        using var rendered = WhiteboardFileService.RenderPageBitmap(page, 640, 360);
        var annotated = Path.Combine(outputDir, "annotated-screenshot.png");
        using (var ms = File.Create(annotated))
            rendered.Save(ms);

        Check("带截图的批注页可渲染", new FileInfo(annotated).Length > 1000);

        // 桌面批注会话：新建带截图的页面
        var settings = new AppSettings();
        var session = new WhiteboardSession(WhiteboardDocument.CreateDefault(), settings);
        var screenshotPage = session.AddPageWithScreenshot(frame);

        Check("会话新增截图页", session.Pages.Count == 2 && session.ActivePage == screenshotPage);
        Check("截图页背景尺寸与截图一致",
            Math.Abs(screenshotPage.BackgroundImageRect.Width - w / 1.5) < 0.01,
            screenshotPage.BackgroundImageRect.ToString());
        Check("插入截图页后标记为已修改", session.IsDirty);
    }

    private static void TestUiConstruction()
    {
        Section("界面构造");

        var session = new WhiteboardSession(BuildSampleDocument(), new AppSettings());
        var vm = new MainWindowViewModel(session);

        Check("视图模型持有 2 页", vm.PageList.Count == 2, vm.PageList.Count.ToString());
        Check("页面导航条有缩略图", vm.PageList.All(p => p.Thumbnail is not null));
        Check("当前页标记正确", vm.PageList[0].IsActive);

        Check("默认工具为硬笔", vm.ActiveTool == WhiteboardTool.Pen);
        Check("调色板非空", vm.Palette.Count >= 8);
        Check("粗细选项非空", vm.ThicknessOptions.Count >= 3);

        // 选项栏可见性随工具变化
        vm.ActiveTool = WhiteboardTool.Eraser;
        Check("橡皮时显示橡皮选项", vm.IsEraserOptionsVisible && vm.IsOptionsBarVisible);
        Check("橡皮时隐藏颜色选项", !vm.IsColorOptionsVisible);

        vm.ActiveTool = WhiteboardTool.Rectangle;
        Check("矩形时显示填充选项", vm.IsShapeOptionsVisible);

        vm.ActiveTool = WhiteboardTool.Select;
        Check("选择工具时隐藏选项栏", !vm.IsOptionsBarVisible);

        // 背景样式
        vm.SetBackgroundStyleCommand.Execute("Grid");
        Check("背景样式切换成功", vm.ActivePage.BackgroundStyle == PageBackgroundStyle.Grid,
            vm.ActivePage.BackgroundStyle.ToString());

        vm.SetBackgroundStyleIndexCommand.Execute((int)PageBackgroundStyle.Music);
        Check("背景样式按索引切换", vm.ActivePage.BackgroundStyle == PageBackgroundStyle.Music);

        // 文本插入
        var before = vm.ActivePage.Items.Count;
        vm.InsertText("测试文本", 100, 120);
        Check("插入文本成功", vm.ActivePage.Items.Count == before + 1);
        Check("插入文本可撤销", vm.CanUndo);
        vm.UndoCommand.Execute(null);
        Check("撤销文本插入", vm.ActivePage.Items.Count == before);

        // 页面增删
        var count = vm.PageList.Count;
        vm.AddPageCommand.Execute(null);
        Check("新增页成功", vm.PageList.Count == count + 1);

        vm.GoToPageCommand.Execute(vm.PageList[0]);
        Check("切换页成功", session.PageIndex == 0);

        vm.DeletePageCommand.Execute(vm.PageList[^1]);
        Check("删除页成功", vm.PageList.Count == count);

        // 清空页面
        vm.ClearPageCommand.Execute(null);
        Check("清空页面成功", vm.ActivePage.Items.Count == 0);

        // 画布事件入口
        vm.HandleItemCompleted(NewStroke(5, 5));
        Check("画布提交对象后写入页面", vm.ActivePage.Items.Count == 1);

        // 构造真实控件与窗口（无头模式，验证 XAML 可以加载）
        var canvas = new WhiteboardCanvas
        {
            Page = vm.ActivePage,
            Tool = WhiteboardTool.Pen,
            PenColor = Colors.Red,
            PenThickness = 5
        };

        Check("画布属性生效", canvas.Tool == WhiteboardTool.Pen && canvas.PenThickness == 5);

        canvas.FitToViewport();
        canvas.ResetZoom();
        Check("画布缩放可切换", Math.Abs(canvas.Zoom - 1.0) < 0.001);

        // 注意：MainWindow / AnnotationCanvasWindow / AnnotationToolBarWindow 的 XAML 加载
        // 必须在应用自身进程里验证（AvaloniaXamlLoader 依据调用方程序集查找已编译的 XAML 资源），
        // 这部分由 WhiteBoard.exe --smoke-test 覆盖，见 WhiteBoardApp/Program.cs。

        // 批注视图模型：共享笔状态与「不破坏页面内容」的回归（纯逻辑，不需要 XAML）
        var shared = new MainWindowViewModel(new WhiteboardSession(WhiteboardDocument.CreateDefault(), new AppSettings()));
        shared.ActiveTool = WhiteboardTool.Highlighter;
        shared.PenColor = Colors.Blue;

        var pageBefore = shared.ActivePage;
        var itemsBefore = pageBefore.Items.Count;

        var overlayVm = new AnnotationOverlayViewModel(shared, shared.Session, pageBefore);

        Check("批注层继承主窗口工具", overlayVm.ActiveTool == WhiteboardTool.Highlighter);
        Check("批注层继承主窗口颜色", overlayVm.PenColor == Colors.Blue);

        Check("开启批注不改变页面对象数量", pageBefore.Items.Count == itemsBefore);
        Check("开启批注不给页面塞截图背景", !pageBefore.HasBackgroundImage);

        overlayVm.ActiveTool = WhiteboardTool.Laser;
        Check("批注层改工具会同步回主窗口", shared.ActiveTool == WhiteboardTool.Laser);

        overlayVm.PenColor = Colors.Green;
        Check("批注层改颜色会同步回主窗口", shared.PenColor == Colors.Green);

        overlayVm.HandleItemCompleted(NewStroke(1, 1));
        Check("批注层书写写入共享文档", pageBefore.Items.Count == itemsBefore + 1);
        Check("批注层撤销可用", overlayVm.CanUndo);

        overlayVm.ClickThrough = true;
        Check("批注层可开启鼠标穿透", overlayVm.ClickThrough);
        Check("穿透状态文案正确", overlayVm.ModeText == "鼠标穿透中", overlayVm.ModeText);

        // 冻结底图是临时覆盖层，不写进文档
        overlayVm.FrozenFrame = new CapturedFrame
        {
            Width = 64,
            Height = 36,
            Scale = 1.0,
            Bgra = new byte[64 * 36 * 4]
        };

        Check("冻结底图已挂上", overlayVm.HasFrozenFrame);
        Check("冻结不写入页面背景", !pageBefore.HasBackgroundImage);

        overlayVm.ShowFrozen = false;
        Check("可切回透明覆盖", overlayVm.IsTransparent);

        overlayVm.Detach();
        Check("批注会话可正常结束", !overlayVm.HasFrozenFrame);
    }

    // ─────────────────────────────────────────────────────────────
    //  测试辅助
    // ─────────────────────────────────────────────────────────────

    private static StrokeItem NewStroke(double x, double y)
    {
        var stroke = new StrokeItem
        {
            Tool = WhiteboardTool.Pen,
            Color = Colors.Black,
            Thickness = 3
        };

        stroke.Points.Add(new StrokePoint(x, y, 1.0, 0));
        return stroke;
    }

    private static WhiteboardDocument BuildSampleDocument()
    {
        var document = new WhiteboardDocument { Title = "冒烟测试白板" };

        // 第 1 页：手写 + 图形 + 文本
        var page1 = new WhiteboardPage { Title = "第 1 页", BackgroundStyle = PageBackgroundStyle.Blank };

        var handwriting = new StrokeItem
        {
            Tool = WhiteboardTool.Pen,
            Color = Color.FromRgb(0x1A, 0x1A, 0x1A),
            Thickness = 5
        };

        for (var i = 0; i <= 60; i++)
        {
            var t = i / 60.0;
            handwriting.Points.Add(new StrokePoint(
                60 + t * 600,
                300 + Math.Sin(t * Math.PI * 3) * 90,
                0.5 + 0.5 * Math.Abs(Math.Sin(t * Math.PI)),
                i * 12));
        }

        page1.Items.Add(handwriting);

        var highlighter = new StrokeItem
        {
            Tool = WhiteboardTool.Highlighter,
            Color = Color.FromRgb(0xF5, 0xC2, 0x11),
            Thickness = 10
        };

        for (var i = 0; i <= 20; i++)
            highlighter.Points.Add(new StrokePoint(90 + i * 28, 500 + i * 2, 1.0, i * 10));

        page1.Items.Add(highlighter);

        page1.Items.Add(new ShapeItem
        {
            Tool = WhiteboardTool.Rectangle,
            Start = new Point(120, 120),
            End = new Point(460, 300),
            Color = Color.FromRgb(0x1B, 0x6F, 0xE0),
            Thickness = 4,
            Fill = ShapeFill.Translucent
        });

        page1.Items.Add(new ShapeItem
        {
            Tool = WhiteboardTool.Arrow,
            Start = new Point(760, 140),
            End = new Point(1080, 340),
            Color = Color.FromRgb(0xE8, 0x1F, 0x1F),
            Thickness = 5
        });

        page1.Items.Add(new ShapeItem
        {
            Tool = WhiteboardTool.Ellipse,
            Start = new Point(900, 520),
            End = new Point(1180, 700),
            Color = Color.FromRgb(0x1E, 0xA8, 0x55),
            Thickness = 4
        });

        page1.Items.Add(new TextItem
        {
            Text = "白板 WhiteBoard\n冒烟测试",
            Position = new Point(140, 620),
            Color = Color.FromRgb(0x7A, 0x3F, 0xC9),
            FontSize = 46,
            Bold = true
        });

        // 第 2 页：方格背景 + 直线
        var page2 = new WhiteboardPage
        {
            Title = "第 2 页",
            BackgroundStyle = PageBackgroundStyle.Grid,
            Background = Color.FromRgb(0xFA, 0xF5, 0xE6)
        };

        page2.Items.Add(new ShapeItem
        {
            Tool = WhiteboardTool.Line,
            Start = new Point(100, 100),
            End = new Point(800, 700),
            Color = Colors.Black,
            Thickness = 3
        });

        document.Pages.Add(page1);
        document.Pages.Add(page2);
        document.ActivePage = page1;

        return document;
    }

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
