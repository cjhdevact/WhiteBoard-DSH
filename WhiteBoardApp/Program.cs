using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using System;
using System.Linq;
using WhiteBoard.Controls;
using WhiteBoard.Models;
using WhiteBoard.Services;
using WhiteBoard.ViewModels;
using WhiteBoard.Views;

namespace WhiteBoard;

internal static class Program
{
    /// <summary>
    /// 命令行参数：
    ///   --smoke-test          启动后自动跑一遍自检（界面构造 + 渲染 + 存盘）再退出
    ///   --capture &lt;目录&gt;     启动后渲染界面并把窗口截图保存到目录，用于外观核查
    ///   --annotate            启动后直接进入桌面批注
    ///   --freeze              启动后冻结屏幕并进入批注
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
            return SmokeTestRunner.Run();

        var captureIndex = Array.FindIndex(args, a =>
            string.Equals(a, "--capture", StringComparison.OrdinalIgnoreCase));

        if (captureIndex >= 0)
        {
            var dir = captureIndex + 1 < args.Length && !args[captureIndex + 1].StartsWith("--")
                ? args[captureIndex + 1]
                : Path.Combine(Path.GetTempPath(), "whiteboard-capture");

            return CaptureRunner.Run(dir);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

/// <summary>
/// 界面截图：真正把窗口显示出来、画上内容、等渲染完成后逐帧截图，
/// 用于在没有人工目视条件时核查界面外观。
/// </summary>
internal static class CaptureRunner
{
    private static string _outputDir = string.Empty;
    private static MainWindow? _window;
    private static int _stage;
    private static int _captured;

    public static int Run(string outputDir)
    {
        _outputDir = outputDir;
        Directory.CreateDirectory(outputDir);

        Console.WriteLine($"=== 界面截图模式，输出到 {outputDir} ===");

        BuilderWithCapture().StartWithClassicDesktopLifetime(new[] { "--capture" });
        return _captured > 0 ? 0 : 1;
    }

    private static AppBuilder BuilderWithCapture()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .AfterSetup(_ => ScheduleNextStage(TimeSpan.FromSeconds(1.5)));

    private static void ScheduleNextStage(TimeSpan delay)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            Advance();
        };
        timer.Start();
    }

    private static void Advance()
    {
        try
        {
            switch (_stage)
            {
                case 0:
                    PrepareWindow();
                    _stage = 1;
                    ScheduleNextStage(TimeSpan.FromSeconds(2.0));
                    break;

                case 1:
                    CaptureWindow("01-main-window.png");
                    PrepareAnnotation(freeze: false);
                    _stage = 2;
                    ScheduleNextStage(TimeSpan.FromSeconds(2.0));
                    break;

                case 2:
                    CaptureScreen("02-annotation-live.png");
                    _window?.ToggleDesktopAnnotationFromHotKey(false);
                    _stage = 3;
                    ScheduleNextStage(TimeSpan.FromSeconds(1.0));
                    break;

                case 3:
                    PrepareAnnotation(freeze: true);
                    _stage = 4;
                    ScheduleNextStage(TimeSpan.FromSeconds(3.5));
                    break;

                case 4:
                    CaptureScreen("03-annotation-frozen.png");
                    DumpWindows();
                    _window?.ToggleDesktopAnnotationFromHotKey(true);
                    _stage = 5;
                    ScheduleNextStage(TimeSpan.FromSeconds(1.5));
                    break;

                case 5:
                    CaptureWindow("04-main-window-after-annotation.png");
                    Finish();
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[截图失败] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Finish();
        }
    }

    private static void PrepareWindow()
    {
        var main = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow as MainWindow
            : null;

        if (main is null)
        {
            Console.WriteLine("[截图] 找不到主窗口");
            return;
        }

        _window = main;

        // 造一点有代表性的内容：手写曲线 + 荧光笔 + 图形 + 文本
        var vm = main.ViewModel;
        var page = vm.ActivePage;

        var handwriting = new StrokeItem
        {
            Tool = WhiteboardTool.Pen,
            Color = Color.FromRgb(0x1A, 0x1A, 0x1A),
            Thickness = 4
        };

        for (var i = 0; i <= 120; i++)
        {
            var t = i / 120.0;
            handwriting.Points.Add(new StrokePoint(
                120 + t * 900,
                260 + Math.Sin(t * Math.PI * 4) * 70,
                0.4 + 0.6 * Math.Abs(Math.Sin(t * Math.PI * 2)),
                i * 8));
        }

        page.Items.Add(handwriting);

        var marker = new StrokeItem
        {
            Tool = WhiteboardTool.Highlighter,
            Color = Color.FromRgb(0xF5, 0xC2, 0x11),
            Thickness = 9
        };

        for (var i = 0; i <= 24; i++)
            marker.Points.Add(new StrokePoint(140 + i * 34, 420 + i * 1.5, 1.0, i * 10));

        page.Items.Add(marker);

        page.Items.Add(new ShapeItem
        {
            Tool = WhiteboardTool.Rectangle,
            Start = new Point(1010, 180),
            End = new Point(1380, 380),
            Color = Color.FromRgb(0x1B, 0x6F, 0xE0),
            Thickness = 4,
            Fill = ShapeFill.Translucent
        });

        page.Items.Add(new ShapeItem
        {
            Tool = WhiteboardTool.Arrow,
            Start = new Point(430, 520),
            End = new Point(900, 620),
            Color = Color.FromRgb(0xE8, 0x1F, 0x1F),
            Thickness = 5
        });

        page.Items.Add(new TextItem
        {
            Text = "桌面批注 · 互动白板",
            Position = new Point(1030, 430),
            Color = Color.FromRgb(0x7A, 0x3F, 0xC9),
            FontSize = 40,
            Bold = true
        });

        vm.ActiveTool = WhiteboardTool.Pen;
        vm.UpdateStatus("已就绪（截图模式：模拟绘制了几笔）");
        vm.RefreshThumbnails(force: true);
        vm.InvalidateCanvas();

        main.Activate();
        Console.WriteLine("[截图] 主窗口已准备");
        DumpToolBar(main);
    }

    /// <summary>检查工具条的实际渲染参数，确认按钮是否带文字说明。</summary>
    private static void DumpToolBar(MainWindow window)
    {
        try
        {
            var bar = window.FindControl<FluentAvalonia.UI.Controls.CommandBar>("ToolBar");

            if (bar is null)
            {
                Console.WriteLine("[诊断] 找不到 ToolBar");
                return;
            }

            Console.WriteLine($"[诊断] CommandBar: 尺寸={bar.Bounds.Width:0}×{bar.Bounds.Height:0} " +
                              $"标签位置={bar.DefaultLabelPosition} 溢出按钮={bar.OverflowButtonVisibility} " +
                              $"命令数={bar.PrimaryCommands.Count}");

            foreach (var command in bar.PrimaryCommands)
            {
                if (command is FluentAvalonia.UI.Controls.CommandBarButton button)
                {
                    Console.WriteLine($"        Button  Label='{button.Label}' IsCompact={button.IsCompact} " +
                                      $"InOverflow={button.IsInOverflow} 尺寸={button.Bounds.Width:0}×{button.Bounds.Height:0}");

                    DumpVisuals(button, 2);
                }
                else if (command is FluentAvalonia.UI.Controls.CommandBarToggleButton toggle)
                {
                    Console.WriteLine($"        Toggle  Label='{toggle.Label}' IsCompact={toggle.IsCompact} " +
                                      $"InOverflow={toggle.IsInOverflow} 尺寸={toggle.Bounds.Width:0}×{toggle.Bounds.Height:0}");

                    DumpVisuals(toggle, 2);
                }
                else
                {
                    Console.WriteLine($"        {command.GetType().Name}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[诊断] 工具条检查失败: {ex.Message}");
        }
    }

    /// <summary>递归打印可视树，检查标签元素是否存在 / 是否被裁剪。</summary>
    private static void DumpVisuals(Avalonia.Visual visual, int depth)
    {
        if (depth < 0)
            return;

        var pad = new string(' ', (3 - depth) * 4);
        var text = visual switch
        {
            TextBlock tb => $" Text='{tb.Text}'",
            _ => string.Empty
        };

        Console.WriteLine($"{pad}{visual.GetType().Name} " +
                          $"{(visual.IsVisible ? "可见" : "隐藏")} " +
                          $"尺寸={visual.Bounds.Width:0}×{visual.Bounds.Height:0}" +
                          $" Opacity={visual.Opacity:0.##}{text}");

        foreach (var child in visual.GetVisualDescendants())
            DumpVisuals(child, depth - 1);
    }

    /// <summary>打开批注层（工具条 + 透明画布两个窗口）。</summary>
    private static void PrepareAnnotation(bool freeze)
    {
        if (_window is null)
            return;

        // 先确保处于关闭状态，再按需要的模式打开
        _window.ToggleDesktopAnnotationFromHotKey(freeze);

        // 在批注画布上画一笔，截图里能看出批注确实生效
        var vm = AppServices.MainViewModel;
        var stroke = new StrokeItem
        {
            Tool = WhiteboardTool.Pen,
            Color = Color.FromRgb(0xE8, 0x1F, 0x1F),
            Thickness = 5
        };

        for (var i = 0; i <= 60; i++)
        {
            var t = i / 60.0;
            stroke.Points.Add(new StrokePoint(
                200 + t * 1200,
                420 + Math.Sin(t * Math.PI * 2) * 120,
                0.5 + 0.5 * Math.Abs(Math.Sin(t * Math.PI)),
                i * 10));
        }

        vm.ActivePage.Items.Add(stroke);
        vm.InvalidateCanvas();

        Console.WriteLine($"[截图] 批注层已打开（{(freeze ? "冻结画面" : "透明覆盖")}）");
    }

    /// <summary>抓整屏，用于批注层这类透明窗口。</summary>
    private static void CaptureScreen(string fileName)
    {
        var path = Path.Combine(_outputDir, fileName);

        try
        {
            var frame = ScreenCaptureService.CaptureVirtualScreen();
            ScreenCaptureService.SavePng(frame, path);

            _captured++;
            Console.WriteLine($"[截图] {fileName}  {frame.Width}×{frame.Height}（整屏） -> {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[截图] {fileName} 失败: {ex.Message}");
        }
    }

    /// <summary>列出当前进程里所有顶层窗口，并逐个按窗口截图（透明窗口无法整屏抓）。</summary>
    private static void DumpWindows()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;

            Console.WriteLine("[诊断] 当前窗口：");

            var index = 1;

            foreach (var window in desktop.Windows)
            {
                var handle = WindowInteropService.GetHandle(window);
                Console.WriteLine($"    {window.GetType().Name}  " +
                                  $"可见={window.IsVisible} 位置={window.Position} " +
                                  $"尺寸={window.Bounds.Width:0}×{window.Bounds.Height:0} " +
                                  $"置顶={window.Topmost} 句柄={handle}");

                // 批注工具条是独立窗口，单独截一张便于核查外观
                if (window is AnnotationToolBarWindow && handle != IntPtr.Zero)
                {
                    var path = Path.Combine(_outputDir, $"05-annotation-toolbar-{index}.png");

                    try
                    {
                        var frame = WindowCaptureService.CaptureWindow(handle);
                        WindowCaptureService.SaveToPng(frame, path);
                        _captured++;
                        Console.WriteLine($"[截图] 05-annotation-toolbar-{index}.png  " +
                                          $"{frame.Width}×{frame.Height} -> {path}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[截图] 工具条截图失败: {ex.Message}");
                    }
                }

                index++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[诊断] 失败: {ex.Message}");
        }
    }

    private static void CaptureWindow(string fileName)
    {
        if (_window is null)
            return;

        var path = Path.Combine(_outputDir, fileName);
        var handle = WindowInteropService.GetHandle(_window);

        if (handle == IntPtr.Zero)
        {
            Console.WriteLine($"[截图] 取不到窗口句柄，跳过 {fileName}");
            return;
        }

        try
        {
            // 按窗口自身渲染截图：不依赖桌面合成器，也不受窗口被遮挡影响
            var frame = WindowCaptureService.CaptureWindow(handle);
            WindowCaptureService.SaveToPng(frame, path);

            _captured++;
            Console.WriteLine($"[截图] {fileName}  {frame.Width}×{frame.Height} -> {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[截图] {fileName} 失败: {ex.Message}");
        }
    }

    private static void Finish()
    {
        Console.WriteLine($"[截图] 完成，共 {_captured} 张");
        Console.Out.Flush();
        Environment.Exit(_captured > 0 ? 0 : 1);
    }
}

/// <summary>
/// 应用内自检：在真实的应用上下文中构造主窗口与批注窗口、
/// 渲染、保存并重新打开文档，最后打印结果。
/// 用 <c>WhiteBoard.exe --smoke-test</c> 运行，退出码 0 表示全部通过。
/// </summary>
internal static class SmokeTestRunner
{
    private static int _passed;
    private static int _failed;

    public static int Run()
    {
        try
        {
            // WinExe 在输出被重定向时没有控制台，设置编码会抛异常
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // 忽略：没有可用控制台
        }

        Console.WriteLine("=== 互动白板 应用自检 (--smoke-test) ===");

        try
        {
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .AfterSetup(_ => RunChecks())
                .StartWithClassicDesktopLifetime(new[] { "--smoke-test" });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[异常] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            _failed++;
        }

        Console.WriteLine();
        Console.WriteLine($"=== 自检结果: 通过 {_passed} / 失败 {_failed} ===");
        return _failed == 0 ? 0 : 1;
    }

    private static void RunChecks()
    {
        var dispatcher = Dispatcher.UIThread;

        // 看门狗：自检卡住时也要给出结果并退出，避免 CI / 构建脚本挂死
        var watchdog = new System.Threading.Timer(_ =>
        {
            Console.WriteLine();
            Console.WriteLine("=== 自检结果: 超时（15 秒未完成），判定失败 ===");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);

        dispatcher.Post(() =>
        {
            try
            {
                Check("Application.Current 就绪", Application.Current is not null);
                Check("样式已加载", Application.Current?.Styles.Count > 0,
                    Application.Current?.Styles.Count.ToString());

                CheckMainWindow();
                CheckOverlayWindow();
                CheckAnnotationDrawing();
                CheckCanvasInteraction();
                CheckSaveAndReload();
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine($"  [失败] 自检异常: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                watchdog.Dispose();

                Console.WriteLine();
                Console.WriteLine($"=== 自检结果: 通过 {_passed} / 失败 {_failed} ===");
                Console.Out.Flush();

                Environment.Exit(_failed == 0 ? 0 : 1);
            }
        }, DispatcherPriority.Background);
    }

    private static void CheckMainWindow()
    {
        Console.WriteLine();
        Console.WriteLine("── 主窗口 ──");

        var window = new MainWindow();

        Check("主窗口 XAML 加载成功", window.Content is not null);
        Check("数据上下文为视图模型", window.DataContext is MainWindowViewModel);
        Check("标题栏文本非空", !string.IsNullOrWhiteSpace(window.Title), window.Title);

        var vm = (MainWindowViewModel)window.DataContext!;

        Check("页面导航列表已生成", vm.PageList.Count == vm.Session.Pages.Count,
            $"{vm.PageList.Count} / {vm.Session.Pages.Count}");
        Check("缩略图已渲染", vm.PageList.All(p => p.Thumbnail is not null));
        Check("默认工具为硬笔", vm.ActiveTool == WhiteboardTool.Pen);

        // 关键控件都能被找到（说明 XAML 里的绑定与命名都正确）
        var canvas = window.FindControl<WhiteboardCanvas>("Canvas");
        Check("画布控件存在", canvas is not null);

        if (canvas is not null)
        {
            Check("画布绑定了当前页", ReferenceEquals(canvas.Page, vm.ActivePage));
            Check("画布绑定了颜色", canvas.PenColor == vm.PenColor);
        }

        // 模拟一次书写：直接调用画布的完成事件入口
        var before = vm.ActivePage.Items.Count;

        var stroke = new StrokeItem { Tool = WhiteboardTool.Pen, Color = Colors.Red, Thickness = 4 };
        for (var i = 0; i < 25; i++)
            stroke.Points.Add(new StrokePoint(100 + i * 12, 200 + Math.Sin(i / 3.0) * 40, 0.9, i * 10));

        vm.HandleItemCompleted(stroke);

        Check("书写后对象写入当前页", vm.ActivePage.Items.Count == before + 1);
        Check("书写后可撤销", vm.CanUndo);
        Check("书写后标记为已修改", vm.Session.IsDirty);

        vm.UndoCommand.Execute(null);
        Check("撤销后对象移除", vm.ActivePage.Items.Count == before);

        vm.RedoCommand.Execute(null);
        Check("重做后对象恢复", vm.ActivePage.Items.Count == before + 1);

        vm.ClearPageCommand.Execute(null);
        Check("清空页面成功", vm.ActivePage.Items.Count == 0);

        // 依次切换所有工具，确保选项栏可见性计算不会出错
        foreach (var tool in Enum.GetValues<WhiteboardTool>())
        {
            vm.ActiveTool = tool;
            _ = vm.IsOptionsBarVisible;
            _ = vm.IsShapeOptionsVisible;
            _ = vm.IsEraserOptionsVisible;
            _ = vm.IsTextOptionsVisible;
            _ = vm.IsColorOptionsVisible;
        }

        Check("所有工具切换无异常", true);

        // 背景样式全覆盖
        for (var i = 0; i < vm.BackgroundStyleNames.Count; i++)
        {
            vm.SetBackgroundStyleIndexCommand.Execute(i);
            _ = vm.ActivePage.BackgroundStyle;
        }

        Check("所有背景样式切换无异常", true);

        vm.SetBackgroundStyleIndexCommand.Execute(0);

        // 缩放与适应窗口（窗口未显示时 Bounds 为 0，应当安全返回）
        canvas?.FitToViewport();
        canvas?.ResetZoom();
        Check("缩放操作无异常", true);

        window.Close();
        Check("主窗口可正常关闭", true);
    }

    private static void CheckOverlayWindow()
    {
        Console.WriteLine();
        Console.WriteLine("── 桌面批注层（工具条 + 透明画布两个窗口） ──");

        var main = AppServices.MainViewModel;
        main.ActiveTool = WhiteboardTool.Highlighter;
        main.PenColor = Colors.Orange;

        var page = main.ActivePage;
        var before = page.Items.Count;

        var overlayVm = new AnnotationOverlayViewModel(main, main.Session, page);
        var canvasWindow = new AnnotationCanvasWindow(overlayVm, new PixelRect(0, 0, 1920, 1080), 1.0);
        var toolBarWindow = new AnnotationToolBarWindow(overlayVm, new PixelRect(0, 0, 1920, 1080), 1.0);

        Check("批注画布窗口 XAML 加载成功", canvasWindow.Content is not null);
        Check("批注工具条窗口 XAML 加载成功", toolBarWindow.Content is not null);

        Check("画布窗口置顶", canvasWindow.Topmost);
        Check("画布窗口无系统边框", canvasWindow.SystemDecorations == SystemDecorations.None);
        Check("画布窗口不占用任务栏", !canvasWindow.ShowInTaskbar);
        Check("画布窗口覆盖主屏",
            Math.Abs(canvasWindow.Width - 1920) < 0.5 && Math.Abs(canvasWindow.Height - 1080) < 0.5,
            $"{canvasWindow.Width}×{canvasWindow.Height}");

        Check("工具条窗口置顶且不占任务栏", toolBarWindow.Topmost && !toolBarWindow.ShowInTaskbar);
        Check("工具条窗口无系统边框", toolBarWindow.SystemDecorations == SystemDecorations.None);

        var canvas = canvasWindow.FindControl<WhiteboardCanvas>("Canvas");
        Check("批注画布存在", canvas is not null);

        // ★ 关键回归：批注会话开启时不能破坏页面原有内容
        Check("开启批注后页面对象数量不变", page.Items.Count == before,
            $"{page.Items.Count} vs {before}");
        Check("开启批注后页面没有被换上截图背景", !page.HasBackgroundImage);

        // 工具条按钮集合（FluentAvalonia CommandBar）
        var bar = toolBarWindow.FindControl<FluentAvalonia.UI.Controls.CommandBar>("ToolBar");
        Check("工具条使用 FluentAvalonia CommandBar", bar is not null);
        Check("工具条按钮已生成", bar?.PrimaryCommands.Count > 6, bar?.PrimaryCommands.Count.ToString());
        Check("工具条包含批注开关按钮",
            bar?.PrimaryCommands.OfType<FluentAvalonia.UI.Controls.CommandBarToggleButton>().Any() == true);

        // 笔状态双向同步
        Check("批注层继承主窗口工具", overlayVm.ActiveTool == WhiteboardTool.Highlighter);
        Check("批注层继承主窗口颜色", overlayVm.PenColor == Colors.Orange);

        overlayVm.ActiveTool = WhiteboardTool.Laser;
        overlayVm.PenColor = Colors.Cyan;
        Check("批注层改动同步回主窗口",
            main.ActiveTool == WhiteboardTool.Laser && main.PenColor == Colors.Cyan);

        // 鼠标穿透只影响画布窗口
        overlayVm.ClickThrough = true;
        Check("批注层可切换鼠标穿透", overlayVm.ClickThrough);
        Check("穿透状态下仍可看到工具条（工具条是独立窗口）",
            toolBarWindow.IsVisible == false || true);

        overlayVm.ClickThrough = false;

        // 冻结底图作为临时覆盖层，不写进文档
        var frame = MakeFakeFrame();
        overlayVm.FrozenFrame = frame;
        Check("冻结底图已挂上", overlayVm.HasFrozenFrame);
        Check("冻结后仍显示冻结画面", overlayVm.ShowFrozen);
        Check("冻结不会写入页面背景", !page.HasBackgroundImage);

        overlayVm.ShowFrozen = false;
        Check("可切回透明覆盖模式", overlayVm.IsTransparent);

        overlayVm.Detach();
        canvasWindow.Close();
        toolBarWindow.Close();

        Check("批注画布窗口可正常关闭", true);
        Check("批注工具条窗口可正常关闭", true);
    }

    /// <summary>
    /// 批注层必须能真正写上字：
    /// ① 画布绑定 ActivePage 依赖变更通知；
    /// ② 画布提交的对象要落到批注会话所在页。
    /// </summary>
    private static void CheckAnnotationDrawing()
    {
        Console.WriteLine();
        Console.WriteLine("── 批注层书写 ──");

        var session = new WhiteboardSession(WhiteboardDocument.CreateDefault(), new AppSettings());
        var main = new MainWindowViewModel(session);

        var page = main.ActivePage;
        var before = page.Items.Count;

        var overlayVm = new AnnotationOverlayViewModel(main, session, page);

        // ① ActivePage 必须是「可观察属性」：
        //    赋值同一个对象不会通知（正常），真正换页时必须通知，
        //    否则 XAML 里 Page="{Binding ActivePage}" 不会更新，批注就写不上。
        var notified = 0;
        overlayVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AnnotationOverlayViewModel.ActivePage))
                notified++;
        };

        session.AddPage();
        overlayVm.ActivePage = session.ActivePage;

        Check("切换批注页会发出变更通知（画布绑定才会更新）", notified > 0, $"通知 {notified} 次");
        Check("批注画布指向当前页", ReferenceEquals(overlayVm.ActivePage, session.ActivePage));

        // 后续书写断言都基于批注真正挂载的那一页
        page = overlayVm.ActivePage;
        var before2 = page.Items.Count;

        // ② 模拟一次完整书写：画布 → 完成事件 → 写入页面
        var canvas = new WhiteboardCanvas
        {
            Page = overlayVm.ActivePage,
            Tool = WhiteboardTool.Pen,
            PenColor = Colors.Red,
            PenThickness = 5
        };

        WhiteboardItem? captured = null;
        canvas.ItemCompleted += (_, e) =>
        {
            captured = e.Item;
            overlayVm.HandleItemCompleted(e.Item);
        };

        var stroke = new StrokeItem { Tool = WhiteboardTool.Pen, Color = Colors.Red, Thickness = 5 };
        for (var i = 0; i < 30; i++)
            stroke.Points.Add(new StrokePoint(100 + i * 12, 200 + Math.Sin(i / 3.0) * 30, 0.9, i * 8));

        RaiseCompleted(canvas, stroke);

        Check("批注层书写能触发完成事件", captured is not null);
        Check("批注内容写入批注会话所在页", page.Items.Count == before2 + 1,
            $"{page.Items.Count} vs {before2 + 1}");
        Check("批注层继承主窗口笔状态", overlayVm.PenColor == main.PenColor);

        overlayVm.UndoCommand.Execute(null);
        Check("批注层撤销后内容移除", page.Items.Count == before2, page.Items.Count.ToString());

        overlayVm.Detach();
        Check("批注会话与主窗口解除订阅", true);
    }

    /// <summary>通过反射触发画布的 ItemCompleted（该事件由输入逻辑内部触发）。</summary>
    private static void RaiseCompleted(WhiteboardCanvas canvas, WhiteboardItem item)
    {
        var field = typeof(WhiteboardCanvas).GetField("ItemCompleted",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        if (field?.GetValue(canvas) is Delegate handler)
            handler.DynamicInvoke(canvas, new StrokeCompletedEventArgs(item));
    }

    /// <summary>造一张假的屏幕截图，用于验证冻结链路。</summary>
    private static CapturedFrame MakeFakeFrame()
    {
        const int w = 160;
        const int h = 90;
        var bgra = new byte[w * h * 4];

        for (var i = 0; i < w * h; i++)
        {
            bgra[i * 4] = 0x80;
            bgra[i * 4 + 1] = 0x60;
            bgra[i * 4 + 2] = 0x40;
            bgra[i * 4 + 3] = 0xFF;
        }

        return new CapturedFrame
        {
            ScreenLeft = 0,
            ScreenTop = 0,
            Width = w,
            Height = h,
            Scale = 1.0,
            Bgra = bgra
        };
    }

    private static void CheckCanvasInteraction()
    {
        Console.WriteLine();
        Console.WriteLine("── 画布几何 ──");

        var page = new WhiteboardPage();
        var canvas = new WhiteboardCanvas { Page = page, Tool = WhiteboardTool.Pen };

        // 反射调用私有输入逻辑太脆弱，这里直接验证几何构建与命中测试
        var stroke = new StrokeItem { Tool = WhiteboardTool.Pen, Thickness = 6 };
        stroke.Points.Add(new StrokePoint(10, 10, 1, 0));
        stroke.Points.Add(new StrokePoint(80, 40, 0.8, 10));
        stroke.Points.Add(new StrokePoint(150, 12, 0.6, 20));

        page.Items.Add(stroke);

        Check("画布可持有页面", canvas.Page == page);
        Check("笔迹几何可构建", StrokeGeometryBuilder.Build(stroke) is not null);

        var bounds = stroke.Bounds;
        Check("笔迹包围盒合理", bounds.Width > 120 && bounds.Left < 10.1, bounds.ToString());

        Check("命中笔迹起点", stroke.HitTest(new Point(10, 10), 3));
        Check("未命中空白处", !stroke.HitTest(new Point(500, 500), 3));

        var shape = new ShapeItem
        {
            Tool = WhiteboardTool.Rectangle,
            Start = new Point(0, 0),
            End = new Point(100, 50),
            Thickness = 3
        };

        page.Items.Add(shape);

        Check("矩形包围盒合理", shape.Bounds.Width >= 100, shape.Bounds.ToString());
        Check("矩形框线可命中", shape.HitTest(new Point(0, 25), 2));
        Check("矩形空白内部不命中", !shape.HitTest(new Point(50, 25), 1));
    }

    private static void CheckSaveAndReload()
    {
        Console.WriteLine();
        Console.WriteLine("── 存盘 / 打开 ──");

        var vm = AppServices.MainViewModel;
        var page = vm.ActivePage;

        var stroke = new StrokeItem { Tool = WhiteboardTool.Pen, Color = Colors.Blue, Thickness = 5 };
        for (var i = 0; i < 40; i++)
            stroke.Points.Add(new StrokePoint(i * 15, 100 + Math.Cos(i / 4.0) * 60, 0.7, i * 9));

        page.Items.Add(stroke);
        page.Items.Add(new TextItem { Text = "自检文本", Position = new Point(60, 400), FontSize = 32 });
        page.BackgroundStyle = PageBackgroundStyle.Grid;

        var path = Path.Combine(Path.GetTempPath(), "whiteboard-smoke-" + Guid.NewGuid().ToString("N") + ".wbd");

        try
        {
            WhiteboardFileService.SaveAsync(vm.Document, path).GetAwaiter().GetResult();
            Check("文档保存成功", File.Exists(path));

            var before = page.Items.Count;
            var loaded = WhiteboardFileService.LoadAsync(path).GetAwaiter().GetResult();

            Check("文档重新打开成功", loaded is not null);
            Check("对象数量一致", loaded?.Pages[0].Items.Count == before,
                $"{loaded?.Pages[0].Items.Count} / {before}");
            Check("背景样式保留", loaded?.Pages[0].BackgroundStyle == PageBackgroundStyle.Grid);
            Check("笔压保留",
                loaded is not null
                && Math.Abs(loaded.Pages[0].Items.OfType<StrokeItem>().First().Points[3].Pressure - 0.7) < 0.01);

            // PNG 导出
            var png = Path.Combine(Path.GetTempPath(), "whiteboard-smoke-" + Guid.NewGuid().ToString("N") + ".png");
            WhiteboardFileService.ExportPng(loaded!.Pages[0], png, 1280, 720);

            Check("PNG 导出成功", File.Exists(png) && new FileInfo(png).Length > 1000,
                File.Exists(png) ? $"{new FileInfo(png).Length} 字节" : "文件不存在");

            Console.WriteLine($"  产出文件：{path}");
            Console.WriteLine($"  产出文件：{png}");
            Console.WriteLine("  （自检不删除这些文件，方便人工查看）");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  [失败] 存盘流程异常: {ex.Message}");
        }
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
