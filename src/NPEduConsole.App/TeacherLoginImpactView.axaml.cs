using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

/// <summary>
/// 教师登录方式变更的影响摘要：账号与邀请一张表、可筛选、可精确跳到某一行。
/// 只负责展示与定位，不保存、不请求；列表在自己的空间里滚动。
/// </summary>
public sealed partial class TeacherLoginImpactView : UserControl
{
    private const int FilterAll = 0;
    private const int FilterAccounts = 1;
    private const int FilterInvitations = 2;
    private const int FilterAttention = 3;

    private TeacherLoginImpact? _impact;
    private bool _canOpen;
    private bool _filling;
    private string? _focusedKey;
    private List<TeacherLoginImpactRow> _shown = new();

    /// <summary>打开某一行时触发（是否真的能打开由主窗决定）。</summary>
    public event Action<TeacherLoginImpactRow>? TargetRequested;

    public TeacherLoginImpactView()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, "教师登录方式的影响");
        TeacherLoginImpactFilter.ItemsSource = new[] { "全部", "账号", "邀请", "需处理" };
        _filling = true;
        TeacherLoginImpactFilter.SelectedIndex = FilterAll;
        _filling = false;
        TeacherLoginImpactRows.SelectionChanged += (_, _) => RememberSelection();
    }

    /// <summary>渲染一份影响预览；<paramref name="canOpen"/> 为假时只展示、不响应回车与按钮。</summary>
    public void Present(TeacherLoginImpact? impact, bool canOpen)
    {
        _impact = impact;
        _canOpen = canOpen;
        Render(_focusedKey);
    }

    /// <summary>把某个账号或邀请定位到可见并聚焦；找不到返回 false。</summary>
    public bool FocusTarget(string kind, string id)
    {
        if (_impact is null) return false;
        var row = _impact.Rows.FirstOrDefault(item =>
            string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase) && item.Id == id);
        if (row is null) return false;

        if (!_shown.Contains(row))
        {
            _filling = true;
            TeacherLoginImpactFilter.SelectedIndex = FilterAll;
            _filling = false;
            Render(KeyOf(row));
        }
        else
        {
            TeacherLoginImpactRows.SelectedItem = row;
            FocusRow(row);
        }
        return true;
    }

    private void Render(string? focusKey)
    {
        var rows = _impact?.Rows ?? Array.Empty<TeacherLoginImpactRow>();
        _shown = FilterRows(rows);

        TeacherLoginImpactCounts.Text = _impact is null
            ? "还没有可预览的影响。"
            : _impact.Rows.Count == 0
                ? "没有查到启用的普通教师账号或待认领邀请。"
                : $"{_impact.AccountCount} 个账号、{_impact.InvitationCount} 项邀请" +
                  (_impact.NeedsAttentionCount > 0 ? $"；{_impact.NeedsAttentionCount} 项需要处理。" : "。");

        TeacherLoginImpactStatus.Text = _impact is null || _impact.Rows.Count == 0
            ? string.Empty
            : !_canOpen
                ? "当前只是预览，打开具体账号或邀请需要先确认这份变更。"
                : string.Empty;

        _filling = true;
        TeacherLoginImpactRows.ItemsSource = _shown;
        var target = focusKey is null ? null : _shown.FirstOrDefault(row => KeyOf(row) == focusKey);
        TeacherLoginImpactRows.SelectedItem = target ?? _shown.FirstOrDefault();
        _filling = false;
        RememberSelection();

        TeacherLoginImpactEmpty.IsVisible = _shown.Count == 0;
        TeacherLoginImpactEmpty.Text = _impact is null
            ? "还没有可预览的影响。"
            : _impact.Rows.Count == 0
                ? "这次变更不会改变任何老师的登录方式或访问范围。"
                : "当前筛选下没有需要核对的对象。";

        TeacherLoginImpactHint.IsVisible = _canOpen && _shown.Count > 0;
        UpdateOpenButton();

        if (target is not null) FocusRow(target);
    }

    private List<TeacherLoginImpactRow> FilterRows(IReadOnlyList<TeacherLoginImpactRow> rows)
    {
        var index = TeacherLoginImpactFilter.SelectedIndex;
        return index switch
        {
            FilterAccounts => rows.Where(row => IsKind(row, "ACCOUNT")).ToList(),
            FilterInvitations => rows.Where(row => IsKind(row, "INVITATION")).ToList(),
            FilterAttention => rows.Where(row => row.NeedsAttention).ToList(),
            _ => rows.ToList()
        };
    }

    private static bool IsKind(TeacherLoginImpactRow row, string kind) =>
        string.Equals(row.Kind, kind, StringComparison.OrdinalIgnoreCase);

    private static string KeyOf(TeacherLoginImpactRow row) => row.Kind + ":" + row.Id;

    private TeacherLoginImpactRow? SelectedRow =>
        TeacherLoginImpactRows.SelectedItem as TeacherLoginImpactRow;

    private bool CanOpenRow(TeacherLoginImpactRow? row) =>
        _canOpen && row is not null && !string.IsNullOrEmpty(row.Action);

    private void RememberSelection()
    {
        if (_filling) return;
        if (SelectedRow is { } row) _focusedKey = KeyOf(row);
        UpdateOpenButton();
    }

    private void UpdateOpenButton()
    {
        var row = SelectedRow;
        TeacherLoginImpactOpen.IsEnabled = CanOpenRow(row);
        TeacherLoginImpactOpen.Content = row is not null && IsKind(row, "INVITATION")
            ? "打开选中的邀请"
            : "打开选中的账号";
    }

    private void FilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_filling || _impact is null) return;
        Render(_focusedKey);
    }

    private void RowsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (!OpenSelected()) return;
        e.Handled = true;
    }

    private void RowsDoubleTapped(object? sender, TappedEventArgs e)
    {
        OpenSelected();
    }

    private void OpenClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenSelected();
    }

    private bool OpenSelected()
    {
        var row = SelectedRow;
        if (!CanOpenRow(row)) return false;
        TargetRequested?.Invoke(row!);
        return true;
    }

    private void FocusRow(TeacherLoginImpactRow row)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (TeacherLoginImpactRows.ContainerFromItem(row) is Control container)
            {
                container.BringIntoView();
                container.Focus(NavigationMethod.Tab);
            }
            else
            {
                TeacherLoginImpactRows.Focus(NavigationMethod.Tab);
            }
        }, DispatcherPriority.Loaded);
    }
}
