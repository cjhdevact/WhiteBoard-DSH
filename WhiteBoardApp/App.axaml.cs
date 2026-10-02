using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using WhiteBoard.Services;
using WhiteBoard.Views;

namespace WhiteBoard;

public partial class App : Application
{
    /// <summary>FluentAvalonia 主题的资源地址（跨程序集，只能运行时加载）。</summary>
    private const string FluentThemeUri = "avares://FluentAvalonia/Styling/Core/FluentAvaloniaTheme.axaml";

    private GlobalHotKeyService? _hotKeys;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // FluentAvalonia 主题提供 CommandBar / SymbolIcon 等控件样式，
        // 并自动跟随系统深色 / 浅色与默认蓝色强调色。
        // 放在最前面，让 AppStyles.axaml 里的覆盖样式能生效。
        try
        {
            var theme = (IStyle)AvaloniaXamlLoader.Load(new Uri(FluentThemeUri));
            Styles.Insert(0, theme);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FluentAvalonia] 主题加载失败: {ex.Message}");
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            StartGlobalHotKeys(desktop);

            desktop.Exit += (_, _) => ShutdownServices();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 注册系统级热键：即使焦点在别的程序里，也能随时调出批注层。
    /// </summary>
    private void StartGlobalHotKeys(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            _hotKeys = new GlobalHotKeyService();
            _hotKeys.Triggered += action => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                switch (action)
                {
                    case GlobalHotKeyAction.ToggleAnnotate:
                    case GlobalHotKeyAction.FreezeAndAnnotate:
                        if (desktop.MainWindow is MainWindow window)
                            window.ToggleDesktopAnnotationFromHotKey(action == GlobalHotKeyAction.FreezeAndAnnotate);
                        break;

                    case GlobalHotKeyAction.ToggleWhiteboard:
                        ToggleMainWindow(desktop);
                        break;

                    case GlobalHotKeyAction.ClearPage:
                        AppServices.MainViewModel.ClearPageCommand.Execute(null);
                        break;
                }
            });

            _hotKeys.Start();
        }
        catch
        {
            // 热键注册失败不影响主功能
        }
    }

    private static void ToggleMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var window = desktop.MainWindow;
        if (window is null)
            return;

        if (window.IsVisible)
        {
            window.Hide();
        }
        else
        {
            window.Show();
            window.Activate();
        }
    }

    /// <summary>退出前把设置写盘并释放资源。</summary>
    public void ShutdownServices()
    {
        try
        {
            _hotKeys?.Dispose();
            _hotKeys = null;
        }
        catch
        {
            // ignore
        }

        AppServices.Shutdown();
        BitmapReleaser.FlushAll();
    }
}
