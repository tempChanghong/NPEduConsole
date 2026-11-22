using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class ClassSubjectRulesWindow
{
    private ClassRuleChoice[] Choices => _baseline!.Rows.Select(row => _draft[row.SubjectId]).ToArray();

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
        FocusEditor();
    }

    private void ShowPreview(ClassRulePreview preview)
    {
        _preview = preview;
        _sourceChecks.Clear();
        _filling = true;
        RulesSourceChecks.Children.Clear();
        foreach (var removal in preview.Removals)
        {
            var box = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = RemovalText(removal, preview.Current.Classroom.Name), TextWrapping = Avalonia.Media.TextWrapping.Wrap
                }
            };
            box.IsCheckedChanged += ConfirmationChanged;
            RulesSourceChecks.Children.Add(box);
            _sourceChecks.Add(removal.GroupId, box);
        }
        RulesClearCheck.IsChecked = false;
        RulesClearCheck.IsVisible = preview.ClearsClass;
        _filling = false;
        var rows = preview.Changes.Select(change =>
        {
            var before = preview.Current.Rows.First(row => row.SubjectId == change.SubjectId);
            var issues = preview.Issues.Where(issue => issue.SubjectId == change.SubjectId);
            return new TermTeachingReviewLine(before.SubjectId, $"{before.Name}（{before.Code}）", Describe(before.Choice),
                Describe(change), string.Join("\n", issues.Select(issue => issue.Message)));
        }).ToArray();
        var firstBlock = preview.Issues.FirstOrDefault();
        _reviewView.SetRows(rows, firstBlock?.SubjectId ?? SelectedId);
        RenderReviewDetail();
        if (preview.Issues.Length > 0)
            Notice($"有 {preview.Issues.Select(issue => issue.SubjectId).Distinct().Count()} 科需先处理。", true);
        else if (preview.Removals.Length > 0 || preview.ClearsClass)
            Notice("请逐项核对右侧将解除的来源和整班变更。");
        else Notice("请核对逐科调整，再确认保存。");
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || _preview != preview) return;
            if (firstBlock is not null) _reviewView.FocusIssue(firstBlock.SubjectId);
            else if (_reviewRows.SelectedItem is { } selected) FocusRow(_reviewRows, selected);
        }, DispatcherPriority.Loaded);
    }

    private static string RemovalText(ClassRuleRemoval row, string classroom) => $"从 {row.GroupName}（{row.GroupCode}）" +
        (row.IsActive ? "" : " · 已停用") + $" 移除 {classroom}。其余来源：" +
        JoinOrNone(row.RemainingSources.Select(source => $"{source.Name}（{source.Code}）"));

    private ClassRuleIssue? SelectedIssue => _preview?.Issues.Concat(_preview.Notices)
        .FirstOrDefault(issue => issue.SubjectId == _reviewView.SelectedKey && issue.Action is "TEACHING" or "COURSE_GROUPS");

    private void RenderReviewDetail()
    {
        if (_preview is not { } preview) return;
        var id = _reviewView.SelectedKey;
        var row = preview.Current.Rows.FirstOrDefault(item => item.SubjectId == id);
        RulesReviewTitle.Text = row is null ? "本次影响与确认" : $"{row.Name} · 本次影响与确认";
        var reasons = preview.Issues.Concat(preview.Notices).Where(issue => issue.SubjectId == id).Select(issue => issue.Message).ToArray();
        RulesReviewDetail.Text = reasons.Length > 0 ? string.Join("\n", reasons) :
            preview.Removals.Length == 0 && !preview.ClearsClass ? "本次无需解除走班来源。" : "勾选已核对的关系后即可保存。";
        var action = SelectedIssue;
        RulesOpenConflict.IsVisible = action is not null && preview.Current.Classroom.GradeId is not null;
        RulesOpenConflict.Content = action?.Action == "TEACHING" ? "处理该科任课" : "办理走班教学班";
    }

    private async void ReviewKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || !RulesOpenConflict.IsEnabled || !RulesOpenConflict.IsVisible) return;
        args.Handled = true;
        await OpenConflictAsync();
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
        _preview = null;
        await RunAsync(ReloadAsync);
        if (_closed) return;
        if (SelectedId != issue.SubjectId && _overlaps.Count == 0 && _differences.Count == 0) RefreshRows(issue.SubjectId);
        FocusEditor();
    }

    private void FocusEditor() => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && RulesRows.SelectedItem is { } selected) FocusRow(RulesRows, selected);
    }, DispatcherPriority.Loaded);

    private static void FocusRow(ListBox list, object row)
    {
        list.ScrollIntoView(row);
        Dispatcher.UIThread.Post(() =>
        {
            if (list.ContainerFromItem(row) is Control control) control.Focus();
            else list.Focus();
        }, DispatcherPriority.Loaded);
    }

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (!RulesSave.IsEnabled || _baseline is not { } baseline || _preview is not { } preview) return;
        var operation = new Submission(baseline, Choices, _archiveEditable, preview, _sourceChecks.Keys.ToArray(),
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
