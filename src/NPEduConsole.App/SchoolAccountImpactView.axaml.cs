using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace NPEduConsole.App;

/// <summary>
/// 学校管理账号变更的影响确认：标题、范围、影响行与固定操作栏。
/// 只负责展示与两个意图事件；表单、请求状态、关闭时机与 PIN 都由调用方持有。
/// </summary>
public sealed partial class SchoolAccountImpactView : UserControl
{
    private const double NarrowWidth = 640;

    private string[] _consequences = Array.Empty<string>();
    private bool _canConfirm;
    private bool _narrow;
    private bool _arranged;

    public SchoolAccountImpactView()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, "学校管理账号变更影响确认");
        SizeChanged += (_, _) => Arrange();
        Loaded += (_, _) => Arrange(force: true);
    }

    /// <summary>调用方点「确认」；是否真正提交由调用方判断。</summary>
    public event Action? ConfirmRequested;

    /// <summary>调用方点「返回」或按 Esc。</summary>
    public event Action? BackRequested;

    /// <summary>
    /// 展示一次待确认的管理变更。<paramref name="confirmText"/> 是确认按钮文案，
    /// <paramref name="canConfirm"/> 为 false 时按钮禁用但仍可见（便于读出阻断原因）。
    /// </summary>
    public void Present(
        string title,
        string scope,
        IReadOnlyList<string> consequences,
        string confirmText,
        bool canConfirm)
    {
        _consequences = consequences?.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray()
                        ?? Array.Empty<string>();
        _canConfirm = canConfirm;

        SchoolAccountImpactTitle.Text = string.IsNullOrWhiteSpace(title) ? "确认这次管理变更" : title;
        SchoolAccountImpactScope.Text = scope ?? string.Empty;
        SchoolAccountImpactScope.IsVisible = !string.IsNullOrWhiteSpace(scope);

        FillConsequences();
        SchoolAccountImpactEmpty.IsVisible = _consequences.Length == 0;
        SchoolAccountImpactScroll.IsVisible = _consequences.Length > 0;
        SchoolAccountImpactStatus.Text = _consequences.Length == 0
            ? string.Empty
            : $"共 {_consequences.Length} 项影响，请逐条核对。";

        SchoolAccountImpactConfirm.Content = string.IsNullOrWhiteSpace(confirmText) ? "确认变更" : confirmText;
        SchoolAccountImpactConfirm.IsEnabled = canConfirm;

        FocusImpactArea();
    }

    /// <summary>影响行按给定顺序逐条铺开，长文本在窄窗里换行而不是横向溢出。</summary>
    private void FillConsequences()
    {
        SchoolAccountImpactItems.Children.Clear();
        foreach (var line in _consequences)
        {
            var text = new TextBlock
            {
                Text = line,
                TextWrapping = TextWrapping.Wrap,
                Classes = { "impact-line" },
            };
            var row = new Border
            {
                Classes = { "impact-row" },
                Child = text,
            };
            AutomationProperties.SetName(row, line);
            SchoolAccountImpactItems.Children.Add(row);
        }
    }

    /// <summary>把焦点移到确认按钮（调用方确认可以提交时使用）。</summary>
    public void FocusConfirm()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!Usable(SchoolAccountImpactConfirm)) return;
            SchoolAccountImpactConfirm.BringIntoView();
            SchoolAccountImpactConfirm.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void FocusImpactArea()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 默认落在可滚动的清单上：回车不会误触发确认，Tab 才到按钮。
            if (Usable(SchoolAccountImpactScroll))
            {
                SchoolAccountImpactScroll.Focus(NavigationMethod.Tab);
                return;
            }

            if (Usable(SchoolAccountImpactBack)) SchoolAccountImpactBack.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void Arrange(bool force = false)
    {
        var width = Bounds.Width;
        if (double.IsNaN(width) || width <= 0) return;
        var narrow = width < NarrowWidth;
        if (!force && _arranged && narrow == _narrow) return;
        _narrow = narrow;
        _arranged = true;
        ApplyActions();
    }

    private void ApplyActions()
    {
        var columns = SchoolAccountImpactActions.ColumnDefinitions;
        if (columns.Count < 3) return;

        if (_narrow)
        {
            // 窄窗：两颗按钮各占一半，确认在右，避免被挤出可视区。
            columns[0].Width = new GridLength(1, GridUnitType.Star);
            columns[1].Width = new GridLength(1, GridUnitType.Star);
            columns[2].Width = new GridLength(0);
            Grid.SetColumn(SchoolAccountImpactBack, 0);
            Grid.SetColumn(SchoolAccountImpactConfirm, 1);
            SchoolAccountImpactBack.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            SchoolAccountImpactConfirm.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            SchoolAccountImpactConfirm.MinWidth = 0;
            return;
        }

        columns[0].Width = new GridLength(1, GridUnitType.Star);
        columns[1].Width = GridLength.Auto;
        columns[2].Width = GridLength.Auto;
        Grid.SetColumn(SchoolAccountImpactBack, 1);
        Grid.SetColumn(SchoolAccountImpactConfirm, 2);
        SchoolAccountImpactBack.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        SchoolAccountImpactConfirm.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        SchoolAccountImpactConfirm.MinWidth = 120;
    }

    private void ConfirmClicked(object? sender, RoutedEventArgs args)
    {
        if (!_canConfirm) return;
        ConfirmRequested?.Invoke();
    }

    private void ViewKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        args.Handled = true;
        BackRequested?.Invoke();
    }

    private void BackClicked(object? sender, RoutedEventArgs args) => BackRequested?.Invoke();

    private static bool Usable(Control? control) =>
        control is not null && control.IsEffectivelyVisible && control.IsEffectivelyEnabled;
}
