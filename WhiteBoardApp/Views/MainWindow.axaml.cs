using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using WhiteBoard.Controls;
using WhiteBoard.Models;
using WhiteBoard.Services;
using WhiteBoard.ViewModels;

namespace WhiteBoard.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;

    private AnnotationOverlayViewModel? _annotation;
    private AnnotationCanvasWindow? _annotationCanvas;
    private AnnotationToolBarWindow? _annotationToolBar;

    private bool _wasMaximizedBeforeFullScreen;

    public MainWindow()
    {
        _vm = AppServices.MainViewModel;
        DataContext = _vm;

        AvaloniaXamlLoader.Load(this);
        InitializeCanvas();
        HookViewModel();
        HookKeyboard();

        Opened += OnOpened;
        Closing += OnClosing;
    }

    public MainWindowViewModel ViewModel => _vm;

    private WhiteboardCanvas DrawCanvas => this.FindControl<WhiteboardCanvas>("Canvas")!;

    private void InitializeCanvas()
    {
        var canvas = DrawCanvas;

        canvas.ItemCompleted += (_, e) => _vm.HandleItemCompleted(e.Item);

        canvas.ItemsErased += (_, e) => _vm.HandleItemsErased(e.Removed, e.Added);

        canvas.SelectionMoved += (_, e) => _vm.HandleSelectionMoved(e);

        canvas.SelectionChanged += (_, _) =>
        {
            var count = _vm.ActivePage.Items.Count(i => i.IsSelected);
            _vm.UpdateStatus(count == 0 ? "未选中对象" : $"已选中 {count} 个对象（可拖动，Delete 删除）");
        };

        canvas.ZoomChanged += (_, _) => _vm.NotifyStatusChanged();

        canvas.TextEditRequested += async (_, item) => await EditTextAsync(item);

        // 页面切换时显式同步，避免依赖绑定链
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainWindowViewModel.ActivePage) or nameof(MainWindowViewModel.ActivePageTitle)))
            return;

        var canvas = DrawCanvas;

        if (!ReferenceEquals(canvas.Page, _vm.ActivePage))
        {
            canvas.Page = _vm.ActivePage;
            canvas.ResetZoom();
        }

        canvas.InvalidateVisual();
    }

    private void HookViewModel()
    {
        // 工具条：FluentAvalonia CommandBar + 由构建器填充的 AppBarButton / AppBarToggleButton
        var bar = this.FindControl<FluentAvalonia.UI.Controls.CommandBar>("ToolBar");
        if (bar is not null)
        {
            FluentToolBarBuilder.Attach(bar, _vm.ToolBar, _vm.PickToolByIdCommand, _vm.ToolActionCommand);
        }

        _vm.FitToViewportRequested += (_, _) => DrawCanvas.FitToViewport();
        _vm.ResetZoomRequested += (_, _) => DrawCanvas.ResetZoom();
        _vm.CanvasInvalidated += (_, _) => DrawCanvas.InvalidateVisual();
        _vm.ToggleFullScreenRequested += (_, _) => ToggleFullScreen();

        _vm.DesktopAnnotateRequested += (_, _) => StartAnnotation(freezeScreen: false);
        _vm.FreezeScreenRequested += (_, _) => StartAnnotation(freezeScreen: true);

        _vm.ShowShortcutsRequested += async (_, _) => await ShowShortcutsAsync();
        _vm.ShowAboutRequested += async (_, _) => await ShowAboutAsync();
        _vm.InsertTextDialogRequested += async (_, _) => await InsertTextAtCenterAsync();

        _vm.SaveRequested += async (_, _) => await SaveDocumentAsync();
        _vm.CloseRequested += (_, _) => Close();
        _vm.ToggleWhiteboardRequested += (_, _) => ToggleVisibility();

        _vm.NewDocumentCommand = new AsyncRelayCommandAdapter(NewDocumentAsync);
        _vm.OpenDocumentCommand = new AsyncRelayCommandAdapter(OpenDocumentAsync);
        _vm.SaveDocumentCommand = new AsyncRelayCommandAdapter(SaveDocumentAsync);
        _vm.SaveAsDocumentCommand = new AsyncRelayCommandAdapter(SaveAsDocumentAsync);
        _vm.ExportPngCommand = new AsyncRelayCommandAdapter(ExportCurrentPageAsync);
        _vm.ExportAllPngCommand = new AsyncRelayCommandAdapter(ExportAllPagesAsync);
        _vm.ExitCommand = new RelayCommandAdapter(Close);

        _vm.RefreshHistoryState();
        _vm.RefreshThumbnails(force: true);
    }

    private void HookKeyboard()
    {
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        DrawCanvas.Focus();
        ApplyThemeFromSettings();

        // 一体机默认全屏白板模式
        if (_vm.Session.Settings.StartFullScreen && WindowState != WindowState.FullScreen)
        {
            _wasMaximizedBeforeFullScreen = false;
            WindowState = WindowState.FullScreen;
            _vm.SetFullScreenState(true);
        }

        _vm.UpdateStatus("就绪：左侧工具条选择笔和橡皮，直接书写即可");

        // 全屏 + 深绿板面时把画布铺满视口，笔迹坐标和屏幕对齐
        Dispatcher.UIThread.Post(() =>
        {
            DrawCanvas.FitToViewport(margin: 24);
            _vm.RefreshThumbnails(force: true);
        }, DispatcherPriority.Background);
    }

    private void ApplyThemeFromSettings()
    {
        try
        {
            var variant = Application.Current?.ActualThemeVariant ?? ThemeVariant.Default;
            _vm.IsDarkTheme = variant == ThemeVariant.Dark;
        }
        catch
        {
            // ignore
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        try
        {
            CloseAnnotation();

            _vm.Detach();
            AppServices.Shutdown();
            BitmapReleaser.FlushAll();
        }
        catch
        {
            // 关闭阶段的异常不应阻止退出
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  标题栏与窗口按钮
    // ─────────────────────────────────────────────────────────────

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (WindowState == WindowState.Maximized)
            return;

        try
        {
            BeginMoveDrag(e);
        }
        catch
        {
            // 某些平台上不支持
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _wasMaximizedBeforeFullScreen ? WindowState.Maximized : WindowState.Normal;
            _vm.SetFullScreenState(false);
        }
        else
        {
            _wasMaximizedBeforeFullScreen = WindowState == WindowState.Maximized;
            WindowState = WindowState.FullScreen;
            _vm.SetFullScreenState(true);
        }
    }

    private void ToggleVisibility()
    {
        if (IsVisible)
            Hide();
        else
        {
            Show();
            Activate();
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  键盘快捷键
    // ─────────────────────────────────────────────────────────────

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            switch (e.Key)
            {
                case Key.A:
                    StartAnnotation(freezeScreen: false);
                    e.Handled = true;
                    return;
                case Key.D:
                    StartAnnotation(freezeScreen: true);
                    e.Handled = true;
                    return;
                case Key.L:
                    _vm.ClearPageCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z:
                    _vm.UndoCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Y:
                    _vm.RedoCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.S:
                    _ = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? SaveAsDocumentAsync() : SaveDocumentAsync();
                    e.Handled = true;
                    return;
                case Key.O:
                    _ = OpenDocumentAsync();
                    e.Handled = true;
                    return;
                case Key.N:
                    _ = NewDocumentAsync();
                    e.Handled = true;
                    return;
                case Key.D0:
                case Key.NumPad0:
                    DrawCanvas.FitToViewport();
                    e.Handled = true;
                    return;
            }

            return;
        }

        switch (e.Key)
        {
            case Key.P: _vm.ActiveTool = WhiteboardTool.Pen; e.Handled = true; break;
            case Key.H: _vm.ActiveTool = WhiteboardTool.Highlighter; e.Handled = true; break;
            case Key.L: _vm.ActiveTool = WhiteboardTool.Laser; e.Handled = true; break;
            case Key.E: _vm.ActiveTool = WhiteboardTool.Eraser; e.Handled = true; break;
            case Key.V: _vm.ActiveTool = WhiteboardTool.Select; e.Handled = true; break;
            case Key.T: _vm.ActiveTool = WhiteboardTool.Text; e.Handled = true; break;
            case Key.F11: ToggleFullScreen(); e.Handled = true; break;
            case Key.OemPlus:
            case Key.Add:
                DrawCanvas.Zoom = Math.Min(8, DrawCanvas.Zoom * 1.15);
                e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                DrawCanvas.Zoom = Math.Max(0.1, DrawCanvas.Zoom / 1.15);
                e.Handled = true;
                break;
        }
    }

    private void OnPageCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: PageItemViewModel page })
            return;

        _vm.GoToPageCommand.Execute(page);
        e.Handled = true;
    }

    // ─────────────────────────────────────────────────────────────
    //  桌面批注（两个窗口：可移动工具条 + 透明画布）
    // ─────────────────────────────────────────────────────────────

    /// <summary>供全局热键调用。</summary>
    public void ToggleDesktopAnnotationFromHotKey(bool freezeScreen)
    {
        if (_annotation is not null)
            CloseAnnotation();
        else
            StartAnnotation(freezeScreen);
    }

    private void StartAnnotation(bool freezeScreen)
    {
        if (_annotation is not null)
        {
            CloseAnnotation();
            return;
        }

        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        var bounds = screen?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
        var scaling = screen?.Scaling ?? 1.0;

        CapturedFrame? frame = null;

        if (freezeScreen)
        {
            try
            {
                frame = ScreenCaptureService.CaptureVirtualScreen();
            }
            catch (Exception ex)
            {
                _vm.UpdateStatus($"抓屏失败：{ex.Message}");
                return;
            }
        }

        var vm = new AnnotationOverlayViewModel(_vm, _vm.Session, _vm.ActivePage);
        _annotation = vm;

        if (frame is not null)
        {
            vm.FrozenFrame = frame;
            vm.ShowFrozen = true;
        }

        vm.CloseRequested += (_, _) => CloseAnnotation();
        vm.SaveRequested += async (_, _) => await SaveDocumentAsync();
        vm.SwitchToWhiteboardRequested += (_, _) => CloseAnnotation();

        vm.ToggleFrozenRequested += (_, _) =>
        {
            if (vm.HasFrozenFrame)
            {
                vm.ShowFrozen = !vm.ShowFrozen;
                _vm.UpdateStatus(vm.ShowFrozen ? "已切回冻结画面" : "已切回透明覆盖");
            }
            else
            {
                try
                {
                    vm.FrozenFrame = ScreenCaptureService.CaptureVirtualScreen();
                    vm.ShowFrozen = true;
                    _vm.UpdateStatus("已冻结当前屏幕");
                }
                catch (Exception ex)
                {
                    _vm.UpdateStatus($"抓屏失败：{ex.Message}");
                }
            }
        };

        _annotationCanvas = new AnnotationCanvasWindow(vm, bounds, scaling);
        _annotationToolBar = new AnnotationToolBarWindow(vm, bounds, scaling);

        _annotationCanvas.Closed += (_, _) => CloseAnnotation();

        // 主窗口让位，避免挡住要批注的内容
        Hide();

        _annotationCanvas.Show();
        _annotationToolBar.Show();

        _annotationCanvas.Activate();
        _vm.UpdateStatus(freezeScreen
            ? "批注中（冻结画面）：Esc 退出，工具条可拖动"
            : "批注中（透明覆盖）：Esc 退出，工具条可拖动");
    }

    private void CloseAnnotation()
    {
        var vm = _annotation;
        _annotation = null;

        try
        {
            if (_annotationToolBar is not null)
            {
                _annotationToolBar.Close();
                _annotationToolBar = null;
            }

            if (_annotationCanvas is not null)
            {
                var canvas = _annotationCanvas;
                _annotationCanvas = null;
                canvas.Close();
            }
        }
        catch
        {
            // ignore
        }

        vm?.Detach();

        if (!IsVisible)
        {
            Show();
            Activate();
        }

        DrawCanvas.InvalidateVisual();
        _vm.RebuildPageList();
        _vm.UpdateStatus("已退出桌面批注");
    }

    // ─────────────────────────────────────────────────────────────
    //  文件操作
    // ─────────────────────────────────────────────────────────────

    private async Task NewDocumentAsync()
    {
        if (_vm.Session.IsDirty)
        {
            var confirm = await ConfirmDialog.ShowAsync(this, "新建白板",
                "当前白板还有未保存的改动，确定要新建吗？未保存的内容会丢失。",
                "新建", "取消");

            if (!confirm)
                return;
        }

        _vm.Session.ResetDocument();
        _vm.RebuildPageList();
        DrawCanvas.ResetZoom();
        _vm.UpdateStatus("已新建空白白板");
    }

    private async Task OpenDocumentAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开白板文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(WhiteboardFileService.FileFilterName)
                {
                    Patterns = new[] { "*." + WhiteboardFileService.FileExtension }
                },
                FilePickerFileTypes.All
            }
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            var document = await WhiteboardFileService.LoadAsync(path);
            if (document is null)
            {
                await MessageBox.ShowAsync(this, "打开失败", "文件内容无法解析。");
                return;
            }

            _vm.Session.LoadDocument(document, path);
            _vm.RebuildPageList();
            DrawCanvas.FitToViewport();
            _vm.UpdateStatus($"已打开：{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            await MessageBox.ShowAsync(this, "打开失败", ex.Message);
        }
    }

    private async Task<bool> SaveDocumentAsync()
    {
        if (string.IsNullOrEmpty(_vm.Session.CurrentFilePath))
            return await SaveAsDocumentAsync();

        return await WriteDocumentAsync(_vm.Session.CurrentFilePath);
    }

    private async Task<bool> SaveAsDocumentAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存白板文件",
            SuggestedFileName = SanitizeFileName(_vm.Document.Title) + "." + WhiteboardFileService.FileExtension,
            DefaultExtension = WhiteboardFileService.FileExtension,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(WhiteboardFileService.FileFilterName)
                {
                    Patterns = new[] { "*." + WhiteboardFileService.FileExtension }
                }
            }
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return false;

        return await WriteDocumentAsync(path);
    }

    private async Task<bool> WriteDocumentAsync(string path)
    {
        try
        {
            _vm.Session.Document.Title = Path.GetFileNameWithoutExtension(path);
            await WhiteboardFileService.SaveAsync(_vm.Session.Document, path);

            _vm.Session.CurrentFilePath = path;
            _vm.Session.IsDirty = false;
            _vm.UpdateStatus($"已保存：{Path.GetFileName(path)}（{new FileInfo(path).Length / 1024.0:0.#} KB）");
            return true;
        }
        catch (Exception ex)
        {
            await MessageBox.ShowAsync(this, "保存失败", ex.Message);
            return false;
        }
    }

    private async Task ExportCurrentPageAsync()
    {
        var (w, h) = ParseExportSize();
        var safeFolder = WhiteboardFileService.PickWritableDirectory(_vm.Session.Settings.LastExportDirectory);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出当前页为图片",
            SuggestedFileName = $"{SanitizeFileName(_vm.Document.Title)}_第{_vm.Session.PageIndex + 1}页.png",
            DefaultExtension = "png",
            SuggestedStartLocation = await TryGetFolderAsync(safeFolder),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG 图片") { Patterns = new[] { "*.png" } }
            }
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        if (!await EnsureWritableAsync(path))
            return;

        try
        {
            WhiteboardFileService.ExportPng(_vm.ActivePage, path, w, h);

            _vm.Session.Settings.LastExportDirectory = Path.GetDirectoryName(path);
            _vm.Session.Settings.Save();
            _vm.UpdateStatus($"已导出：{Path.GetFileName(path)}（{w}×{h}）");
        }
        catch (Exception ex)
        {
            await MessageBox.ShowAsync(this, "导出失败", ex.Message);
        }
    }

    private async Task ExportAllPagesAsync()
    {
        var (w, h) = ParseExportSize();
        var safeFolder = WhiteboardFileService.PickWritableDirectory(_vm.Session.Settings.LastExportDirectory);

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择导出目录",
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetFolderAsync(safeFolder)
        });

        var folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(folder))
            return;

        if (!await EnsureWritableAsync(Path.Combine(folder, "probe.png")))
            return;

        var name = SanitizeFileName(_vm.Document.Title);

        try
        {
            for (var i = 0; i < _vm.Session.Pages.Count; i++)
            {
                var path = Path.Combine(folder, $"{name}_第{i + 1}页.png");
                WhiteboardFileService.ExportPng(_vm.Session.Pages[i], path, w, h);
            }

            _vm.Session.Settings.LastExportDirectory = folder;
            _vm.Session.Settings.Save();
            _vm.UpdateStatus($"已导出 {_vm.Session.Pages.Count} 张图片到 {folder}");
        }
        catch (Exception ex)
        {
            await MessageBox.ShowAsync(this, "导出失败", ex.Message);
        }
    }

    /// <summary>
    /// 导出前先确认目标目录可写。
    /// 桌面 / 文档这类目录常被 Windows 的「受控文件夹访问」或 OneDrive 重定向拦住，
    /// 与其让用户挑完路径后才看到一个 Access denied，不如提前讲清楚并给替代目录。
    /// </summary>
    private async Task<bool> EnsureWritableAsync(string targetFile)
    {
        var directory = Path.GetDirectoryName(targetFile);

        if (string.IsNullOrEmpty(directory))
            return true;

        if (WhiteboardFileService.IsDirectoryWritable(directory))
            return true;

        var fallback = WhiteboardFileService.PickWritableDirectory();

        return await ConfirmDialog.ShowAsync(this, "这个位置无法写入",
            $"""
            没有权限写入：
            {directory}

            常见原因是 Windows 安全中心的「受控文件夹访问」（勒索软件防护）
            拦截了程序对桌面 / 文档等目录的写入，或者这些目录被重定向到了 OneDrive。
            可以改用其它目录导出，推荐：
            {fallback}

            要继续选择其它位置吗？（取消则放弃本次导出）
            """,
            "重新选择", "放弃导出");
    }

    /// <summary>把一个本机路径包装成存储提供程序的文件夹对象（失败时返回 null）。</summary>
    private async Task<IStorageFolder?> TryGetFolderAsync(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        try
        {
            return await StorageProvider.TryGetFolderFromPathAsync(path);
        }
        catch
        {
            return null;
        }
    }

    private (int Width, int Height) ParseExportSize()
    {
        var parts = _vm.ExportSize.Split('×', 'x', 'X');

        if (parts.Length == 2
            && int.TryParse(parts[0].Trim(), out var w)
            && int.TryParse(parts[1].Trim(), out var h)
            && w > 0 && h > 0)
        {
            return (w, h);
        }

        return (1920, 1080);
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "白板";

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return name;
    }

    // ─────────────────────────────────────────────────────────────
    //  文本
    // ─────────────────────────────────────────────────────────────

    private async Task InsertTextAtCenterAsync()
    {
        var text = await TextInputDialog.ShowAsync(this, "插入文本", string.Empty);
        if (string.IsNullOrWhiteSpace(text))
            return;

        var x = (DrawCanvas.Bounds.Width / 2 - DrawCanvas.PanOffset.X) / DrawCanvas.Zoom;
        var y = (DrawCanvas.Bounds.Height / 2 - DrawCanvas.PanOffset.Y) / DrawCanvas.Zoom;

        _vm.InsertText(text, x, y);
    }

    private async Task EditTextAsync(TextItem item)
    {
        var text = await TextInputDialog.ShowAsync(this, "编辑文本", item.Text);
        if (text is null)
            return;

        if (string.IsNullOrWhiteSpace(text))
        {
            _vm.ActivePage.Items.Remove(item);
            _vm.UpdateStatus("已删除空文本");
        }
        else
        {
            item.Text = text;
            _vm.UpdateStatus("已更新文本");
        }

        _vm.InvalidateCanvas();
        _vm.RefreshThumbnails();
    }

    // ─────────────────────────────────────────────────────────────
    //  帮助
    // ─────────────────────────────────────────────────────────────

    private Task ShowShortcutsAsync() => MessageBox.ShowAsync(this, "快捷键说明",
        """
        【白板工具】
          P 硬笔    H 荧光笔    L 激光笔    E 橡皮擦    V 选择    T 文本

        【编辑】
          Ctrl+Z 撤销      Ctrl+Y 重做      Delete 删除选中
          Ctrl+S 保存      Ctrl+Shift+S 另存为
          Ctrl+O 打开      Ctrl+N 新建      Ctrl+0 适应窗口
          F11 全屏         +/- 缩放

        【画布】
          左键拖动   书写 / 绘制
          右键拖动   平移画布
          Ctrl+滚轮  以光标为中心缩放
          Shift      约束正方形 / 45° 直线

        【桌面批注】
          Ctrl+Alt+A  切换桌面批注（全局热键，任何程序里都能用）
          Ctrl+Alt+D  冻结当前屏幕并批注
          Ctrl+Alt+W  显示 / 隐藏白板
          Ctrl+Alt+L  清空当前页
          工具条按住顶部可拖动；Esc 退出批注
          开启「鼠标穿透」后仍可点击工具条，不会被锁在批注层里
        """);

    private Task ShowAboutAsync() => MessageBox.ShowAsync(this, "关于 互动白板",
        """
        互动白板 WhiteBoard 1.0

        基于 .NET 8 + Avalonia 11 + FluentAvalonia 的桌面白板 / 批注工具。

        • 硬笔（带笔压）/ 荧光笔 / 激光笔 / 像素橡皮
        • 直线、箭头、矩形、椭圆、文本标注
        • 多页白板 + 页面缩略图导航 + 撤销重做
        • 方格 / 横线 / 点阵 / 五线谱 / 田字格底纹
        • 桌面批注：透明覆盖或冻结画面，工具条可拖动，支持鼠标穿透
        • 保存 / 打开 .wbd 文档，导出 PNG

        目标框架：net8.0-windows
        UI 框架：Avalonia 11.3.12 + FluentAvalonia 2.4.1
        """);
}

/// <summary>把异步方法适配成命令。</summary>
public sealed class AsyncRelayCommandAdapter : System.Windows.Input.ICommand
{
    private readonly Func<Task> _execute;
    private bool _running;

    public AsyncRelayCommandAdapter(Func<Task> execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running;

    public async void Execute(object? parameter)
    {
        if (_running)
            return;

        _running = true;

        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[command] {ex}");
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>把同步方法适配成命令。</summary>
public sealed class RelayCommandAdapter : System.Windows.Input.ICommand
{
    private readonly Action _execute;

    public RelayCommandAdapter(Action execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
