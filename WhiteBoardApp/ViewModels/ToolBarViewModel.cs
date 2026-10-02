using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WhiteBoard.Models;

namespace WhiteBoard.ViewModels;

/// <summary>工具条项的图标类型。</summary>
public enum ToolBarIconKind
{
    /// <summary>用 FluentAvalonia 的 Symbol 字形。</summary>
    Symbol = 0,

    /// <summary>用内置矢量路径。</summary>
    Path = 1,

    /// <summary>用工程内的图片资源（按需求：橡皮使用 ui/clean.png）。</summary>
    Image = 2
}

/// <summary>
/// 工具「家族」。同一个家族的工具共用一个一级按钮，
/// 具体的笔类型放在二级菜单里（二级菜单切换后，一级按钮保持选中）。
/// </summary>
public enum ToolFamily
{
    /// <summary>不属于任何家族（独立按钮，如橡皮、文本）。</summary>
    None = 0,

    /// <summary>笔类：硬笔 / 荧光笔 / 激光笔。</summary>
    Pen = 1,

    /// <summary>图形类：直线 / 箭头 / 矩形 / 椭圆。</summary>
    Shape = 2,

    /// <summary>橡皮。</summary>
    Eraser = 3
}

/// <summary>
/// 工具栏上的一项。
/// 用普通对象描述（而不是在 XAML 里逐个写按钮），
/// 这样主界面工具条和批注工具条可以共用同一份定义。
/// </summary>
public sealed partial class ToolBarItem : ObservableObject
{
    public ToolBarItem(string id, string label, string symbol, string? path = null)
    {
        Id = id;
        Label = label;
        Symbol = symbol;
        PathData = path;
        IconKind = string.IsNullOrEmpty(path) ? ToolBarIconKind.Symbol : ToolBarIconKind.Path;
    }

    /// <summary>
    /// 稳定标识。
    /// 普通按钮 = 目标工具名；家族按钮 = 家族默认工具名（如 Pen / Line）。
    /// 以 <c>__</c> 开头的是「动作」而不是工具。
    /// </summary>
    public string Id { get; }

    public string Label { get; }

    public string Symbol { get; }

    public string? PathData { get; }

    public ToolBarIconKind IconKind { get; init; }

    /// <summary>图片图标资源地址（avares://…），仅在 <see cref="IconKind"/> 为 Image 时使用。</summary>
    public string? ImageSource { get; init; }

    /// <summary>
    /// 图片图标是否按主题前景色着色。
    /// ui/clean.png 是固定浅灰色，深色板面上看不清，着色后能跟随主题。
    /// </summary>
    public bool TintImage { get; init; } = true;

    /// <summary>是否是分隔符。</summary>
    public bool IsSeparator { get; init; }

    /// <summary>是否是开关按钮（有选中态）。</summary>
    public bool IsToggle { get; init; }

    /// <summary>所属家族；决定哪些工具共用一个一级按钮的选中态。</summary>
    public ToolFamily Family { get; init; } = ToolFamily.None;

    /// <summary>一级按钮旁边是否显示「有子菜单」的小三角。</summary>
    public bool HasSubmenu { get; init; }

    /// <summary>二级菜单里的工具。</summary>
    public IReadOnlyList<ToolBarItem> SubmenuItems { get; init; } = Array.Empty<ToolBarItem>();

    /// <summary>按钮选中态（由 <see cref="ToolBarViewModel"/> 统一维护）。</summary>
    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>该家族当前选中的具体工具（用于二级菜单打勾）。</summary>
    [ObservableProperty]
    private string? _currentSubmenuToolId;

    public static ToolBarItem Separator() => new("__sep", string.Empty, string.Empty) { IsSeparator = true };
}

/// <summary>
/// 工具条视图模型。主窗口与桌面批注工具条共用，
/// 因此两处的按钮集合、选中态、可用状态完全同步。
/// </summary>
public sealed partial class ToolBarViewModel : ObservableObject
{
    private readonly EditorState _editor;

    public ToolBarViewModel(EditorState editor, IReadOnlyList<ToolBarItem> items)
    {
        _editor = editor;

        // 展开家族按钮的二级工具，便于按工具名反查家族
        var all = new List<ToolBarItem>();

        foreach (var item in items)
        {
            all.Add(item);
            all.AddRange(item.SubmenuItems);
        }

        AllItems = all;
        Items = new ObservableCollection<ToolBarItem>(items);
        RefreshSelection();
    }

    /// <summary>一级按钮（含分隔符）。</summary>
    public ObservableCollection<ToolBarItem> Items { get; }

    /// <summary>所有工具项（含二级菜单里的），用于反查。</summary>
    public IReadOnlyList<ToolBarItem> AllItems { get; }

    /// <summary>
    /// 把当前工具同步到按钮选中态。
    /// 家族按钮：家族内任一工具被选中，一级按钮就选中。
    /// </summary>
    public void RefreshSelection()
    {
        var active = _editor.ActiveTool;

        foreach (var item in Items)
        {
            if (item.IsSeparator)
                continue;

            if (item.Family != ToolFamily.None)
            {
                // 家族按钮：看当前工具是否属于本家族
                item.IsChecked = ToolBarItems.BelongsTo(active, item.Family);

                // 记录家族内当前具体工具，便于二级菜单打勾
                item.CurrentSubmenuToolId = item.IsChecked ? active.ToString() : null;

                foreach (var sub in item.SubmenuItems)
                    sub.IsChecked = item.IsChecked && sub.Id.Equals(active.ToString(), StringComparison.OrdinalIgnoreCase);

                if (item.IsChecked)
                    item.CurrentSubmenuToolId = active.ToString();
            }
            else
            {
                item.IsChecked = Enum.TryParse<WhiteboardTool>(item.Id, true, out var tool) && tool == active;
            }
        }

        OnPropertyChanged(nameof(ActiveToolLabel));
    }

    /// <summary>状态栏用：当前工具名。</summary>
    public string ActiveToolLabel => _editor.ToolDisplayName;
}

/// <summary>工具条项目工厂：集中维护图标、文案与分组，避免两处界面写出不一致的按钮。</summary>
public static class ToolBarItems
{
    // ── 单个工具 ────────────────────────────────────────────

    public static ToolBarItem Select() => new("Select", "选择", "SelectAll", Icons.Select) { IsToggle = true };

    public static ToolBarItem Pen() => new("Pen", "笔", "Edit", Icons.Pen);

    public static ToolBarItem Highlighter() => new("Highlighter", "荧光笔", "Highlight", Icons.Highlighter);

    public static ToolBarItem Laser() => new("Laser", "激光笔", "View", Icons.Laser);

    public static ToolBarItem Line() => new("Line", "直线", "Cut", Icons.Line);

    public static ToolBarItem Arrow() => new("Arrow", "箭头", "Forward", Icons.Arrow);

    public static ToolBarItem Rectangle() => new("Rectangle", "矩形", "Stop", Icons.Rectangle);

    public static ToolBarItem Ellipse() => new("Ellipse", "椭圆", "World", Icons.Ellipse);

    public static ToolBarItem Text() => new("Text", "文本", "Font", Icons.Text) { IsToggle = true };

    // ── 动作 ────────────────────────────────────────────────

    public static ToolBarItem Undo() => new("__undo", "撤销", "Undo", Icons.Undo);

    public static ToolBarItem Redo() => new("__redo", "重做", "Redo", Icons.Redo);

    public static ToolBarItem Clear() => new("__clear", "清空", "Delete", Icons.Trash);

    public static ToolBarItem Save() => new("__save", "保存", "Save", Icons.Save);

    public static ToolBarItem Menu() => new("__menu", "菜单", "GlobalNavigationButton", Icons.Menu);

    public static ToolBarItem Close() => new("__close", "关闭", "Cancel", Icons.Close);

    public static ToolBarItem ClickThrough() => new("__through", "鼠标穿透", "Move", Icons.ClickThrough)
    {
        IsToggle = true
    };

    public static ToolBarItem Whiteboard() => new("__whiteboard", "白板", "Home", Icons.DesktopPen);

    // ── 家族按钮（一级入口，具体类型在二级菜单） ──────────────

    /// <summary>笔家族：一级按钮点一下就回到硬笔，二级菜单里切换硬笔 / 荧光笔 / 激光笔。</summary>
    public static ToolBarItem PenFamily() => new("Pen", "笔", "Edit", Icons.Pen)
    {
        IsToggle = true,
        Family = ToolFamily.Pen,
        HasSubmenu = true,
        SubmenuItems = new[] { Pen(), Highlighter(), Laser() }
    };

    /// <summary>
    /// 橡皮：直接使用 ui/clean.png 作为显示样式（按需求）。
    /// 参考图是固定浅灰色，深色主题下几乎看不见，因此做了主题着色处理。
    /// </summary>
    public static ToolBarItem Eraser() => new("Eraser", "橡皮", "Clear")
    {
        IsToggle = true,
        Family = ToolFamily.Eraser,
        IconKind = ToolBarIconKind.Image,
        ImageSource = "avares://WhiteBoard/Assets/Icons/clean.png"
    };

    /// <summary>图形家族：一级按钮点一下是直线，二级菜单里切换直线 / 箭头 / 矩形 / 椭圆。</summary>
    public static ToolBarItem ShapeFamily() => new("Line", "形状", "Cut", Icons.Line)
    {
        IsToggle = true,
        Family = ToolFamily.Shape,
        HasSubmenu = true,
        SubmenuItems = new[] { Line(), Arrow(), Rectangle(), Ellipse() }
    };

    /// <summary>判断某个工具是否属于指定家族。</summary>
    public static bool BelongsTo(WhiteboardTool tool, ToolFamily family) => family switch
    {
        ToolFamily.Pen => tool is WhiteboardTool.Pen or WhiteboardTool.Highlighter or WhiteboardTool.Laser,
        ToolFamily.Shape => tool is WhiteboardTool.Line or WhiteboardTool.Arrow
            or WhiteboardTool.Rectangle or WhiteboardTool.Ellipse,
        ToolFamily.Eraser => tool is WhiteboardTool.Eraser,
        _ => false
    };

    // ── 完整工具条 ──────────────────────────────────────────

    /// <summary>
    /// 主窗口工具条。
    /// 顺序参考希沃白板底栏：选择 · 笔 · 橡皮 · 形状 · 文本 · 撤销 · 重做 · 清空。
    /// </summary>
    public static List<ToolBarItem> MainToolBar() => new()
    {
        Select(),
        ToolBarItem.Separator(),
        PenFamily(),
        Eraser(),
        ShapeFamily(),
        Text(),
        ToolBarItem.Separator(),
        Undo(),
        Redo(),
        Clear()
    };

    /// <summary>
    /// 桌面批注工具条：在主窗口基础上加了菜单 / 保存 / 穿透 / 回白板。
    /// 布局参考 ui/note.png：关闭 · 保存 · 菜单 在左，书写工具居中。
    /// </summary>
    public static List<ToolBarItem> AnnotationToolBar() => new()
    {
        Menu(),
        ToolBarItem.Separator(),
        PenFamily(),
        Eraser(),
        ShapeFamily(),
        ToolBarItem.Separator(),
        Undo(),
        Redo(),
        Clear(),
        ToolBarItem.Separator(),
        ClickThrough(),
        Whiteboard(),
        ToolBarItem.Separator(),
        Save(),
        Close()
    };
}

/// <summary>自定义矢量图标（与 Assets/Icons.axaml 保持一致，供运行时构造按钮使用）。</summary>
public static class Icons
{
    public const string Select = "M 5.4,1.8 L 18.8,12.6 L 12.4,13.2 L 15.6,20.6 L 12.9,21.8 L 9.8,14.4 L 5.4,19.8 Z";

    /// <summary>笔：斜握的笔杆 + 笔尖。</summary>
    public const string Pen = "M 12,1.6 L 18.9,11.9 L 12,16.4 L 5.1,11.9 Z M 12,3.6 L 7.3,11.6 L 12,14.5 L 16.7,11.6 Z " +
                              "M 11.4,17 L 12.6,17 L 12,22.4 Z";

    /// <summary>荧光笔：细线笔身 + 斜切笔尖 + 高光条。</summary>
    public const string Highlighter = "M 3.4,19.2 L 8.4,21.6 L 8.4,23 H 3.4 Z M 9.8,12.6 L 13.6,14.5 L 19.6,8.5 L 15.8,6.6 Z " +
                                      "M 16.6,4 L 21,6.2 L 20.4,7.4 L 16,5.2 Z";

    public const string Laser = "M 12,6.4 C 15.1,6.4 17.6,8.9 17.6,12 C 17.6,15.1 15.1,17.6 12,17.6 C 8.9,17.6 6.4,15.1 6.4,12 " +
                                "C 6.4,8.9 8.9,6.4 12,6.4 Z M 12,9.6 C 10.7,9.6 9.6,10.7 9.6,12 C 9.6,13.3 10.7,14.4 12,14.4 " +
                                "C 13.3,14.4 14.4,13.3 14.4,12 C 14.4,10.7 13.3,9.6 12,9.6 Z M 10.9,1.4 H 13.1 V 4.6 H 10.9 Z " +
                                "M 10.9,19.4 H 13.1 V 22.6 H 10.9 Z M 1.4,10.9 H 4.6 V 13.1 H 1.4 Z M 19.4,10.9 H 22.6 V 13.1 H 19.4 Z";

    /// <summary>
    /// ui/clean.png 的造型：圆角方框（外轮廓 + 内孔挖空）+ 两条竖圆角条。
    /// 用「外框顺时针 / 内孔逆时针」的非零环绕方式自然挖空。
    /// </summary>
    public const string EraserClean =
        "M 6,3 H 18 A 3,3 0 0 1 21,6 V 18 A 3,3 0 0 1 18,21 H 6 A 3,3 0 0 1 3,18 V 6 A 3,3 0 0 1 6,3 Z " +
        "M 6,5.4 A 0.6,0.6 0 0 0 5.4,6 V 18 A 0.6,0.6 0 0 0 6,18.6 H 18 A 0.6,0.6 0 0 0 18.6,18 V 6 " +
        "A 0.6,0.6 0 0 0 18,5.4 Z " +
        "M 9.6,7.6 H 10.8 A 0.6,0.6 0 0 1 11.4,8.2 V 15.8 A 0.6,0.6 0 0 1 10.8,16.4 H 9.6 " +
        "A 0.6,0.6 0 0 1 9,15.8 V 8.2 A 0.6,0.6 0 0 1 9.6,7.6 Z " +
        "M 13.2,7.6 H 14.4 A 0.6,0.6 0 0 1 15,8.2 V 15.8 A 0.6,0.6 0 0 1 14.4,16.4 H 13.2 " +
        "A 0.6,0.6 0 0 1 12.6,15.8 V 8.2 A 0.6,0.6 0 0 1 13.2,7.6 Z";

    public const string Line = "M 4.2,16.6 C 5.5,16.6 6.5,17.6 6.5,18.9 C 6.5,20.2 5.5,21.2 4.2,21.2 C 2.9,21.2 1.9,20.2 1.9,18.9 " +
                               "C 1.9,17.6 2.9,16.6 4.2,16.6 Z M 19.8,2.8 C 21.1,2.8 22.1,3.8 22.1,5.1 C 22.1,6.4 21.1,7.4 19.8,7.4 " +
                               "C 18.5,7.4 17.5,6.4 17.5,5.1 C 17.5,3.8 18.5,2.8 19.8,2.8 Z M 6.2,17.5 L 20.2,5.3 L 19,3.8 L 4.4,16.6 Z";

    public const string Arrow = "M 2.2,19.6 L 12.6,9.2 L 9.8,6.4 L 21.4,2.8 L 17.8,14.4 L 15,11.6 L 4.6,22 Z";

    public const string Rectangle = "M 2.2,4.4 H 21.8 V 19.6 H 2.2 Z M 4.6,6.8 V 17.2 H 19.4 V 6.8 Z";

    public const string Ellipse = "M 2.2,6 H 21.8 V 18 H 2.2 Z M 4.6,8.4 V 15.6 H 19.4 V 8.4 Z";

    public const string Text = "M 3,3.2 H 21 V 7.2 H 13.4 V 20.8 H 10.6 V 7.2 H 3 Z";

    public const string Undo = "M 11.6,4.4 V 8.4 C 9.3,8.4 7.4,9.7 7.4,12.1 C 7.4,14.5 9.5,15.9 12,15.9 C 14.5,15.9 16.4,14.4 16.4,12.1 " +
                               "H 19.6 C 19.6,16.2 16.3,18.7 12,18.7 C 7.7,18.7 4.2,16.3 4.2,12.2 C 4.2,8 7.6,4.4 11.6,4.4 Z " +
                               "M 12.4,2 L 8.4,6.4 L 12.4,10.8 Z M 11.6,4.4 C 8.9,4.7 6.9,6.3 5.8,8.3 L 3.1,6.7 C 4.7,4.2 7.6,2.4 10.9,2.1 Z";

    public const string Redo = "M 12.4,4.4 V 8.4 C 14.7,8.4 16.6,9.7 16.6,12.1 C 16.6,14.5 14.5,15.9 12,15.9 C 9.5,15.9 7.6,14.4 7.6,12.1 " +
                               "H 4.4 C 4.4,16.2 7.7,18.7 12,18.7 C 16.3,18.7 19.8,16.3 19.8,12.2 C 19.8,8 16.4,4.4 12.4,4.4 Z " +
                               "M 11.6,2 L 15.6,6.4 L 11.6,10.8 Z M 12.4,4.4 C 15.1,4.7 17.1,6.3 18.2,8.3 L 20.9,6.7 C 19.3,4.2 16.4,2.4 13.1,2.1 Z";

    public const string Trash = "M 9.4,2.2 H 14.6 L 15.6,4.6 H 20.8 V 7 H 3.2 V 4.6 H 8.4 Z M 4.6,8.4 H 19.4 V 20.4 " +
                                "C 19.4,21.6 18.4,22.6 17.2,22.6 H 6.8 C 5.6,22.6 4.6,21.6 4.6,20.4 Z " +
                                "M 9.2,10.8 V 20 H 11 V 10.8 Z M 13,10.8 V 20 H 14.8 V 10.8 Z";

    public const string Save = "M 2.6,2.6 H 16.4 L 21.4,7.6 V 21.4 H 2.6 Z M 6,2.6 H 14 V 8.6 H 6 Z M 5.6,13 H 18.4 V 20.6 H 5.6 Z";

    public const string Close = "M 5.6,3.4 L 12,9.8 L 18.4,3.4 L 20.6,5.6 L 14.2,12 L 20.6,18.4 L 18.4,20.6 L 12,14.2 L 5.6,20.6 L 3.4,18.4 L 9.8,12 L 3.4,5.6 Z";

    /// <summary>菜单（三横）。</summary>
    public const string Menu = "M 2.6,5 H 21.4 V 7.8 H 2.6 Z M 2.6,10.6 H 21.4 V 13.4 H 2.6 Z M 2.6,16.2 H 21.4 V 19 H 2.6 Z";

    public const string ClickThrough = "M 6.4,1.6 L 17.6,12.8 H 11.6 L 14.8,20.4 L 12.2,21.5 L 9,13.9 L 6.4,16.9 Z";

    public const string DesktopPen = "M 6.2,9.2 L 15.3,18.3 L 20,23 L 21.6,21.4 L 16.9,16.7 L 7.8,7.6 Z " +
                                     "M 5,10.4 L 7.8,7.6 L 4.4,4.2 C 3.9,3.7 3.1,3.7 2.6,4.2 L 2.6,4.2 C 2.1,4.7 2.1,5.5 2.6,6 Z";

    /// <summary>小三角：表示该按钮有二级菜单。</summary>
    public const string ChevronDownSmall = "M 8,11 L 16,11 L 12,16 Z";
}
