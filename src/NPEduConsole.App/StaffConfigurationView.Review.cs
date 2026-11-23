using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class StaffConfigurationView
{
    private void Render()
    {
        if (_closed) return;
        var term = _scope?.Term;
        StaffPlanScope.Text = $"{_school.Name} · " + (term is null ? "启用学期待核对" : $"当前启用：{term.Name}（{term.AcademicYear} 学年 · 第 {term.Semester} 学期）");
        if (_scope is { Term: null }) StaffPlanScope.Text = $"{_school.Name} · 暂无启用学期";
        if (_pending is { } pending && pending.TermId != term?.Id)
            StaffPlanScope.Text += $" · 正在核对原学期（编号 {pending.TermId}）";
        if (_receipt is { } receipt && receipt.TermId != term?.Id)
            StaffPlanScope.Text += $" · 回执属于原学期（编号 {receipt.TermId}）";
        StaffPlanDates.Text = term is null ? "" : $"{Date(term.StartsAt, "未设开始日")} 至 {Date(term.EndsAt, "未设结束日")}";
        StaffPlanDates.IsVisible = term is not null && (term.StartsAt is not null || term.EndsAt is not null);
        StaffPlanFile.Text = _document is { } doc
            ? $"文件声明：{doc.SchoolCode ?? "未填学校代码"} · {(doc.AcademicYear is { } year ? $"{year} 学年" : "未填学年")} · {(doc.Semester is { } number ? $"第 {number} 学期" : "未填学期")} · {doc.TeacherCount} 位教师"
            : _pending is { } request ? $"原申请编号：{request.Id:D}" : _receipt is { } saved ? $"本人回执：{saved.RequestId:D}" : null;
        StaffPlanFile.IsVisible = StaffPlanFile.Text is not null;
        StaffPlanPendingBar.IsVisible = _pending is null && _receipt is null && _available.Length > 0;
        StaffPlanPendingBar.IsEnabled = !_busy;
        StaffPlanEmpty.IsVisible = !HasRows;
        StaffPlanPanes.IsVisible = HasRows;
        StaffPlanEmptyText.Text = _scope switch
        {
            null => _busy ? "正在核对所选学校及启用学期…" : "无法读取办理范围。请返回后重新打开。",
            _ when _pending is not null => "只读查回已保存的本人结果，也可在服务器查无本人回执时重新选择与原确认一致的文件。",
            { Term: null } => "请先在学期管理核对启用学期，再导入师资方案。",
            _ when ActiveTerm is null => "启用学期已切换。要办理新学期，请从更新后的开学配置重新进入。",
            { Mode: "OAUTH_EMAIL" } => "本校使用邮箱登录。请在「教师与岗位」使用批量分配教师访问和邮箱邀请。",
            _ => "选择网页首次初始化所用的完整师资 JSON。预览会分别列出账号、直接访问、任课与岗位。已有账号沿用原凭据，文件内的旧 PIN 不会覆盖它。"
        };
        StaffPlanSummary.Text = _receipt is { } done
            ? $"{done.Teachers.Length} 位教师 · {done.Teachers.Count(row => row.Created)} 个新建账号"
            : _preview is { } preview ? $"{preview.Teachers.Length} 位教师 · {preview.Teachers.Sum(row => row.Changes.Count(change => change.Changed))} 项变更 · {preview.Issues.Length} 个阻断 · {preview.Warnings.Length} 条提醒" : null;
        StaffPlanSummary.IsVisible = StaffPlanSummary.Text is not null;
        StaffPlanClose.IsEnabled = !_writing;
        StaffPlanOpen.IsVisible = !_cancelPrompt && _receipt is null && (_pending is null || _missingReceipt && _pending.TermId == ActiveTerm?.Id) && ScopeWritable;
        StaffPlanOpen.Content = _pending is null ? "选择完整师资 JSON" : "重新选原文件";
        StaffPlanOpen.IsEnabled = CanPick;
        StaffPlanPreview.IsVisible = !_cancelPrompt && _document is not null && _preview is null && _receipt is null;
        StaffPlanPreview.IsEnabled = CanPreview;
        StaffPlanApprove.IsVisible = !_cancelPrompt && !_cancelUncertain && _preview is { CanApply: true } review && (_pending is null || _missingReceipt && _pending.Proof == review.Version);
        StaffPlanApprove.IsEnabled = !_busy;
        StaffPlanSave.IsVisible = StaffPlanApprove.IsVisible;
        StaffPlanSave.Content = _pending is null ? "确认并导入" : "确认按原编号重试";
        StaffPlanSave.IsEnabled = CanSave;
        StaffPlanCheck.IsVisible = _pending is not null && !_cancelPrompt;
        StaffPlanCheck.IsEnabled = !_busy;
        StaffPlanCancelRequest.IsVisible = _pending is not null && _missingReceipt && !_cancelPrompt;
        StaffPlanCancelRequest.IsEnabled = !_busy;
        StaffPlanKeepRequest.IsVisible = StaffPlanConfirmCancel.IsVisible = _cancelPrompt;
        StaffPlanKeepRequest.IsEnabled = StaffPlanConfirmCancel.IsEnabled = !_busy;
        StaffPlanNew.IsVisible = _receipt is not null;
        StaffPlanNew.IsEnabled = !_busy;
        StaffPlanSearch.IsEnabled = StaffPlanRows.IsEnabled = !_busy;
        Arrange();
        RenderDetail();
    }

    private static string Date(DateTimeOffset? value, string fallback) => value?.ToString("yyyy-MM-dd") ?? fallback;

    private void LoadPreviewRows(StaffConfigurationPreview preview, int? selectedIndex)
    {
        var rows = preview.Teachers.OrderBy(row => row.Index).Select(row =>
        {
            var issues = preview.Issues.Where(issue => issue.TeacherIndex == row.Index).ToArray();
            var warnings = preview.Warnings.Where(issue => issue.TeacherIndex == row.Index).ToArray();
            var changes = row.Changes.Count(item => item.Changed);
            var detail = new List<string> { string.IsNullOrWhiteSpace(row.Username) ? "缺短账号" : row.Username, $"{changes} 项变更" };
            if (issues.Length != 0) detail.Add($"{issues.Length} 个阻断");
            if (warnings.Length != 0) detail.Add($"{warnings.Length} 条提醒");
            return new StaffPlanRow(row.Index, string.IsNullOrWhiteSpace(row.Name) ? $"文件第 {row.Index + 1} 位教师" : row.Name,
                string.Join(" · ", detail), row, issues, warnings);
        }).ToList();
        var indexes = rows.Select(row => row.Index).ToHashSet();
        var general = preview.Issues.Where(issue => issue.TeacherIndex is not { } index || !indexes.Contains(index)).ToArray();
        var reminders = preview.Warnings.Where(issue => issue.TeacherIndex is not { } index || !indexes.Contains(index)).ToArray();
        if (general.Length + reminders.Length != 0)
            rows.Insert(0, new(-1, "文件与范围", $"{general.Length} 个阻断 · {reminders.Length} 条提醒", null, general, reminders));
        SetRows(rows.ToArray(), selectedIndex ?? rows.FirstOrDefault(row => row.Issues.Length > 0)?.Index);
    }

    private void SetRows(StaffPlanRow[] rows, int? selectedIndex = null)
    {
        _rows = rows;
        ApplySearch(selectedIndex);
    }

    private void ApplySearch(int? selectedIndex)
    {
        var query = StaffPlanSearch.Text?.Trim() ?? "";
        var rows = _rows.Where(row => row.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            row.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Index == -1).ToArray();
        _binding = true;
        StaffPlanRows.ItemsSource = rows;
        StaffPlanRows.SelectedItem = rows.FirstOrDefault(row => row.Index == selectedIndex) ?? rows.FirstOrDefault();
        _binding = false;
        RenderDetail();
        Arrange();
    }

    private void Arrange()
    {
        if (_closed) return;
        StaffPlanPanes.ColumnDefinitions[1].Width = Compact ? new GridLength(0) : new GridLength(3, GridUnitType.Star);
        StaffPlanPanes.ColumnSpacing = Compact ? 0 : 8;
        Grid.SetColumn(StaffPlanDetailPane, Compact ? 0 : 1);
        StaffPlanListPane.IsVisible = !Compact || !_detail;
        StaffPlanDetailPane.IsVisible = !Compact || _detail;
        StaffPlanDetails.IsVisible = Compact && Selected is not null;
        StaffPlanBackList.IsVisible = Compact && _detail;
    }

    private void RenderDetail()
    {
        var row = Selected;
        StaffPlanDetailTitle.Text = row is null ? "没有匹配的教师" : row.Teacher is { } teacher && !string.IsNullOrWhiteSpace(teacher.Username)
            ? $"{row.Title}（{teacher.Username}）" : row.Title;
        StaffPlanDetail.Children.Clear();
        if (row is null) return;
        foreach (var issue in row.Issues) AddIssue(row, issue, warning: false);
        foreach (var issue in row.Warnings) AddIssue(row, issue, warning: true);
        if (row.Teacher is { } person)
        {
            AddChanges(person.Changes, fromReceipt: false);
            if (person.Changes.Length == 0 && row.Issues.Length + row.Warnings.Length == 0)
                StaffPlanDetail.Children.Add(Line("这位教师没有提交变更。", "muted"));
        }
        if (row.Saved is { } saved)
        {
            AddDelivery(saved);
            if (saved.Changes is { Length: > 0 } changes)
            {
                StaffPlanDetail.Children.Add(Line("本次回执记录的变化", "label"));
                AddChanges(changes, fromReceipt: true);
            }
            else StaffPlanDetail.Children.Add(Line("原回执未提供逐项办理明细。", "muted"));
        }
    }

    private void AddChanges(StaffConfigurationChange[] changes, bool fromReceipt)
    {
        foreach (var change in changes.OrderByDescending(item => item.Changed))
        {
            var body = new StackPanel { Spacing = 3 };
            body.Children.Add(Line($"{Kind(change.Kind)} · {change.Label}", "label"));
            if (change.Changed || change.Before != change.After)
            {
                var values = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8 };
                values.Children.Add(Line($"{(fromReceipt ? "保存前" : "当前")}：{DisplayValue(change.Before)}", "muted"));
                var after = Line($"{(fromReceipt ? "当时保存后" : "导入后")}：{DisplayValue(change.After)}");
                Grid.SetColumn(after, 1);
                values.Children.Add(after);
                body.Children.Add(values);
            }
            else body.Children.Add(Line($"保持：{DisplayValue(change.Before)}", "muted"));
            StaffPlanDetail.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(8, 5), Child = body });
        }
    }

    private static string Kind(string? kind) => kind switch
    {
        "ACCOUNT" => "账号", "ACCESS" => "直接访问", "TEACHING" => "任课", "GRADE_DUTY" => "年级岗位",
        "CLASS_DUTY" => "班级岗位", _ => "文件"
    };

    private static string DisplayValue(string? value) => string.IsNullOrWhiteSpace(value) ? "无" : value;

    private void AddIssue(StaffPlanRow row, StaffConfigurationIssue issue, bool warning)
    {
        var group = new StackPanel { Spacing = 4 };
        group.Children.Add(Line($"{(warning ? "提醒" : "阻断")} · {Kind(issue.Kind)}", "label"));
        group.Children.Add(Line(issue.Message));
        if (!string.IsNullOrWhiteSpace(issue.Path))
        {
            var position = Line(FilePosition(issue), "muted");
            ToolTip.SetTip(position, issue.Path);
            group.Children.Add(position);
        }
        var layout = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 7 };
        layout.Children.Add(group);
        if (!warning && issue.TargetId is not null && (issue.Kind is "TEACHING" && issue.SubjectId is not null ||
            issue.Kind is "CLASS_DUTY" or "GRADE_DUTY"))
        {
            var action = new Button { Classes = { "action" }, Content = issue.Kind == "TEACHING" ? "打开原任课" : "打开原岗位", IsEnabled = !_busy,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(action, $"StaffPlanResolve-{issue.Kind}");
            action.Click += async (_, _) => await ResolveAsync(row, issue);
            Grid.SetColumn(action, 1);
            layout.Children.Add(action);
        }
        StaffPlanDetail.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(8, 6), Child = layout });
    }

    private static string FilePosition(StaffConfigurationIssue issue)
    {
        if (issue.TeacherIndex is not { } teacher) return $"文件位置：{issue.Path}";
        var prefix = $"文件第 {teacher + 1} 位教师";
        var first = issue.Path.IndexOf('[');
        var last = issue.Path.LastIndexOf('[');
        var end = last < 0 ? -1 : issue.Path.IndexOf(']', last);
        if (last > first && end > last && int.TryParse(issue.Path.AsSpan(last + 1, end - last - 1), out var position))
            return $"{prefix} · {Kind(issue.Kind)}第 {position + 1} 项";
        return $"{prefix} · {(issue.Kind is null ? issue.Path : Kind(issue.Kind))}";
    }

    private async Task ResolveAsync(StaffPlanRow row, StaffConfigurationIssue issue)
    {
        if (_busy || _receipt is not null || _preview is not { } preview || ResolveIssue is not { } open) return;
        StaffPlanApprove.IsChecked = false;
        await RunAsync(async ct =>
        {
            await open(issue, row.Teacher, preview.TermId, ct);
            if (_closed) return;
            _preview = null;
            await ReadScopeAsync(ct);
            if (_closed) return;
            if (ActiveTerm is null)
            {
                ClearPreview();
                Status("启用学期已变化，原文件已保留。请从开学配置进入新学期。");
                return;
            }
            await ReadPreviewAsync(ct, row.Index);
        });
        FocusLater(StaffPlanDetailPane.IsEffectivelyVisible && Compact ? StaffPlanBackList : StaffPlanRows);
    }

    private void AddDelivery(StaffConfigurationSavedTeacher teacher)
    {
        StaffPlanDetail.Children.Add(Line(teacher.Created ? $"已新建短账号 {teacher.Username}" : $"沿用短账号 {teacher.Username} 及原凭据。"));
        if (!_pins.TryGetValue(teacher.AccountId, out var delivery))
        {
            if (teacher.Created) StaffPlanDetail.Children.Add(Line(_scope?.Mode == "SHARED_PASSWORD"
                ? "请使用学校发放的通用口令登录。" : "当前没有可交付的 PIN。如需新 PIN，请在教师账号页办理。", "muted"));
            return;
        }
        StaffPlanDetail.Children.Add(Line("仅交付本次生效的初始 PIN。另存或复制一次后，这里会清除。", "muted"));
        var actions = new WrapPanel { ItemSpacing = 7, LineSpacing = 4 };
        foreach (bool save in new[] { false, true })
        {
            var button = new Button { Content = save ? "另存本次账号与 PIN" : "复制本次账号与 PIN", Classes = { "action" }, IsEnabled = !_busy };
            AutomationProperties.SetAutomationId(button, save ? "StaffPlanSavePin" : "StaffPlanCopyPin");
            button.Click += async (_, _) => await DeliverAsync(teacher, delivery, save);
            actions.Children.Add(button);
        }
        StaffPlanDetail.Children.Add(actions);
    }

    private async Task DeliverAsync(StaffConfigurationSavedTeacher teacher, InitialPinDelivery<int> delivery, bool save)
    {
        if (_busy || _receipt is not { } receipt || _scope is not { Mode: "LOCAL_PIN" } scope || _pins.GetValueOrDefault(teacher.AccountId) != delivery ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        await RunAsync(async ct =>
        {
            async Task<bool> Verify()
            {
                var catalog = await _client.GetTeacherAllocationAsync(_school.Id, null, null, true, ct);
                if (_closed || _receipt != receipt || Selected?.Saved?.AccountId != teacher.AccountId || _pins.GetValueOrDefault(teacher.AccountId) != delivery) return false;
                if (TeacherAllocationView.TeacherPinMatches(catalog, delivery) && catalog.SchoolCode == scope.SchoolCode) return true;
                _pins.Remove(teacher.AccountId);
                return false;
            }

            if (!await delivery.DeliverAsync(owner, save, _school.Name, scope.SchoolCode, "教师短账号", Verify, ct, SelectPinFile)) return;
            _pins.Remove(teacher.AccountId);
            if (!_closed) Status(save ? "已另存所选教师的账号和 PIN。" : "已复制所选教师的账号和 PIN。");
        });
    }

    private static TextBlock Line(string text, string? style = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        if (style is not null) block.Classes.Add(style);
        return block;
    }

    private void SearchChanged(object? sender, TextChangedEventArgs args)
    {
        if (!_binding && !_closed && _client is not null) ApplySearch(Selected?.Index);
    }
    private void RowChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_binding || _closed || _client is null) return;
        StaffPlanDetailScroll.Offset = default;
        RenderDetail();
        Arrange();
    }
    private void RowTapped(object? sender, TappedEventArgs args)
    {
        if (!Compact || _busy || args.Source is not Visual visual ||
            visual.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault() is not { } item ||
            StaffPlanRows.ItemFromContainer(item) is not StaffPlanRow row) return;
        StaffPlanRows.SelectedItem = row;
        OpenDetail();
        args.Handled = true;
    }
    private void RowKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || !Compact || _busy || Selected is null) return;
        OpenDetail();
        args.Handled = true;
    }
    private void DetailsClicked(object? sender, RoutedEventArgs args) => OpenDetail();
    private void BackListClicked(object? sender, RoutedEventArgs args) => BackToList();

    private void PageKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape && Compact && _detail)
        {
            BackToList();
            args.Handled = true;
        }
    }

    private void OpenDetail()
    {
        _detail = true;
        Arrange();
        FocusLater(StaffPlanBackList);
    }

    private void BackToList()
    {
        _detail = false;
        Arrange();
        if (Selected is { } row)
        {
            StaffPlanRows.ScrollIntoView(row);
            Dispatcher.UIThread.Post(() =>
            {
                if (!_closed && StaffPlanRows.IsEffectivelyVisible && Selected == row)
                    ((StaffPlanRows.ContainerFromItem(row) as Control) ?? StaffPlanRows).Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Background);
        }
        else FocusLater(StaffPlanSearch);
    }

    private void FocusLater(Control control) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && control.IsEffectivelyVisible && control.IsEffectivelyEnabled) control.Focus(NavigationMethod.Tab);
    }, DispatcherPriority.Loaded);
}
