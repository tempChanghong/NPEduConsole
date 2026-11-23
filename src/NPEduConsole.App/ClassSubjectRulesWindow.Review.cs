using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed class ClassRuleReviewLine(string? subjectId, string title, string before, string after, bool hasIssue)
    : INotifyPropertyChanged
{
    public string? SubjectId { get; } = subjectId;
    public string Title { get; } = title;
    public string Before { get; } = before;
    public string After { get; } = after;
    public bool HasIssue { get; } = hasIssue;
    public bool IsSubject => SubjectId is not null;
    public string Status { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status;
        PropertyChanged?.Invoke(this, new(nameof(Status)));
    }
}

public sealed partial class ClassSubjectRulesWindow
{
    private ClassRuleChoice[] Choices => _baseline!.Rows.Select(row => _draft[row.SubjectId]).ToArray();
    private ClassRuleReviewLine? SelectedReview => RulesReviewRows.SelectedItem as ClassRuleReviewLine;
    private bool ShouldConfirmClear => _preview?.ClearsClass == true && RulesClearCheck.IsChecked != true;
    private bool CanConfirmReview => CanEdit && _preview?.CanApply == true && _sourceReview is {RemainingCount: 0} && !ShouldConfirmClear;

    private async Task PreviewAsync()
    {
        if (_baseline is not { } baseline || _overlaps.Count > 0 || Pending is not null) return;
        var observed = await _client.PreviewClassRulesAsync(baseline, Choices, _archiveEditable, _lifetime.Token);
        if (_closed) return;
        if (observed.State == "STALE")
        {
            Stale(observed.Current);
            return;
        }
        ShowPreview(observed);
    }

    private void Stale(ClassRuleSnapshot current)
    {
        Merge(current);
        Notice("这份预览已过期，本次零写入。请核对最新安排，保留的草稿需要重新预览。", true);
        FocusEditor(reveal: _overlaps.Count > 0 || _differences.Count > 0);
    }

    private void ShowPreview(ClassRulePreview preview)
    {
        DropReview();
        _preview = preview;
        _sourceReview = new(preview);
        var changes = preview.Changes.ToDictionary(row => row.SubjectId);
        var blocked = preview.Issues.Select(issue => issue.SubjectId).ToHashSet();
        var relevant = changes.Keys.Concat(blocked).Concat(preview.Notices.Select(issue => issue.SubjectId))
            .Concat(preview.Removals.Select(row => row.SubjectId)).ToHashSet();
        var rows = preview.Current.Rows.Where(row => relevant.Contains(row.SubjectId)).Select(row =>
            new ClassRuleReviewLine(row.SubjectId, $"{row.Name}（{row.Code}）", Describe(row.Choice),
                Describe(changes.GetValueOrDefault(row.SubjectId) ?? row.Choice), blocked.Contains(row.SubjectId))).ToList();
        if (preview.ClearsClass) rows.Add(new(null, "整班清空", "", "", false));
        var firstBlock = preview.Issues.FirstOrDefault();
        RulesReviewRows.ItemsSource = rows;
        RulesReviewRows.SelectedItem = rows.FirstOrDefault(row => row.SubjectId == (firstBlock?.SubjectId ?? SelectedId)) ?? rows.FirstOrDefault();
        Render();
        if (preview.Issues.Length > 0)
            Notice($"有 {blocked.Count} 科需先处理。", true);
        else if (preview.Removals.Length > 0)
            Notice("请在对应科目核对将解除的来源。可用 F8 定位下个未核对项。");
        else if (preview.ClearsClass)
            Notice("整班清空需要单独确认。可用 F8 定位确认项。");
        else Notice("请核对逐科调整，再确认保存。");
        if (_compact && firstBlock is not null) FocusReview();
        else FocusList(RulesReviewRows);
    }

    private void DropReview()
    {
        _preview = null;
        _sourceReview = null;
        _reviewDetail = false;
        _filling = true;
        RulesReviewRows.ItemsSource = null;
        RulesSourceChecks.Children.Clear();
        RulesClearCheck.IsChecked = false;
        _filling = false;
    }

    private void ReviewRowChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_client is null || _filling) return;
        RenderReviewDetail();
        ApplyPaneLayout();
    }

    private ClassRuleIssue? SelectedIssue => _preview?.Issues.Concat(_preview.Notices)
        .FirstOrDefault(issue => issue.SubjectId == SelectedReview?.SubjectId && issue.Action is "TEACHING" or "COURSE_GROUPS");

    private void RenderReviewDetail()
    {
        RulesSourceChecks.Children.Clear();
        RulesReviewScroll.Offset = default;
        var line = SelectedReview;
        if (_preview is not { } preview || _sourceReview is not { } review || line is null) return;
        RulesReviewTitle.Text = line.Title;
        RulesReviewValues.IsVisible = line.IsSubject;
        RulesReviewBefore.Text = line.Before;
        RulesReviewAfter.Text = line.After;
        RulesClearCheck.IsVisible = !line.IsSubject && preview.ClearsClass;
        var action = SelectedIssue;
        RulesOpenConflict.IsVisible = action is not null && preview.Current.Classroom.GradeId is not null;
        RulesOpenConflict.Content = action?.Action == "TEACHING" ? "处理该科任课" : "办理走班教学班";
        if (line.SubjectId is not { } id)
        {
            RulesReviewDetail.Text = $"保存后，{preview.Current.Classroom.Name}（{preview.Current.Classroom.Code}）不再开设任何科目。请单独确认。";
            return;
        }

        var removals = review.ForSubject(id).ToArray();
        var reasons = preview.Issues.Concat(preview.Notices).Where(issue => issue.SubjectId == id).Select(issue => issue.Message);
        var summary = removals.Length == 0 ? "该科无需解除走班来源。" : $"该科有 {removals.Length} 条走班来源将解除，请逐项核对。";
        RulesReviewDetail.Text = string.Join("\n", reasons.Append(summary));
        foreach (var removal in removals)
        {
            var content = new StackPanel { Spacing = 3 };
            var group = $"{removal.GroupName}（{removal.GroupCode}）" + (removal.IsActive ? "" : " · 已停用");
            var remaining = JoinOrNone(removal.RemainingSources.Select(source => $"{source.Name}（{source.Code}）"));
            content.Children.Add(new TextBlock { Text = group, Classes = { "label" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"解除来源班：{preview.Current.Classroom.Name}（{preview.Current.Classroom.Code}）", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"保留来源班：{remaining}", Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var box = new CheckBox { Content = content, Tag = removal.GroupId, IsChecked = review.IsConfirmed(id, removal.GroupId) };
            box.Classes.Add("rule-confirmation");
            AutomationProperties.SetName(box, $"已核对解除 {group} 的本班来源");
            AutomationProperties.SetAutomationId(box, $"RulesSource-{removal.GroupId}");
            box.IsCheckedChanged += (_, _) =>
            {
                if (_closed || _working || Pending is not null || _sourceReview != review || SelectedReview?.SubjectId != id) return;
                review.SetConfirmed(id, removal.GroupId, box.IsChecked == true);
                RenderReviewProgress();
                RulesSave.IsEnabled = CanConfirmReview;
            };
            RulesSourceChecks.Children.Add(box);
        }
    }

    private void RenderReviewProgress()
    {
        RulesReviewSummary.IsVisible = _preview is not null && _sourceReview is not null;
        if (_preview is not { } preview || _sourceReview is not { } review)
        {
            RulesNextUnconfirmed.IsVisible = false;
            return;
        }
        foreach (var line in RulesReviewRows.Items.OfType<ClassRuleReviewLine>())
        {
            var remaining = review.RemainingFor(line.SubjectId);
            var status = !line.IsSubject ? (ShouldConfirmClear ? "待确认" : "已核对") :
                remaining > 0 ? $"{remaining} 条来源待核对" :
                review.ForSubject(line.SubjectId).Any() ? "来源已核对" : "";
            if (line.HasIssue) status = status.Length == 0 ? "需先处理" : "需先处理 · " + status;
            line.SetStatus(status);
        }
        var remainingSubjects = preview.Current.Rows.Where(row => review.RemainingFor(row.SubjectId) > 0).Take(2).ToArray();
        var sources = remainingSubjects is [var subject] ? $"{subject.Name} {review.RemainingCount} 条来源待核对" :
            review.RemainingCount > 0 ? $"{review.RemainingCount} 条来源待核对" : "来源已核对";
        RulesReviewSummary.Text = $"{preview.Changes.Length} 科调整" +
            (preview.Removals.Length > 0 ? $" · {sources}" : "") +
            (preview.ClearsClass ? (ShouldConfirmClear ? " · 整班清空待确认" : " · 整班清空已核对") : "");
        RulesNextUnconfirmed.IsVisible = Pending is null && (review.RemainingCount > 0 || ShouldConfirmClear);
        RulesNextUnconfirmed.IsEnabled = !_working;
    }

    private void NextUnconfirmedClicked(object? sender, RoutedEventArgs args) => NextUnconfirmed();

    private void NextUnconfirmed()
    {
        if (_working || Pending is not null || _sourceReview is not { } review) return;
        var next = review.NextUnconfirmed(SelectedReview?.SubjectId);
        if (next is null && !ShouldConfirmClear) return;
        var line = RulesReviewRows.Items.OfType<ClassRuleReviewLine>().FirstOrDefault(row => row.SubjectId == next?.SubjectId);
        if (line is null) return;
        RulesReviewRows.SelectedItem = line;
        FocusReview(next?.GroupId);
    }

    private async void ReviewKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || _working || SelectedReview is null) return;
        args.Handled = true;
        if (!_compact && RulesOpenConflict.IsEffectivelyEnabled && RulesOpenConflict.IsEffectivelyVisible) await OpenConflictAsync();
        else FocusReview();
    }

    private async void OpenConflictClicked(object? sender, RoutedEventArgs args) => await OpenConflictAsync();
    private async Task OpenConflictAsync()
    {
        if (_working || _resolver is not null || Pending is not null || SelectedIssue is not { } issue ||
            _baseline is not { Classroom.GradeId: { } grade } baseline) return;
        var window = new GradeTeachingPlanWindow(_client, _school, grade, issue.SubjectId, null, _termId, _classId,
            ruleAction: issue.Action) { SessionExpired = AccessChanged };
        _resolver = window;
        try { await window.ShowDialog(this); }
        finally { _resolver = null; }
        if (_closed) return;
        WorkspaceChanged |= window.WorkspaceChanged;
        DropReview();
        await RunAsync(ReloadAsync);
        if (_closed) return;
        if (SelectedId != issue.SubjectId && _overlaps.Count == 0 && _differences.Count == 0) SelectEditorSubject(issue.SubjectId);
        FocusEditor(reveal: _compact);
    }

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanConfirmReview || _baseline is not { } baseline || _preview is not { } preview || _sourceReview is not { } review) return;
        var operation = new Submission(baseline, Choices, _archiveEditable, preview, review.ConfirmedGroupIds(),
            RulesClearCheck.IsChecked == true, Guid.NewGuid());
        PendingOperations[PendingKey] = operation;
        await RunAsync(() => SaveAsync(operation));
    }

    private async Task SaveAsync(Submission operation)
    {
        _recoveryRead = false;
        try
        {
            var result = await _client.SaveClassRulesAsync(operation.Baseline, operation.Choices, operation.EditArchive,
                operation.Preview, operation.Sources, operation.Clear, operation.Id, _lifetime.Token);
            if (_closed) return;
            PendingOperations.Remove(PendingKey);
            if (result.Receipt is { } receipt) Finish(receipt, result.Current);
            else if (result.State == "STALE" || result.Current.Version != operation.Baseline.Version) Stale(result.Current);
            else
            {
                ShowPreview(result.Preview!);
                Notice("本次零写入。请重新核对变更和需要确认的来源。", true);
            }
        }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
        {
            if (_closed) return;
            Notice("保存结果待核对。正在按原编号读取本账号的回执。");
            await VerifyAsync(operation);
        }
        catch (ConsoleApiException)
        {
            PendingOperations.Remove(PendingKey);
            throw;
        }
    }

    private async Task VerifyAsync(Submission operation)
    {
        var receipt = await _client.GetClassRuleReceiptAsync(operation.Baseline, operation.Preview, operation.Id, _lifetime.Token);
        var current = await ReadAsync();
        if (_closed) return;
        if (receipt is not null)
        {
            PendingOperations.Remove(PendingKey);
            Finish(receipt, current);
            return;
        }
        Bind(operation.Baseline);
        foreach (var choice in operation.Choices) _draft[choice.SubjectId] = choice;
        Merge(current);
        _recoveryRead = true;
        var choices = current.Rows.Select(row => row.Choice).ToHashSet();
        var matches = choices.SetEquals(operation.Choices) &&
            !current.Rows.SelectMany(row => row.Groups).Any(group => operation.Sources.Contains(group.Id));
        Notice(matches ? "服务器现状与本次提交一致，暂未找到本账号回执。可再次核对，或按原编号重试。" :
            "服务器现状与提交不同，暂未找到本账号回执。可按原编号重试，由服务器重新核对原预览。", true);
    }

    private void Finish(ClassRuleReceipt receipt, ClassRuleSnapshot current)
    {
        WorkspaceChanged = true;
        Bind(current);
        var changedAgain = receipt.Changes.Where(choice => current.Rows.FirstOrDefault(row => row.SubjectId == choice.SubjectId)?.Choice != choice)
            .Select(choice =>
            {
                var actual = current.Rows.FirstOrDefault(row => row.SubjectId == choice.SubjectId);
                return $"{actual?.Name ?? choice.SubjectId}：本次保存 {Describe(choice)}，学校当前 {Describe(actual?.Choice)}";
            }).ToArray();
        var sourcesBack = current.Rows.SelectMany(row => row.Groups).Where(group => receipt.RemovedSourceIds.Contains(group.Id))
            .Select(group => group.Name).ToArray();
        var message = $"已确认本账号的保存回执。调整 {receipt.Changes.Length} 科，解除 {receipt.RemovedSourceIds.Length} 条来源。";
        if (changedAgain.Length > 0) message += " " + string.Join("；", changedAgain);
        if (sourcesBack.Length > 0) message += " 学校当前已重新关联：" + string.Join("、", sourcesBack) + "。";
        Notice(message);
        FocusEditor();
    }

    private async void VerifyClicked(object? sender, RoutedEventArgs args)
    {
        if (Pending is { } operation) await RunAsync(() => VerifyAsync(operation));
    }

    private async void RetryClicked(object? sender, RoutedEventArgs args)
    {
        if (_recoveryRead && Pending is { } operation) await RunAsync(() => SaveAsync(operation));
    }

    private async void PresetClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanEdit || _baseline is not { } baseline || _presetWindow is not null) return;
        var recipes = new Option[] { new("all-walking", "基础随班，其余走班"), new("single-fixed", "单科定班"),
            new("triple-fixed", "三科完全定班"), new("all-fixed", "全部科目随行政班"), new("custom", "完全自定义") };
        var kind = new ComboBox { ItemsSource = recipes, SelectedIndex = 4, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var subjectChoices = baseline.Rows.Where(row => row.Category != "CORE").Select(row => new Option(row.SubjectId, $"{row.Name} · {row.Code}")).ToArray();
        var fixedSubjects = new ListBox { ItemsSource = subjectChoices, SelectionMode = SelectionMode.Multiple, MaxHeight = 150 };
        AutomationProperties.SetName(kind, "授课方式预设");
        AutomationProperties.SetName(fixedSubjects, "定班科目");
        var help = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var cancel = new Button { Content = "取消", Classes = { "action" } };
        var apply = new Button { Content = "应用到草稿", Classes = { "action", "accent" } };
        var window = new Window
        {
            Title = "授课方式预设", Width = 410, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 8,
                Children = { kind, help, fixedSubjects, new StackPanel { Spacing = 6, Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, apply } } }
            }
        };
        void Update()
        {
            var preset = (kind.SelectedItem as Option)?.Id;
            var required = preset == "single-fixed" ? 1 : preset == "triple-fixed" ? 3 : 0;
            fixedSubjects.IsVisible = required > 0;
            apply.IsEnabled = preset != "custom" && (required == 0 || fixedSubjects.SelectedItems?.Count == required);
            help.Text = required == 0 ? "预设只填授课方式，已开的课程保留原有必修属性。" : $"请选择 {required} 门非基础科目固定随班。";
        }
        kind.SelectionChanged += (_, _) => Update();
        fixedSubjects.SelectionChanged += (_, _) => Update();
        cancel.Click += (_, _) => window.Close();
        apply.Click += (_, _) =>
        {
            var preset = (kind.SelectedItem as Option)?.Id;
            var fixedIds = preset is "single-fixed" or "triple-fixed"
                ? fixedSubjects.SelectedItems!.OfType<Option>().Select(row => row.Id).ToHashSet()
                : [];
            foreach (var row in baseline.Rows.Where(row => !_overlaps.ContainsKey(row.SubjectId)))
            {
                var mode = row.Category == "CORE" || preset == "all-fixed" || fixedIds.Contains(row.SubjectId)
                    ? Following : preset == "triple-fixed" ? NotOffered : Walking;
                ChangeMode(row.SubjectId, mode);
            }
            window.Close();
            RefreshRows();
            Render();
            Notice("预设已填入草稿。请按科目核对授课方式和必修属性。");
        };
        Update();
        _presetWindow = window;
        window.Closed += (_, _) => { if (_presetWindow == window) _presetWindow = null; };
        try { await window.ShowDialog(this); }
        finally { if (_presetWindow == window) _presetWindow = null; }
        if (!_closed && _presetWindow is null) RulesPreset.Focus();
    }
}
