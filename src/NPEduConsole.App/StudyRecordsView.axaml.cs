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

/// <summary>一条自习记录：标题与明细由调用方拼好，照片只给 id。</summary>
public sealed record StudyRecordRow(string Key, string Title, string Details, string? PhotoId);

/// <summary>
/// 自习记录列表与单张照片预览。只做展示：不发网络、不持有照片所有权
/// （调用方负责 Dispose 位图，本控件只在请求关闭时通知 <see cref="PreviewClosed"/>）。
/// </summary>
public sealed partial class StudyRecordsView : UserControl
{
    private const double CompactWidth = 900;

    private IReadOnlyList<StudyRecordRow> _rows = Array.Empty<StudyRecordRow>();
    private bool _canLoadMore;
    private bool _compact;
    private bool _previewOpen;
    private bool _arranged;
    private bool _syncingSelection;
    private StudyRecordRow? _current;

    public StudyRecordsView()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, "自习记录与照片");
        SizeChanged += (_, _) => Arrange();
        Loaded += (_, _) => Arrange(force: true);
    }

    /// <summary>选中某条记录（或键盘确认）时请求调用方加载预览。</summary>
    public event Action<StudyRecordRow>? PreviewRequested;

    public event Action? MoreRequested;
    internal event Action? CompactPreviewChanged;
    internal bool IsCompactPreview => _compact && _previewOpen;
    internal StudyRecordRow? Current => _current;

    /// <summary>窄窗返回、宽窗关闭或记录消失导致预览结束时触发；调用方据此立即释放位图。</summary>
    public event Action? PreviewClosed;

    /// <summary>整体替换列表。按 <c>Key</c> 保留选中项，行内容变化不会重置选择。</summary>
    public void SetRows(IReadOnlyList<StudyRecordRow> rows, bool canLoadMore)
    {
        rows ??= Array.Empty<StudyRecordRow>();
        var selectedKey = SelectedRow()?.Key ?? _current?.Key;
        var changed = !SameRows(_rows, rows) || _canLoadMore != canLoadMore;
        _rows = rows;
        _canLoadMore = canLoadMore;
        if (changed) Rebuild(selectedKey);
        UpdateStatus();
    }

    /// <summary>
    /// 显示一条记录及其照片。位图归调用方所有：本控件只放到 <c>Image.Source</c>，从不 Dispose。
    /// 调用方主动清空（<c>null, null</c>）不会反向触发 <see cref="PreviewClosed"/>。
    /// </summary>
    public void SetPreview(StudyRecordRow? row, IImage? image)
    {
        var focusWasPreview = StudyRecordsPreviewPane.IsKeyboardFocusWithin;
        _current = row;

        StudyRecordsImage.Source = image;
        StudyRecordsPreviewTitle.Text = row?.Title ?? "选择一条记录";
        StudyRecordsPreviewDetails.Text = row?.Details ?? string.Empty;
        StudyRecordsPreviewDetails.IsVisible = row is not null;

        StudyRecordsPhotoEmpty.Text = row is null ? "选择左侧记录查看照片" : "这次记录没有照片";
        StudyRecordsPhotoEmpty.IsVisible = image is null;

        if (row is null)
        {
            // 调用方主动清空（权限失效、换班、丢回包）：窄窗回到列表，且不反向触发 PreviewClosed。
            if (!_previewOpen) return;
            _previewOpen = false;
            ApplyLayout();
            if (focusWasPreview) RestoreRowFocus();
            return;
        }

        if (!_previewOpen)
        {
            _previewOpen = true;
            ApplyLayout();
            if (_compact) FocusPreviewControls();
        }
    }

    /// <summary>关闭预览并触发 <see cref="PreviewClosed"/>；宽窗关闭窗口前也应调用。</summary>
    public void ClosePreview()
    {
        if (!_previewOpen && StudyRecordsImage.Source is null && _current is null) return;
        StudyRecordsImage.Source = null;
        _previewOpen = false;
        ApplyLayout();
        RestoreRowFocus();
        PreviewClosed?.Invoke();
    }

    private void Rebuild(string? selectedKey)
    {
        _syncingSelection = true;
        StudyRecordsList.ItemsSource = _rows;
        _syncingSelection = false;

        var match = selectedKey is null ? null : _rows.FirstOrDefault(row => row.Key == selectedKey);
        if (match is not null)
        {
            _syncingSelection = true;
            StudyRecordsList.SelectedItem = match;
            _syncingSelection = false;
            if (_previewOpen) _current = match;
        }
        else if (_current is not null)
        {
            // 当前记录已经不在了：收起预览，让调用方释放照片。
            var hadPreview = _previewOpen || StudyRecordsImage.Source is not null;
            _current = null;
            StudyRecordsImage.Source = null;
            _previewOpen = false;
            ApplyLayout();
            if (hadPreview) PreviewClosed?.Invoke();
        }
    }

    private void UpdateStatus()
    {
        // 空列表不写「还没有记录」：撤权、读取失败等说明由窗口负责，控件只报计数。
        StudyRecordsStatus.Text = _rows.Count == 0
            ? string.Empty
            : _canLoadMore
                ? $"共 {_rows.Count} 条记录，还有更早的记录。"
                : $"共 {_rows.Count} 条记录。";
        StudyRecordsMore.IsVisible = _canLoadMore;
        SyncStatusVisibility();
    }

    /// <summary>状态行只在有记录、且窄窗预览没有占掉它的时候出现。</summary>
    private void SyncStatusVisibility()
    {
        StudyRecordsStatus.IsVisible = _rows.Count > 0 && !IsCompactPreview;
        StudyRecordsBody.RowSpacing = StudyRecordsStatus.IsVisible ? 7 : 0;
    }

    private StudyRecordRow? SelectedRow() => StudyRecordsList.SelectedItem as StudyRecordRow;

    private static bool SameRows(IReadOnlyList<StudyRecordRow> before, IReadOnlyList<StudyRecordRow> after)
    {
        if (ReferenceEquals(before, after)) return true;
        if (before.Count != after.Count) return false;
        for (var index = 0; index < before.Count; index++)
        {
            var a = before[index];
            var b = after[index];
            if (a.Key != b.Key || a.Title != b.Title || a.Details != b.Details || a.PhotoId != b.PhotoId) return false;
        }

        return true;
    }

    private void RowSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_syncingSelection) return;
        if (SelectedRow() is not { } row) return;
        if (_compact && _previewOpen && row.Key == _current?.Key) return;
        PreviewRequested?.Invoke(row);
    }

    private void RowKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter) return;
        if (SelectedRow() is not { } row) return;
        args.Handled = true;
        PreviewRequested?.Invoke(row);
    }

    private void BackClicked(object? sender, RoutedEventArgs args)
    {
        if (!_previewOpen) return;
        ClosePreview();
    }

    private void MoreClicked(object? sender, RoutedEventArgs args) => MoreRequested?.Invoke();

    private void Arrange(bool force = false)
    {
        var width = Bounds.Width;
        if (double.IsNaN(width) || width <= 0) return;
        var compact = width < CompactWidth;
        if (!force && _arranged && compact == _compact) return;
        var focusList = !compact && IsCompactPreview && StudyRecordsBack.IsFocused;
        var focusPreview = compact && _previewOpen && StudyRecordsList.IsKeyboardFocusWithin;
        _compact = compact;
        _arranged = true;
        ApplyLayout();
        if (focusList) RestoreRowFocus();
        if (focusPreview) FocusPreviewControls();
    }

    private void ApplyLayout()
    {
        var columns = StudyRecordsColumns.ColumnDefinitions;
        if (columns.Count >= 2)
        {
            columns[0].Width = new GridLength(_compact ? 1 : 4, GridUnitType.Star);
            columns[1].Width = _compact ? new GridLength(0) : new GridLength(5, GridUnitType.Star);
        }

        StudyRecordsColumns.ColumnSpacing = _compact ? 0 : 9;
        StudyRecordsListPane.IsVisible = !_compact || !_previewOpen;
        StudyRecordsPreviewPane.IsVisible = !_compact || _previewOpen;
        Grid.SetColumn(StudyRecordsPreviewPane, _compact ? 0 : 1);
        StudyRecordsBack.IsVisible = _compact && _previewOpen;
        SyncStatusVisibility();
        CompactPreviewChanged?.Invoke();
    }

    private void FocusPreviewControls()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Usable(StudyRecordsBack))
            {
                StudyRecordsBack.BringIntoView();
                StudyRecordsBack.Focus(NavigationMethod.Tab);
                return;
            }

            if (Usable(StudyRecordsPreviewPane)) StudyRecordsPreviewPane.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void RestoreRowFocus()
    {
        var row = SelectedRow();
        Dispatcher.UIThread.Post(() =>
        {
            if (IsCompactPreview || !ReferenceEquals(row, SelectedRow())) return;
            if (row is not null) StudyRecordsList.ScrollIntoView(row);
            Dispatcher.UIThread.Post(() =>
            {
                if (IsCompactPreview || !ReferenceEquals(row, SelectedRow()) || !Usable(StudyRecordsList)) return;
                if (row is not null && StudyRecordsList.ContainerFromItem(row) is Control container && Usable(container) &&
                    container.Focus(NavigationMethod.Tab)) return;
                StudyRecordsList.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private static bool Usable(Control? control) =>
        control is not null && control.IsEffectivelyVisible && control.IsEffectivelyEnabled;
}
