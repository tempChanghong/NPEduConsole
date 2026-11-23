using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed record TeachingTeacherCandidate(TeachingTeacher Teacher, string Detail)
{
    public string AccessibleName => $"{Teacher.Name}，{Detail}";
}

public sealed partial class TeachingView
{
    private Control TeacherChoiceAnchor => InWorkspace ? OpenTeachingTeacherChoice : TeachingTeacherPicker;

    private void MarkDraftChanged()
    {
        if (!InWorkspace || _binding) return;
        _draftChanged = true;
        TeachingKeepDraft.IsVisible = TeachingDiscard.IsVisible = false;
    }

    private void DiscardTeachingClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy) return;
        _draftChanged = false;
        CloseRequested?.Invoke();
    }

    private void ArrangeTeacherChoice()
    {
        if (!InWorkspace) return;
        bool wide = Bounds.Width >= 875;
        bool choosing = TeachingTeacherChoiceLayer.IsVisible;
        TeachingTeacherChoice.HorizontalAlignment = wide ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        TeachingTeacherChoice.Width = wide ? 315 : double.NaN;
        TeachingTeacherChoice.Margin = wide ? new Thickness(0, 12, 15, 12) : new Thickness(12, 6);
        Grid.SetColumn(TeachingTeacherChoiceLayer, wide ? 1 : 0);
        Grid.SetColumnSpan(TeachingTeacherChoiceLayer, wide ? 1 : 2);
        TeachingWorkspaceBody.IsVisible = TeachingWorkspaceActions.IsVisible = !choosing || wide;
        TeachingWorkspaceBody.ColumnDefinitions[0].Width = new GridLength(choosing && wide ? 242 : Bounds.Width < 785 ? 262 : 305);
    }

    private void OpenTeacherChoiceClicked(object? sender, RoutedEventArgs args)
    {
        if (!OpenTeachingTeacherChoice.IsEnabled || _closed) return;
        TeachingTeacherChoiceLayer.IsVisible = true;
        TeachingWorkspaceBody.IsEnabled = TeachingWorkspaceActions.IsEnabled = false;
        ArrangeTeacherChoice();
        TeachingTeacherSearch.Clear();
        FilterTeachers(Teacher?.Id);
        Dispatcher.UIThread.Post(() =>
        {
            if (TeachingTeacherChoiceLayer.IsEffectivelyVisible) TeachingTeacherSearch.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void FilterTeachers(string? selectedId)
    {
        string query = TeachingTeacherSearch.Text?.Trim() ?? "";
        var candidates = TeachingTeacherPicker.Items.OfType<TeachingTeacher>()
            .Select(teacher => new TeachingTeacherCandidate(teacher, teacher.Username is { Length: > 0 } username ? "@" + username : "账号编号 " + teacher.Id))
            .Where(row => row.AccessibleName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        TeachingTeacherCandidates.ItemsSource = candidates;
        TeachingTeacherCandidates.SelectedItem = candidates.FirstOrDefault(row => row.Teacher.Id == selectedId);
        TeachingTeacherEmpty.IsVisible = candidates.Length == 0;
    }

    private void TeacherSearchChanged(object? sender, TextChangedEventArgs args)
    {
        if (TeachingTeacherChoiceLayer.IsVisible)
            FilterTeachers((TeachingTeacherCandidates.SelectedItem as TeachingTeacherCandidate)?.Teacher.Id);
    }

    private void TeacherCandidateChanged(object? sender, SelectionChangedEventArgs args) =>
        UseTeachingTeacher.IsEnabled = TeachingTeacherCandidates.SelectedItem is TeachingTeacherCandidate;

    private void CancelTeacherChoiceClicked(object? sender, RoutedEventArgs args) => CloseTeacherChoice();
    private void UseTeacherClicked(object? sender, RoutedEventArgs args) => CommitTeacherChoice();
    private void TeacherCandidateTapped(object? sender, TappedEventArgs args) => CommitTeacherChoice();

    private void TeacherChoiceKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        CloseTeacherChoice();
        args.Handled = true;
    }

    private void CandidateKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || TeachingTeacherCandidates.SelectedItem is not TeachingTeacherCandidate) return;
        CommitTeacherChoice();
        args.Handled = true;
    }

    private void CommitTeacherChoice()
    {
        if (_closed || TeachingTeacherCandidates.SelectedItem is not TeachingTeacherCandidate selected) return;
        TeachingTeacherPicker.SelectedItem = TeachingTeacherPicker.Items.OfType<TeachingTeacher>()
            .FirstOrDefault(teacher => teacher.Id == selected.Teacher.Id);
        CloseTeacherChoice();
    }

    private void CloseTeacherChoice()
    {
        TeachingTeacherChoiceLayer.IsVisible = false;
        TeachingTeacherCandidates.ItemsSource = null;
        TeachingWorkspaceBody.IsEnabled = TeachingWorkspaceActions.IsEnabled = true;
        ArrangeTeacherChoice();
        if (!_closed) TeacherChoiceAnchor.Focus(NavigationMethod.Tab);
    }
}
