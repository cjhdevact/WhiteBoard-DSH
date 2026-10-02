using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using WhiteBoard.ViewModels;

namespace WhiteBoard.Controls;

/// <summary>
/// 把 <see cref="ToolBarViewModel"/> 填充到 FluentAvalonia 的 <see cref="CommandBar"/> 上。
///
/// 为什么用静态方法而不是继承 CommandBar：
/// FluentAvalonia 的主题样式选择器精确匹配 <c>CommandBar</c> 类型，
/// 子类拿不到它的 ControlTemplate（模板里的 MoreButton 等部件缺失），
/// 会在 AttachItems 时抛 NullReferenceException。
///
/// 按钮类型对应 WinUI：
///   AppBarButton       → CommandBarButton
///   AppBarToggleButton → CommandBarToggleButton
///
/// 选中态的关键设计：
/// <c>IsChecked</c> 用 OneWay 绑到视图模型，点击时不去改它，
/// 而是执行命令让视图模型统一刷新整个工具条。
/// 这样「点橡皮 → 笔自动取消选中」永远不会错位，
/// 也不会出现「自己把自己点反」的问题。
/// </summary>
public static class FluentToolBarBuilder
{
    /// <summary>由外部保存 / 恢复的当前打开的子菜单（同一时刻只允许一个）。</summary>
    private static FlyoutBase? _openFlyout;

    public static void Attach(
        CommandBar bar,
        ToolBarViewModel viewModel,
        ICommand? toolCommand,
        ICommand? actionCommand)
    {
        bar.DefaultLabelPosition = CommandBarDefaultLabelPosition.Bottom;
        bar.IsDynamicOverflowEnabled = true;
        bar.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;


        void Rebuild()
        {
            CloseOpenFlyout();
            bar.PrimaryCommands.Clear();

            foreach (var item in viewModel.Items)
            {
                if (item.IsSeparator)
                {
                    bar.PrimaryCommands.Add(new CommandBarSeparator());
                    continue;
                }

                bar.PrimaryCommands.Add(CreateButton(item, toolCommand, actionCommand));
            }
        }

        Rebuild();

        // 工具变化时刷新所有按钮的选中态（含二级菜单打勾）
        viewModel.PropertyChanged += (_, _) => SyncState(bar, viewModel);

        viewModel.Items.CollectionChanged += (_, _) =>
        {
            Rebuild();
            SyncState(bar, viewModel);
        };
    }

    /// <summary>把视图模型的选中态 / 可用态同步到已经生成的按钮上。</summary>
    private static void SyncState(CommandBar bar, ToolBarViewModel viewModel)
    {
        foreach (var command in bar.PrimaryCommands)
        {
            if (command is not Control control)
                continue;

            var id = control switch
            {
                CommandBarToggleButton t => t.CommandParameter as string,
                CommandBarButton b => b.CommandParameter as string,
                _ => null
            };

            if (id is null)
                continue;

            var item = viewModel.AllItems.FirstOrDefault(i => i.Id == id);
            if (item is null)
                continue;

            switch (control)
            {
                case CommandBarToggleButton toggle:
                    toggle.IsChecked = item.IsChecked;
                    toggle.IsEnabled = item.IsEnabled;
                    break;

                case CommandBarButton button:
                    button.IsEnabled = item.IsEnabled;
                    break;
            }
        }

        // 二级菜单里的工具也要刷新打勾状态
        if (_openFlyout is not null)
            RefreshSubmenuChecks(_openFlyout, viewModel);
    }

    private static ICommandBarElement CreateButton(
        ToolBarItem item, ICommand? toolCommand, ICommand? actionCommand)
    {
        // 约定：__ 开头的是「动作」，其余是「切换工具」
        var isAction = item.Id.StartsWith("__", StringComparison.Ordinal);
        var command = isAction ? actionCommand : toolCommand;
        var icon = CreateIcon(item);

        if (item.IsToggle)
        {
            var toggle = new CommandBarToggleButton
            {
                Label = item.Label,
                IconSource = icon,
                CommandParameter = item.Id,
                IsChecked = item.IsChecked,
                IsEnabled = item.IsEnabled
            };

            UseOwnTemplate(toggle, "WhiteBoardCommandBarToggleButtonTheme");

            // OneWay：点击不再自行翻转，统一由视图模型刷新，避免状态错位
            toggle.Bind(CommandBarToggleButton.IsCheckedProperty,
                new Binding(nameof(ToolBarItem.IsChecked))
                {
                    Source = item,
                    Mode = BindingMode.OneWay
                });

            toggle.Bind(InputElement.IsEnabledProperty,
                new Binding(nameof(ToolBarItem.IsEnabled))
                {
                    Source = item,
                    Mode = BindingMode.OneWay
                });

            if (item.HasSubmenu)
            {
                AttachSubmenu(toggle, item, toolCommand);
            }
            else
            {
                // 普通开关按钮：点一下切换工具
                toggle.Click += (_, _) =>
                {
                    if (command?.CanExecute(item.Id) == true)
                        command.Execute(item.Id);
                };
            }

            return toggle;
        }

        var button = new CommandBarButton
        {
            Label = item.Label,
            IconSource = icon,
            Command = command,
            CommandParameter = item.Id,
            IsEnabled = item.IsEnabled
        };

        UseOwnTemplate(button, "WhiteBoardCommandBarButtonTheme");

        button.Bind(InputElement.IsEnabledProperty,
            new Binding(nameof(ToolBarItem.IsEnabled))
            {
                Source = item,
                Mode = BindingMode.OneWay
            });

        if (item.HasSubmenu)
            AttachSubmenu(button, item, toolCommand);

        return button;
    }

    /// <summary>
    /// 换成工程自带的按钮模板。
    /// FluentAvalonia 的默认模板会把标签 TextBlock 压成 0×0（可视树可验证），
    /// 结果就是「工具条没有文字说明」；这里用 AppStyles.axaml 里定义的模板覆盖它。
    /// </summary>
    private static void UseOwnTemplate(TemplatedControl control, string themeKey)
    {
        control.Theme = null;

        if (Application.Current is not null
            && Application.Current.TryFindResource(themeKey, out var theme)
            && theme is ControlTheme controlTheme)
        {
            control.Theme = controlTheme;
        }
    }

    /// <summary>
    /// 给按钮挂二级菜单。
    /// 交互（按需求）：
    ///   • 第一次点：切到该家族的默认工具，并展开二级菜单；
    ///   • 再点一次：收起二级菜单，**模式与选中态都不变**；
    ///   • 在二级菜单里选别的工具：切过去，一级按钮仍然保持选中。
    /// </summary>
    private static void AttachSubmenu(Button button, ToolBarItem item, ICommand? toolCommand)
    {
        var flyout = new MenuFlyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft
        };

        foreach (var sub in item.SubmenuItems)
        {
            var menuItem = new MenuItem
            {
                Header = sub.Label,
                CommandParameter = sub.Id,
                Tag = sub
            };

            menuItem.Click += (_, _) =>
            {
                if (toolCommand?.CanExecute(sub.Id) == true)
                    toolCommand.Execute(sub.Id);

                flyout.Hide();
            };

            flyout.Items.Add(menuItem);
        }

        // 收起时同步状态，让外部知道当前没有展开的菜单
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openFlyout, flyout))
                _openFlyout = null;

            button.Classes.Remove("submenuOpen");
        };

        button.Click += (_, _) =>
        {
            // 已经展开 → 收起，模式保持不变
            if (ReferenceEquals(_openFlyout, flyout))
            {
                flyout.Hide();
                return;
            }

            // 保证家族默认工具处于激活状态（已是本家族则不改变）
            if (toolCommand?.CanExecute(item.Id) == true)
                toolCommand.Execute(item.Id);

            CloseOpenFlyout();
            _openFlyout = flyout;
            button.Classes.Add("submenuOpen");
            flyout.ShowAt(button);
        };

        // 二级菜单的「有下级」小三角
        ToolTip.SetTip(button, $"{item.Label}（点击展开可选类型）");
    }

    private static void CloseOpenFlyout()
    {
        var flyout = _openFlyout;
        _openFlyout = null;

        try
        {
            flyout?.Hide();
        }
        catch
        {
            // ignore
        }
    }

    private static void RefreshSubmenuChecks(FlyoutBase flyout, ToolBarViewModel viewModel)
    {
        if (flyout is not MenuFlyout menu)
            return;

        foreach (var obj in menu.Items)
        {
            if (obj is not MenuItem item || item.Tag is not ToolBarItem sub)
                continue;

            // 用「图标区显示勾」的方式表达当前选中的具体工具
            item.Icon = sub.IsChecked
                ? new SymbolIcon { Symbol = Symbol.Accept }
                : null;
        }
    }

    private static IconSource? CreateIcon(ToolBarItem item)
    {
        if (item.IconKind == ToolBarIconKind.Image)
        {
            var image = ImageIconFactory.Create(item.ImageSource, item.TintImage);
            if (image is not null)
                return new ImageIconSource { Source = image };
        }

        if (item.IconKind == ToolBarIconKind.Path && !string.IsNullOrEmpty(item.PathData))
            return new PathIconSource { Data = Geometry.Parse(item.PathData) };

        return Enum.TryParse<Symbol>(item.Symbol, true, out var symbol)
            ? new SymbolIconSource { Symbol = symbol }
            : new SymbolIconSource { Symbol = Symbol.Edit };
    }
}
