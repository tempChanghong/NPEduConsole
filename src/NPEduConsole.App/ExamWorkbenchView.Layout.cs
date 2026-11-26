using Avalonia;
using Avalonia.Controls;

namespace NPEduConsole.App;

public sealed partial class ExamWorkbenchView
{
    private const double CompactWidth = 880;
    private bool _compact, _layoutReady, _showPlan, _showChecks;

    private void WireExamLayout()
    {
        ExamIdentityToggle.Click += (_, _) => ShowFullIdentity(!ExamFullIdentityText.IsVisible);
        ExamStepStatusButton.Click += (_, _) => SelectExamStep(showPlan: false);
        ExamStepPlanButton.Click += (_, _) => SelectExamStep(showPlan: true);
        ExamWidePlanButton.Click += (_, _) => SelectExamStep(!_showPlan);
        ExamNarrowChecksButton.Click += (_, _) =>
        {
            _showChecks = !_showChecks;
            UpdateExamSteps();
            if (_showChecks) ExamRightSection.BringIntoView();
        };
    }

    private void ArrangeExamWorkspace()
    {
        if (Bounds.Width <= 0) return;
        var compact = Bounds.Width < CompactWidth;
        if (_layoutReady && _compact == compact) return;
        _layoutReady = true;
        _compact = compact;
        ExamWorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 1 : 1.15, GridUnitType.Star);
        ExamWorkspaceGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(.85, GridUnitType.Star);
        ExamWorkspaceGrid.ColumnSpacing = compact ? 0 : 12;
        Grid.SetColumn(ExamRightSection, compact ? 0 : 1);
        Grid.SetRow(ExamRightSection, compact ? 1 : 0);
        UpdateExamSteps();
    }

    private void ShowFullIdentity(bool show)
    {
        ExamFullIdentityText.IsVisible = show;
        ExamIdentityToggle.Content = show ? "收起编号" : "编号";
    }

    private void ResetExamSteps()
    {
        ShowFullIdentity(false);
        _showPlan = _showChecks = false;
        ExamScroll.Offset = default;
        UpdateExamSteps();
    }

    private void SelectExamStep(bool showPlan)
    {
        if (_showPlan == showPlan) return;
        _showPlan = showPlan;
        if (_compact) ExamScroll.Offset = default;
        Render();
    }

    private void UpdateExamSteps()
    {
        ExamStepBar.IsVisible = _compact;
        ExamStepPlanButton.IsEnabled = _scope is not null;
        ExamStepStatusButton.Classes.Set("selected", !_showPlan);
        ExamStepPlanButton.Classes.Set("selected", _showPlan);
        ExamStatusCard.IsVisible = !_compact || !_showPlan;
        ExamRightSection.IsVisible = !_compact || !_showPlan && _showChecks;
        ExamNarrowChecksButton.IsVisible = _compact && !_showPlan && _scope is not null;
        ExamNarrowChecksButton.Content = _showChecks ? "收起环境核对" : "考前核对与环境影响";
        ExamWidePlanButton.IsVisible = !_compact && _scope is not null;
        ExamWidePlanButton.Content = _showPlan ? "收起方案与放映" : "方案与放映（可选）";
        ExamPlanSection.IsVisible = _showPlan;
    }
}
