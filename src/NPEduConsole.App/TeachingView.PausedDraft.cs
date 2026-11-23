using System.Runtime.CompilerServices;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TeachingView
{
    private static readonly ConditionalWeakTable<ConsoleClient,
        Dictionary<(string School, string Term, string Workspace, string Subject, string? EntryAccount), TeachingDraft>> PausedDrafts = new();

    private void PauseTeachingClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || _submitting || _unconfirmed is not null || !_draftChanged || Subject is not { } subject) return;
        SavePausedDraft(subject.Id);
        _draftChanged = false;
        CloseRequested?.Invoke();
    }

    private void SavePausedDraft(string subjectId) =>
        PausedDrafts.GetOrCreateValue(_client)[(_schoolId, _termId, _classId, subjectId, _entryAccountId)] = CaptureDraft(subjectId);

    private void RestorePausedDraft()
    {
        if (Subject is not { } subject || !PausedDrafts.TryGetValue(_client, out var drafts) ||
            !drafts.Remove((_schoolId, _termId, _classId, subject.Id, _entryAccountId), out var draft)) return;
        if (!RestoreDraft(draft))
        {
            TeachingFeedback.Text = "原草稿对应的任课关系已变化。请选择当前关系后重新办理。";
            return;
        }
        TeachingFeedback.Text = (draft.AccountId is not null && Teacher is null || draft.ClassIds.Any(id => id != _classId && !_choices.ContainsKey(id)))
            ? "已恢复仍可用的草稿选择。原教师或部分班级已不可选，请核对后重新预览。"
            : "已恢复本班本科目的草稿，请重新预览当前安排。";
    }

    private bool RestoreDraft(TeachingDraft draft)
    {
        var original = TeachingExistingPicker.Items.OfType<TeachingAssignment>().FirstOrDefault(row => row.Id == draft.AssignmentId);
        if (draft.AssignmentId is not null && original is null) return false;
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
        return true;
    }

    internal static void ForgetSchool(ConsoleClient client, School school)
    {
        if (PausedDrafts.TryGetValue(client, out var drafts))
            foreach (var key in drafts.Keys.Where(key => key.School == school.Id).ToArray()) drafts.Remove(key);
        if (PendingBySession.TryGetValue(client, out var pending))
            foreach (var key in pending.Keys.Where(key => key.School == school.Id).ToArray()) pending.Remove(key);
    }

    internal static void EndSession(ConsoleClient client)
    {
        PausedDrafts.Remove(client);
        PendingBySession.Remove(client);
    }
}
