using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace NPEduConsole.App;

public sealed partial class ClassSubjectRulesWindow
{
    private bool _compact, _editorDetail, _reviewDetail;
    private string? _pinnedEditorSubject;

    private void Arrange()
    {
        var compact = ClientSize.Width is > 0 and < 900;
        var leavingEditorReturn = !compact && RulesReturnSubjects.IsFocused;
        var leavingReviewReturn = !compact && RulesReturnReviewList.IsFocused;
        if (compact != _compact)
        {
            if (compact)
            {
                if (RulesEditor.IsEffectivelyVisible && (RulesEditorListPane.IsKeyboardFocusWithin || RulesEditorTools.IsKeyboardFocusWithin))
                    _editorDetail = false;
                else if (RulesEditorDetailPane.IsKeyboardFocusWithin) _editorDetail = true;
                if (RulesReviewListPane.IsKeyboardFocusWithin) _reviewDetail = false;
                else if (RulesConfirmBox.IsKeyboardFocusWithin) _reviewDetail = true;
            }
            _compact = compact;
        }
        ApplyPaneLayout();
        if (leavingEditorReturn) FocusList(RulesRows);
        if (leavingReviewReturn) FocusList(RulesReviewRows);
    }

    private void ApplyPaneLayout()
    {
        var editorDetail = _compact && _editorDetail && SelectedId is not null;
        SetColumns(RulesEditorPanes, RulesEditorDetailPane, listWidth: 2, detailWidth: 3);
        RulesEditorTools.IsVisible = RulesEditorListPane.IsVisible = !editorDetail;
        RulesEditorDetailPane.IsVisible = !_compact || editorDetail;
        RulesOpenSubject.IsVisible = _compact;
        RulesOpenSubject.IsEnabled = SelectedId is not null && !_working;
        RulesReturnSubjects.IsVisible = editorDetail;

        var reviewDetail = _compact && _reviewDetail && SelectedReview is not null;
        SetColumns(RulesReview, RulesConfirmBox, listWidth: 3, detailWidth: 2);
        RulesReviewListPane.IsVisible = !reviewDetail;
        RulesConfirmBox.IsVisible = !_compact || reviewDetail;
        RulesOpenReviewSubject.IsVisible = _compact;
        RulesOpenReviewSubject.IsEnabled = SelectedReview is not null && !_working;
        RulesReturnReviewList.IsVisible = reviewDetail;
    }

    private void SetColumns(Grid grid, Control detail, int listWidth, int detailWidth)
    {
        grid.ColumnDefinitions[0].Width = new(_compact ? 1 : listWidth, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = _compact ? new GridLength(0) : new GridLength(detailWidth, GridUnitType.Star);
        grid.ColumnSpacing = _compact ? 0 : 8;
        Grid.SetColumn(detail, _compact ? 0 : 1);
    }

    private void SelectEditorSubject(string id)
    {
        if (!RulesRows.Items.OfType<ClassRuleEditorLine>().Any(row => row.Id == id)) _pinnedEditorSubject = id;
        RefreshRows(id);
    }

    private void EditorListKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || SelectedId is null || _working) return;
        args.Handled = true;
        FocusEditor(reveal: true);
    }

    private void OpenEditorSubject(object? sender, RoutedEventArgs args) => FocusEditor(reveal: true);

    private void EditorSubjectTapped(object? sender, TappedEventArgs args)
    {
        if (!SelectTappedRow(RulesRows, args)) return;
        FocusEditor(reveal: true);
    }

    private void ReturnEditorSubjects(object? sender, RoutedEventArgs args) => FocusEditor();

    private void FocusEditor(bool reveal = false)
    {
        if (_closed) return;
        _editorDetail = reveal;
        ApplyPaneLayout();
        if (!reveal || SelectedId is null) { FocusList(RulesRows); return; }
        RulesEditorScroll.Offset = default;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || !RulesEditorDetailPane.IsEffectivelyVisible) return;
            if (RulesResolveDraft.IsVisible && RulesKeepChoice.Focus(NavigationMethod.Tab)) return;
            if (RulesMode.Focus(NavigationMethod.Tab)) return;
            RulesReturnSubjects.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void OpenReviewSubject(object? sender, RoutedEventArgs args) => FocusReview();

    private void ReviewSubjectTapped(object? sender, TappedEventArgs args)
    {
        if (!SelectTappedRow(RulesReviewRows, args)) return;
        FocusReview();
    }

    private bool SelectTappedRow(ListBox list, TappedEventArgs args)
    {
        if (!_compact || _working || args.Source is not Visual visual ||
            visual.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault() is not { } container ||
            list.ItemFromContainer(container) is not { } row) return false;
        list.SelectedItem = row;
        args.Handled = true;
        return true;
    }

    private void ReturnReviewSubjects(object? sender, RoutedEventArgs args)
    {
        _reviewDetail = false;
        ApplyPaneLayout();
        FocusList(RulesReviewRows);
    }

    private void FocusReview(string? groupId = null)
    {
        _reviewDetail = true;
        ApplyPaneLayout();
        RulesReviewScroll.Offset = default;
        var selected = SelectedReview;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || SelectedReview != selected || !RulesConfirmBox.IsEffectivelyVisible) return;
            var entry = groupId is null ? null : RulesSourceChecks.Children.OfType<CheckBox>()
                .FirstOrDefault(box => Equals(box.Tag, groupId));
            if (entry is not null && entry.Focus(NavigationMethod.Tab)) { entry.BringIntoView(); return; }
            if (RulesOpenConflict.IsEffectivelyVisible && RulesOpenConflict.Focus(NavigationMethod.Tab)) return;
            if (RulesClearCheck.IsEffectivelyVisible && RulesClearCheck.Focus(NavigationMethod.Tab)) return;
            if (RulesReturnReviewList.IsEffectivelyVisible && RulesReturnReviewList.Focus(NavigationMethod.Tab)) return;
            if (RulesSourceChecks.Children.OfType<CheckBox>().FirstOrDefault() is { } first) first.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void FocusList(ListBox list)
    {
        var row = list.SelectedItem;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || !list.IsEffectivelyVisible || !ReferenceEquals(list.SelectedItem, row)) return;
            if (row is null) { list.Focus(NavigationMethod.Tab); return; }
            list.ScrollIntoView(row);
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || !list.IsEffectivelyVisible || !ReferenceEquals(list.SelectedItem, row)) return;
                if (list.ContainerFromItem(row) is Control container && container.Focus(NavigationMethod.Tab)) return;
                list.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private void WindowKeyDown(object? sender, KeyEventArgs args)
    {
        if (_working || _closed) return;
        if (args.Key == Key.F8 && RulesNextUnconfirmed.IsEffectivelyEnabled && RulesNextUnconfirmed.IsEffectivelyVisible)
        {
            args.Handled = true;
            NextUnconfirmed();
            return;
        }
        if (args.Key != Key.Escape || !_compact || RulesMode.IsDropDownOpen || RulesRequirement.IsDropDownOpen) return;
        if (_preview is not null && _reviewDetail)
        {
            args.Handled = true;
            ReturnReviewSubjects(this, args);
        }
        else if (_preview is null && _editorDetail)
        {
            args.Handled = true;
            FocusEditor();
        }
    }
}
