using WhiteBoard.Models;
using WhiteBoard.ViewModels;

namespace WhiteBoard.Services;

/// <summary>
/// 轻量的应用级服务容器。主窗口与桌面批注层需要共享同一份
/// “当前笔状态”（颜色 / 粗细 / 工具），通过这里拿到同一个实例。
/// </summary>
public static class AppServices
{
    private static MainWindowViewModel? _mainViewModel;

    /// <summary>主窗口的视图模型（同时也是共享的笔状态）。</summary>
    public static MainWindowViewModel MainViewModel
    {
        get
        {
            if (_mainViewModel is null)
            {
                var settings = AppSettings.Load();
                var document = WhiteboardDocument.CreateDefault();
                var session = new WhiteboardSession(document, settings);
                _mainViewModel = new MainWindowViewModel(session);
            }

            return _mainViewModel;
        }
    }

    /// <summary>是否已经创建过（关闭时用于判断要不要保存设置）。</summary>
    public static bool HasMainViewModel => _mainViewModel is not null;

    public static WhiteboardSession CurrentSession => MainViewModel.Session;

    /// <summary>把设置落盘并释放资源。</summary>
    public static void Shutdown()
    {
        if (_mainViewModel is null)
            return;

        _mainViewModel.FlushToSettings(_mainViewModel.Session.Settings);
        _mainViewModel.Session.Settings.Save();
        _mainViewModel.Session.Thumbnails.Dispose();

        _mainViewModel = null;
    }
}
