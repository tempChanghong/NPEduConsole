using System.Runtime.CompilerServices;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TeachingView
{
    private static readonly ConditionalWeakTable<ConsoleClient,
        Dictionary<(string School, string Term, string Workspace, string Subject), TeachingDraft>> PausedDrafts = new();

    private void PauseTeachingClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || Draft() is not { } draft) return;
        PausedDrafts.GetOrCreateValue(_client)[(_schoolId, _termId, _classId, draft.SubjectId)] = draft;
        _draftChanged = false;
        CloseRequested?.Invoke();
    }

    private void RestorePausedDraft()
    {
        if (Subject is not { } subject || !PausedDrafts.TryGetValue(_client, out var drafts) ||
            !drafts.Remove((_schoolId, _termId, _classId, subject.Id), out var draft)) return;
        var original = TeachingExistingPicker.Items.OfType<TeachingAssignment>().FirstOrDefault(row => row.Id == draft.AssignmentId);
        if (draft.AssignmentId is not null && original is null)
        {
            TeachingFeedback.Text = "原草稿对应的任课关系已变化。请选择当前关系后重新办理。";
            return;
        }
        _binding = true;
        if (original is not null) TeachingExistingPicker.SelectedItem = original;
        SetOperation(draft.Action, original);
        TeachingTeacherPicker.SelectedItem = TeachingTeacherPicker.Items.OfType<TeachingTeacher>()
            .FirstOrDefault(row => row.Id == draft.AccountId);
        TeachingPositionPicker.SelectedIndex = draft.Position == "CO_TEACHER" ? 1 : 0;
        foreach (var id in draft.ClassIds)
            if (_choices.TryGetValue(id, out var check)) check.IsChecked = true;
        _binding = false;
        _draftChanged = true;
        UpdateControls();
        TeachingFeedback.Text = (draft.AccountId is not null && Teacher is null || draft.ClassIds.Any(id => id != _classId && !_choices.ContainsKey(id)))
            ? "已恢复仍可用的草稿选择。原教师或部分班级已不可选，请核对后重新预览。"
            : "已恢复本班本科目的草稿，请重新预览当前安排。";
    }

    internal static void ForgetPausedDrafts(ConsoleClient client, School school)
    {
        if (!PausedDrafts.TryGetValue(client, out var drafts)) return;
        foreach (var key in drafts.Keys.Where(key => key.School == school.Id).ToArray()) drafts.Remove(key);
    }
}
