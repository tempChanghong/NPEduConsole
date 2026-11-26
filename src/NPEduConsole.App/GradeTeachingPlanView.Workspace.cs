using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NPEduConsole.App;

public sealed partial class GradeTeachingPlanView
{
    private object? _workspaceEditTab;

    private void ArrangeWorkspaceReview()
    {
        bool review = _compact && _workspaceReview;
        bool showPreview = review && !_workspaceDraftList && _preview is not null && _reviewContent is not null;
        Control? content = !review ? null : showPreview ? _reviewContent : PlanChangeCard;
        if (PlanWorkspaceReviewHost.Child != content)
        {
            PlanWorkspaceReviewHost.Child = null;
            if (showPreview) PlanReviewTab.Content = null;
            if (content == PlanChangeCard) PlanWideChanges.Child = null;
            PlanWorkspaceReviewHost.Child = content;
        }
        if (!showPreview && PlanReviewTab.Content is null) PlanReviewTab.Content = _reviewContent;
        if (content != PlanChangeCard && PlanWideChanges.Child is null) PlanWideChanges.Child = PlanChangeCard;
        PlanWorkspaceReviewHost.IsVisible = review;
        PlanColumns.IsVisible = !review;
        PlanWideChanges.IsVisible = !_compact;
    }

    private void RenderWorkspaceReview()
    {
        if (!_inWorkspace) return;
        bool review = _compact && _workspaceReview;
        PlanWorkspaceStep.IsVisible = _compact;
        PlanWorkspaceStep.Text = review ? "第 2 步 · 核对整份方案" : "第 1 步 · 编辑方案";
        PlanWorkspaceChanges.IsVisible = _compact && HasDraft && (!review || !_workspaceDraftList);
        PlanWorkspaceChanges.Content = $"本次变更（{_rules.Count + _groups.Count + _assignments.Count}）";
        PlanWorkspaceChanges.IsEnabled = !_busy;
        PlanWorkspaceEdit.IsVisible = review && Pending is null;
        PlanWorkspaceEdit.IsEnabled = !_busy;
        PlanWorkspaceLocate.IsVisible = review && !_workspaceDraftList && _preview is not null && !_confirmedPreview && Pending is null;
        PlanWorkspaceLocate.IsEnabled = !_busy;
        PlanBack.IsVisible = !PlanWorkspaceEdit.IsVisible;
        PlanSave.IsVisible = !_compact || review && !_workspaceDraftList && _confirmedPreview || Pending is not null && _replay;
        PlanPreview.IsVisible = !_compact || !review || _workspaceDraftList || !_confirmedPreview && Pending is null;
        PlanPreview.Classes.Set("accent", _compact && Pending is null);
        if (_compact && _previousPreview is not null && !_confirmedPreview) PlanPreview.Content = "重新预览";
        else if (_specifiedTermId is null) PlanPreview.Content = "预览整份方案";
    }

    private void OpenWorkspaceChanges(object? sender, RoutedEventArgs args)
    {
        if (!_inWorkspace || !_compact || !HasDraft || _busy) return;
        _workspaceReview = _workspaceDraftList = true;
        ArrangeWorkspaceReview();
        Render();
        Focus(PlanChangeRows);
    }

    private void ReturnWorkspaceEdit(object? sender, RoutedEventArgs args)
    {
        if (_busy || Pending is not null) return;
        LeaveWorkspaceReview();
        Render();
        Focus(PlanTabs);
    }

    private void LeaveWorkspaceReview()
    {
        if (!_inWorkspace || !_workspaceReview) return;
        _workspaceReview = _workspaceDraftList = false;
        ArrangeWorkspaceReview();
        if (PlanTabs.SelectedItem == PlanReviewTab)
            PlanTabs.SelectedItem = _workspaceEditTab ?? PlanTabs.Items[0];
    }
}
