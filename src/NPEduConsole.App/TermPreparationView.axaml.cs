using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TermPreparationView : UserControl
{
    private enum Page { Directory, Create, Check, ActivationResult }
    private sealed record Operation(string SourceId, TermDraftSettings Target, TermDraftPreview Preview, Guid RequestId);
    private sealed record PreviewSelection(string Name, string Details) { public override string ToString() => Name; }
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, Operation>> PendingByClient = new();
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string? _initialDraftCheck;
    private TermPreparationCatalog? _catalog;
    private ManagedAcademicTerm? _activeTerm;
    private TermDraftPreview? _preview;
    private TermDraftReceipt? _receipt;
    private TermPreparationCheck? _check;
    private string? _sourceId, _reviewId, _existingId, _selectedTermId, _screenId;
    private Page _page, _checkBack;
    private bool _busy, _closed, _writing, _binding, _dirty, _allowClose, _reviewed, _canRetry, _canEndCheck, _sourceUnavailable;

    public TermPreparationView() => InitializeComponent();

    internal TermPreparationView(ConsoleClient client, School school, string? initialDraftCheck = null) : this()
    {
        _client = client;
        _school = school;
        _initialDraftCheck = initialDraftCheck;
        TermScope.Text = school.Name;
        SizeChanged += (_, _) => ApplyLayout();
        Render();
    }

    internal async Task StartAsync()
    {
        await RunAsync(async () =>
        {
            if (PendingActivation is { } activation)
            {
                _check = activation.Check;
                _reviewId = activation.Check.Term.Id;
                _activeTerm = _check.ActiveTerm;
                SetPage(Page.Check);
                ShowCheck();
                await VerifyActivationAsync(activation);
                return;
            }
            await LoadCatalogAsync();
            if (_closed) return;
            if (Pending is { } pending)
            {
                StartEditor(pending.SourceId, pending.Target);
                ShowPreview(pending.Preview, false);
                await VerifyAsync(pending);
            }
            else if (_initialDraftCheck is not null)
            {
                _checkBack = Page.Directory;
                if (_catalog?.Drafts.Any(row => row.Id == _initialDraftCheck) == true)
                    await OpenCheckAsync(_initialDraftCheck);
                else
                {
                    SetPage(Page.Check);
                    Notice("这份导入回执对应的学期已不再是草稿。请返回结果，或从学期管理查看当前状态。", true);
                }
            }
            else Focus(PreparedTerms.ItemCount > 0 ? PreparedTerms : TermPrimary);
        });
    }

    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal Func<ManagedAcademicTerm, Task<bool>>? OpenOrganizationImport { get; init; }
    internal static bool HasPendingSchoolRequest(ConsoleClient client, string schoolId) =>
        PendingByClient.TryGetValue(client, out var drafts) && drafts.ContainsKey(schoolId) ||
        ActivationsByClient.TryGetValue(client, out var activations) && activations.ContainsKey(schoolId);
    internal Action? CloseRequested { get; set; }
    private Window HostWindow => (Window)TopLevel.GetTopLevel(this)!;
    private Dictionary<string, Operation> Operations => PendingByClient.GetOrCreateValue(_client);
    private Operation? Pending => Operations.GetValueOrDefault(_school.Id);
    private ManagedAcademicTerm? SelectedTerm => PreparedTerms.SelectedItem as ManagedAcademicTerm;
    private ManagedAcademicTerm? SelectedDraft => SelectedTerm is {Status: "DRAFT"} draft ? draft : null;

    private async Task LoadCatalogAsync()
    {
        var catalog = await _client.GetTermPreparationAsync(_school.Id, _lifetime.Token);
        if (_closed) return;
        _catalog = catalog;
        _latestActivationId = catalog.LatestActivationRequestId;
        _activeTerm = catalog.ActiveTerm;
        ApplyTermFilter();
        ActiveTermName.Text = _activeTerm?.Name ?? "暂无启用学期";
        ActiveTermDates.Text = _activeTerm is { } term ? $"{term.Period} · {term.Dates}" :
            catalog.HasAnyTerm ? "学生暂时没有启用学期。请选择已有学期继续办理。" : "本校尚无来源学期，请先完成初始化。";
        if (_page == Page.Create) _sourceUnavailable = !CatalogTerms.Any(term => term.Id == _sourceId);
        Render();
    }

    private void ApplyLayout()
    {
        bool narrow = Bounds.Width < 850;
        Arrange(TermDirectory, DraftTermsCard, narrow);
        Arrange(TermCreateView, TermReviewCard, narrow);
        Arrange(TermCheckIssues, TermRemindersCard, narrow);
        static void Arrange(Grid panel, Border second, bool stacked)
        {
            int count = stacked ? 1 : 2;
            if (panel.ColumnDefinitions.Count != count)
            {
                panel.ColumnDefinitions = new(stacked ? "*" : "*,*");
                panel.RowDefinitions = new(stacked ? "Auto,Auto" : "Auto");
            }
            Grid.SetColumn(second, stacked ? 0 : 1);
            Grid.SetRow(second, stacked ? 1 : 0);
            second.Margin = stacked ? new Thickness(0, 8, 0, 0) : new Thickness(8, 0, 0, 0);
        }
    }

    private void StartEditor(string source, TermDraftSettings target)
    {
        _sourceId = source;
        _sourceUnavailable = !CatalogTerms.Any(term => term.Id == source);
        _binding = true;
        TermDraftYear.Text = target.AcademicYear.ToString(CultureInfo.InvariantCulture);
        TermDraftSemester.SelectedIndex = target.Semester - 1;
        TermDraftName.Text = target.Name;
        TermStartDate.Text = target.StartsAt;
        TermEndDate.Text = target.EndsAt;
        CarryTermMembers.IsChecked = target.CarryWorkspaceMembers;
        CarryTermTeaching.IsChecked = target.CarryTeachingAssignments;
        CarryTermDuties.IsChecked = target.CarryLeaderships;
        CarryTermInvites.IsChecked = target.CarryPendingInvitations;
        _binding = false;
        _existingId = null;
        _dirty = false;
        _receipt = null;
        TermDiscard.IsVisible = false;
        ClearPreview();
        SetPage(Page.Create);
        Focus(TermDraftName);
    }

    private void SetPage(Page page)
    {
        _page = page;
        TermScroll.Offset = default;
        Render();
    }

    private void DraftSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (_binding || _client is null) return;
        _selectedTermId = SelectedTerm?.Id;
        Render();
    }

    private void DraftListKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || _page != Page.Directory || SelectedTerm is null) return;
        args.Handled = true;
        if (SelectedDraft is not null) ExistingClicked(sender, args);
        else Focus(TermStatusActivate.IsEffectivelyVisible ? TermStatusActivate : TermPrimary);
    }

    private void InputChanged(object? sender, TextChangedEventArgs args) => EditorChanged();

    // 勾选组合一变就把继承后果写进顶部固定可见的提示里：840×480 时卡片底部那行说明常在滚动区外。
    private void CarryChanged(object? sender, RoutedEventArgs args)
    {
        EditorChanged();
        if (_binding || _page != Page.Create) return;
        if (string.IsNullOrWhiteSpace(TermAccessNote.Text)) return;
        Notice(TermAccessNote.Text);
        TermAccessNote.BringIntoView();
    }
    private void SemesterChanged(object? sender, SelectionChangedEventArgs args) => EditorChanged();

    private void EditorChanged()
    {
        if (_binding || _page != Page.Create) return;
        _dirty = true;
        _existingId = null;
        TermDiscard.IsVisible = false;
        ClearPreview();
        Notice(null);
        Render();
    }

    private void ClearPreview()
    {
        _reviewed = false;
        _preview = null;
        TermPreviewSummary.Text = "请选择继承范围，再读取预览。";
        TermPreviewItems.ItemsSource = null;
        TermPreviewItems.IsVisible = TermPreviewDetails.IsVisible = false;
    }

    private TermDraftSettings? ReadTarget()
    {
        string name = (TermDraftName.Text ?? "").Trim();
        int semester = TermDraftSemester.SelectedIndex + 1;
        if (!int.TryParse(TermDraftYear.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int year) ||
            year is < 2000 or > 2200 || semester is < 1 or > 3 || name.Length is < 1 or > 191)
        {
            Notice("请填写 2000 至 2200 的学年、学期序号和草稿名称。", true);
            Focus(name.Length == 0 ? TermDraftName : TermDraftYear);
            return null;
        }
        string? from = TermStartDate.Text?.Trim(), to = TermEndDate.Text?.Trim();
        from = string.IsNullOrEmpty(from) ? null : from;
        to = string.IsNullOrEmpty(to) ? null : to;
        DateOnly start = default, end = default;
        if (from is not null && !DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start))
        { Notice("开始日期请用 YYYY-MM-DD 格式。", true); Focus(TermStartDate); return null; }
        if (to is not null && !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end))
        { Notice("结束日期请用 YYYY-MM-DD 格式。", true); Focus(TermEndDate); return null; }
        if (from is not null && to is not null && end < start)
        { Notice("结束日期应在开始日期当天或之后。", true); Focus(TermEndDate); return null; }
        return new(year, semester, name, from, to, CarryTermMembers.IsChecked == true, CarryTermTeaching.IsChecked == true,
            CarryTermDuties.IsChecked == true, CarryTermInvites.IsChecked == true);
    }

    private async void PreviewClicked(object? sender, RoutedEventArgs args)
    {
        if (!TermPreviewButton.IsEnabled || Pending is not null) return;
        if (_sourceUnavailable && _activeTerm is { } active)
        {
            _sourceId = active.Id;
            _sourceUnavailable = false;
            ClearPreview();
            Notice("已选择当前启用学期。原填写内容仍在，请重新预览。");
            Render();
            return;
        }
        if (_sourceId is not { } source || ReadTarget() is not { } target) return;
        await RunAsync(async () =>
        {
            var preview = await _client.PreviewTermDraftAsync(_school.Id, source, target, _lifetime.Token);
            if (_closed) return;
            _existingId = null;
            ShowPreview(preview, true);
            Notice("请核对本次继承项目。学生名单需在新学期另行准备。");
            Focus(TermPreviewItems.IsEffectivelyVisible ? TermPreviewItems : TermPrimary);
        });
    }

    private void ShowPreview(TermDraftPreview preview, bool reviewed)
    {
        _preview = preview;
        _reviewed = reviewed;
        TermPreviewSummary.Text = Counts(preview.Counts);
        var items = preview.GradeDuties.Select(row => new PreviewSelection($"{row.Name} · 年级职责",
                People(row.Duties, person => person.Position == "PRIMARY" ? "年级组长" : person.Role)))
            .Concat(preview.Workspaces.Select(row => new PreviewSelection(
                $"{row.Name}（{row.Code}）{(row.IsActive ? "" : " · 已停用")}",
                string.Join('\n', new[] {row.SubjectName is null ? null : $"学科：{row.SubjectName}",
                    row.SourceClasses.Length == 0 ? null : $"来源班：{string.Join("、", row.SourceClasses.Select(room => room.Name))}",
                    $"成员权限 {row.WorkspaceMembers} 条 · 未认领邀请 {row.PendingInvitations} 条",
                    row.Assignments.Length == 0 ? null : "任课：" + People(row.Assignments, person => person.Role),
                    row.Duties.Length == 0 ? null : "职责：" + People(row.Duties, person => person.Role)}.Where(line => line is not null)))))
            .ToArray();
        TermPreviewItems.ItemsSource = items;
        TermPreviewItems.IsVisible = items.Length > 0;
        TermPreviewItems.SelectedItem = items.FirstOrDefault(row => row.Details.Contains("来源班", StringComparison.Ordinal)) ?? items.FirstOrDefault();
        PreviewItemChanged(null, null!);
        if (Bounds.Width < 850) Dispatcher.UIThread.Post(() => { if (!_closed && _page == Page.Create) TermReviewCard.BringIntoView(); }, DispatcherPriority.Loaded);
    }

    private static string People(IEnumerable<TermDraftPerson> rows, Func<TermDraftPerson, string> role) =>
        string.Join("、", rows.Select(person => $"{person.Name}（{role(person)}）"));

    private void PreviewItemChanged(object? sender, SelectionChangedEventArgs args)
    {
        TermPreviewDetails.Text = (TermPreviewItems.SelectedItem as PreviewSelection)?.Details;
        TermPreviewDetails.IsVisible = TermPreviewDetails.Text is not null;
    }

    private static string Counts(TermDraftCounts count) =>
        $"{count.Grades} 个年级 · {count.AdministrativeClasses} 个行政班 · {count.CourseGroups} 个走班教学班\n" +
        $"{count.Workspaces} 个教学空间 · {count.SourceLinks} 条走班来源\n" +
        $"{count.WorkspaceMembers} 条成员权限 · {count.TeachingAssignments} 条任课\n" +
        $"{count.GradeLeaderships} 条年级职责 · {count.ClassLeaderships} 条班级职责 · {count.PendingInvitations} 条未认领邀请";

    private async void PrimaryClicked(object? sender, RoutedEventArgs args)
    {
        if (!TermPrimary.IsEnabled) return;
        if (_page == Page.ActivationResult) { await OpenActivationScreenAsync(); return; }
        if (_page == Page.Check || PendingActivation is not null) { await OpenActivationActionAsync(); return; }
        if (Pending is { } pending)
        {
            if (_canRetry) await RunAsync(() => SaveAsync(pending));
            return;
        }
        if (_page == Page.Directory && (SelectedTerm ?? _activeTerm) is { } term)
        {
            string name = "";
            StartEditor(term.Id, new(term.AcademicYear + (term.Semester >= 2 ? 1 : 0), term.Semester == 1 ? 2 : 1, name, null, null));
            Notice(null);
            return;
        }
        if (_page != Page.Create || !_reviewed || _preview is not { } preview || _sourceId is not { } source || ReadTarget() is not { } target) return;
        var operation = new Operation(source, target, preview, Guid.NewGuid());
        Operations[_school.Id] = operation;
        await RunAsync(() => SaveAsync(operation));
    }

    private async Task SaveAsync(Operation pending)
    {
        _canRetry = _canEndCheck = false;
        _writing = true;
        Notice("正在建立新学期草稿…");
        Render();
        TermDraftResult result;
        try { result = await _client.SaveTermDraftAsync(_school.Id, pending.SourceId, pending.Target, pending.Preview, pending.RequestId, _lifetime.Token); }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
        {
            if (!_closed) await VerifyAsync(pending);
            return;
        }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            if (error.Code == "TERM_DRAFT_REQUEST_CONFLICT")
            {
                await VerifyAsync(pending);
                return;
            }
            Operations.Remove(_school.Id);
            _reviewed = false;
            throw;
        }
        if (_closed) return;
        if (result.Receipt is { } receipt) { await FinishDraftAsync(receipt); return; }
        Operations.Remove(_school.Id);
        ShowPreview(result.Preview!, false);
        Notice("来源配置已变化，本次没有建立草稿。输入已保留，请核对最新继承项目后重新预览。", true);
    }

    private async Task VerifyAsync(Operation pending)
    {
        _canRetry = _canEndCheck = false;
        TermDiscard.IsVisible = false;
        Notice("正在按原请求编号只读核对草稿回执…");
        TermDraftReceipt? result;
        try
        {
            result = await _client.GetTermDraftReceiptAsync(_school.Id, pending.RequestId, pending.Preview.Counts, _lifetime.Token);
        }
        catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403))
        {
            if (_closed) return;
            Notice("核对没有完成：" + error.Message + " 请继续按原编号核对回执。", true);
            Focus(TermRefresh);
            return;
        }
        if (_closed) return;
        if (result is not null) { await FinishDraftAsync(result); return; }
        await LoadCatalogAsync();
        if (_closed) return;
        var existing = _catalog?.Drafts.FirstOrDefault(row => row.AcademicYear == pending.Target.AcademicYear && row.Semester == pending.Target.Semester);
        _canRetry = _activeTerm?.Id == pending.SourceId && existing is null;
        _canEndCheck = !_canRetry;
        TermDiscard.IsVisible = _canEndCheck;
        Notice(_canRetry ? "暂未找到本次回执。可继续核对，或使用原编号重试。" :
            "暂未找到本次回执。启用学期或目标学期已变化，可继续核对或结束本次查询。同学年和学期的草稿无法证明本次成功。");
        Focus(_canRetry ? TermPrimary : TermRefresh);
    }

    private async Task FinishDraftAsync(TermDraftReceipt receipt)
    {
        Operations.Remove(_school.Id);
        _dirty = _writing = _canRetry = _canEndCheck = false;
        _receipt = receipt;
        _selectedTermId = receipt.TermId;
        _existingId = null;
        _checkBack = Page.Directory;
        await OpenCheckAsync(receipt.TermId);
    }

    private async void ExistingClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || Pending is not null) return;
        string? termId = _page == Page.Directory ? SelectedDraft?.Id : _existingId;
        if (termId is null) return;
        _receipt = null;
        _checkBack = _page;
        await RunAsync(() => OpenCheckAsync(termId));
    }

    private async void OrganizationImportClicked(object? sender, RoutedEventArgs args)
    {
        var term = _page == Page.Check ? _check?.Term : SelectedTerm;
        if (_busy || term is null || term.Status is not ("DRAFT" or "ARCHIVED") || OpenOrganizationImport is not { } open) return;
        await RunAsync(async () =>
        {
            if (!await open(term) || _closed) return;
            if (_page == Page.Check) await ReadCheckAsync(term.Id);
            else await LoadCatalogAsync();
            if (!_closed) Focus(TermOrganizationImport.IsVisible ? TermOrganizationImport : PreparedTerms);
        });
    }

    private async Task OpenCheckAsync(string termId)
    {
        _confirmation = null;
        _reviewId = termId;
        _selectedTermId = termId;
        _check = null;
        _screenId = null;
        TermScreenSearch.Clear();
        SetPage(Page.Check);
        await ReadCheckAsync(termId, reveal: true);
    }

    private async Task ReadCheckAsync(string termId, bool reveal = false)
    {
        _confirmation = null;
        TermActivationChanges.IsVisible = false;
        if (TermScreenRows.SelectedItem is TermDraftScreenImpact selection) _screenId = selection.BindingId;
        bool listFocused = TermScreenRows.IsKeyboardFocusWithin;
        _check = null;
        ShowCheck();
        try
        {
            var check = await _client.GetTermPreparationCheckAsync(_school.Id, termId, _lifetime.Token);
            if (_closed || _reviewId != termId) return;
            _check = check;
            _activeTerm = check.ActiveTerm;
            ShowCheck();
            if (_closed) return;
            if (reveal) RevealCheck();
            else if (listFocused) Focus(TermScreenRows);
            Notice(_receipt is null ? null : "草稿已建立，已按本次回执打开启用前检查。");
        }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            if (error.StatusCode is 401 or 403) throw;
            if (error.Code == "TERM_DRAFT_CHANGED") await LoadCatalogAsync();
            if (!_closed) Notice((_receipt is null ? "" : "已建立草稿。") + error.Message, true);
        }
    }

    // 进入检查页时把焦点放到真正的重点上：先阻断项，再逐台大屏，最后刷新按钮。
    private void RevealCheck()
    {
        if (_closed) return;
        if (TermBlockerItems.Children.OfType<Control>().FirstOrDefault() is { } blocker)
        {
            TermBlockersCard.BringIntoView();
            Focus(blocker);
            return;
        }
        if (TermScreenRows.ItemCount > 0)
        {
            TermScreenRows.BringIntoView();
            Focus(TermScreenRows);
            return;
        }
        Focus(CanConfirmActivation ? TermPrimary : TermRefresh);
    }

    private void ShowCheck()
    {
        TermCheckTarget.Text = _check?.Term.Name is { Length: > 0 } name ? "准备中 · " + name :
            (_receipt is null ? "草稿 · 待核对" : "已建立草稿 · 待核对");
        TermCheckDates.Text = _check is { } check ? $"{check.Term.Period} · {check.Term.Dates}" : "可刷新服务器检查结果。";
        TermCheckStatus.Text = _check is not { } status ? "正在读取服务器检查结果…" :
            status.ActiveTerm is { } running
                ? $"归档 {running.Name} 后，仍在原学期的 NPEduTools 授权也会失效。"
                : "该学期尚未启用，全校当前没有启用学期。";
        TermCheckReceiptCounts.IsVisible = _receipt is not null;
        TermCheckReceiptCounts.Text = _receipt is { } received ? "本次继承：" + Counts(received.Counts) : null;
        TermBlockerItems.Children.Clear();
        TermReminderItems.Children.Clear();
        if (_check is { } observed)
        {
            TermBlockersSummary.Text = observed.BlockingDiagnostics.Length == 0 ? "本次检查未发现启用阻断。" : $"有 {observed.BlockingDiagnostics.Length} 项需要先处理。";
            TermRemindersSummary.Text = observed.Reminders.Length == 0 ? "本次检查未发现一般提醒。" : $"有 {observed.Reminders.Length} 项需核对。";
            AddDiagnostics(TermBlockerItems, observed.BlockingDiagnostics, false);
            AddDiagnostics(TermReminderItems, observed.Reminders, true);
            TermScreenSummary.Text = observed.Screens.Length == 0 ? "当前没有需核对的启用大屏绑定。" :
                $"{observed.Screens.Length} 台启用大屏 · {observed.Screens.Count(row => row.TargetClass is not null)} 台班级代码匹配";
        }
        else TermBlockersSummary.Text = TermRemindersSummary.Text = TermScreenSummary.Text = "待核对";
        FilterScreens();
        Render();
    }

    // 每条诊断都是可聚焦的行，键盘可以把焦点停在“需要先处理”的具体一条上。
    private static void AddDiagnostics(Panel panel, IEnumerable<TermDraftDiagnostic> rows, bool muted)
    {
        foreach (var row in rows)
        {
            var label = new TextBlock {Text = row.Message, TextWrapping = TextWrapping.Wrap};
            if (muted) label.Classes.Add("muted");
            var host = new Border
            {
                Child = label,
                Focusable = true,
                Padding = new Thickness(2, 1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 0, 2)
            };
            AutomationProperties.SetName(host, row.Message);
            panel.Children.Add(host);
        }
    }

    private void ScreenSearchChanged(object? sender, TextChangedEventArgs args) => FilterScreens();

    // 输入绑定编号后按 Enter 直接把焦点放到第一台匹配的大屏，键盘用户不必再 Tab 进列表。
    private void ScreenSearchKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || TermScreenRows.ItemCount == 0) return;
        args.Handled = true;
        TermScreenRows.SelectedIndex = 0;
        Focus(TermScreenRows);
        if (TermScreenRows.SelectedItem is { } row) TermScreenRows.ScrollIntoView(row);
    }
    private void FilterScreens()
    {
        if (TermScreenRows is null) return;
        string query = (TermScreenSearch.Text ?? "").Trim();
        var rows = _check?.Screens.Where(row => new[] {row.BindingId, row.Name, row.Original, row.Target}
            .Any(text => text.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray() ?? [];
        TermScreenRows.ItemsSource = rows;
        TermScreenRows.SelectedItem = rows.FirstOrDefault(row => row.BindingId == _screenId);
        if (TermScreenRows.SelectedItem is { } selected) TermScreenRows.ScrollIntoView(selected);
    }

    private async void RefreshClicked(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (PendingActivation is { } activation) { await VerifyActivationAsync(activation); return; }
        if (_page == Page.ActivationResult && _activation?.Receipt is { } receipt)
        { await RefreshActivationAsync(receipt.RequestId, receipt.Term.Id); return; }
        if (Pending is { } pending) { await VerifyAsync(pending); return; }
        if (_page == Page.Check && _reviewId is { } check) { await ReadCheckAsync(check); return; }
        await LoadCatalogAsync();
        if (_closed) return;
        if (_page == Page.Create) ClearPreview();
        Notice(_page == Page.Create && _sourceUnavailable ? "当前来源已变化，原输入已保留。" : null);
    });

    private async void BackClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy) return;
        if (_initialDraftCheck is not null || Pending is not null || PendingActivation is not null || _page == Page.Directory) { Close(); return; }
        if (_page == Page.Create && _dirty) { OfferDiscard(); return; }
        var destination = _page == Page.Check ? _checkBack : Page.Directory;
        _receipt = null;
        _check = null;
        _reviewId = null;
        SetPage(destination);
        Notice(null);
        if (destination == Page.Directory)
            await RunAsync(async () => { await LoadCatalogAsync(); Focus(SelectedDraft is null ? TermPrimary : PreparedTerms); });
        else Focus(TermExistingButton);
    }

    private void OfferDiscard()
    {
        TermDiscard.IsVisible = true;
        Notice("还有未保存输入。可继续填写，或放弃后返回。");
        Focus(TermDiscard);
    }

    private async void DiscardClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy) return;
        if (Pending is { } pending)
        {
            if (!_canEndCheck) return;
            await RunAsync(async () =>
            {
                var receipt = await _client.GetTermDraftReceiptAsync(_school.Id, pending.RequestId, pending.Preview.Counts, _lifetime.Token);
                if (_closed) return;
                if (receipt is not null) { await FinishDraftAsync(receipt); return; }
                Operations.Remove(_school.Id);
                LeaveDraft();
                // 结束后必须重读目录：本次草稿可能已在服务器建成，列表不能停在旧快照上。
                await LoadCatalogAsync();
                if (_closed) return;
                Notice("已结束本次核对，并重新读取了学期列表。");
                Focus(SelectedDraft is null ? TermPrimary : PreparedTerms);
            });
            return;
        }
        LeaveDraft();
    }

    private void LeaveDraft()
    {
        _dirty = _sourceUnavailable = false;
        _existingId = null;
        TermDiscard.IsVisible = false;
        ClearPreview();
        SetPage(Page.Directory);
        Notice(null);
        Focus(TermPrimary);
    }

    private void WebClicked(object? sender, RoutedEventArgs args)
    {
        string? active = _activeTerm?.Id;
        bool setup = _catalog?.HasAnyTerm == false;
        _confirmation = null;
        Render();
        var url = setup
            ? SafeSetupUrl(_catalog?.SetupUrl)
            : SchoolAdminBrowser.Link(_catalog?.AdminUrl, _school.Id, active, "terms");
        if (url is not null && SchoolAdminBrowser.Open(url)) return;
        Notice(setup ? "请在 NPClassworks 网页打开首次初始化入口。" : "请在 NPClassworks 网页学校管理打开「学期运维」。");
    }

    private static Uri? SafeSetupUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var url) &&
        url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0 &&
        (url.Scheme == Uri.UriSchemeHttps || url.IsLoopback && url.Scheme == Uri.UriSchemeHttp) ? url : null;

    private async Task RunAsync(Func<Task> action)
    {
        if (_closed || _busy) return;
        _busy = true;
        Render();
        try { await action(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            if (error.Code == "TERM_DRAFT_SOURCE_CHANGED")
            {
                _reviewed = false;
                await TryReloadCatalogAsync();
            }
            if (error.Code == "TARGET_TERM_EXISTS")
            {
                await TryReloadCatalogAsync();
                if (_catalog?.Drafts.Any(row => row.Id == error.ExistingTermId) == true) _existingId = error.ExistingTermId;
            }
            if (error.StatusCode is 401 or 403)
            {
                ClearSchoolScope();
            }
            if (!_closed) Notice(PendingActivation is not null ? $"启用回执待核对。{error.Message}" :
                Pending is not null ? $"草稿回执待核对。{error.Message}" : error.Message, true);
            if (error.StatusCode is 401 or 403) SessionExpired?.Invoke(error);
        }
        finally { _busy = _writing = false; if (!_closed) Render(); }
    }

    private async Task TryReloadCatalogAsync()
    {
        try { await LoadCatalogAsync(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error) when (error.StatusCode is 401 or 403)
        {
            if (_closed) return;
            ClearSchoolScope();
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        }
        catch (ConsoleApiException) { }
    }

    private void ClearSchoolScope()
    {
        _catalog = null;
        _latestActivationId = null;
        _confirmation = null;
        _activation = null;
        Activations.Remove(_school.Id);
        TermActivatedScreens.ItemsSource = null;
        TermActivationChanges.IsVisible = false;
        _activeTerm = null;
        _check = null;
        PreparedTerms.ItemsSource = null;
        ActiveTermName.Text = "学校学期待核对";
        ActiveTermDates.Text = "当前未能读取该校学期。";
        DraftListEmpty.Text = "当前未能读取该校草稿。";
        DraftListEmpty.IsVisible = true;
        _dirty = false;
        SetPage(Page.Directory);
    }

    private void Render()
    {
        if (_client is null || TermPrimary is null) return;
        bool pending = Pending is not null;
        bool editable = !_busy && !pending && _catalog is not null;
        TermScope.Text = $"{_school.Name} · 全校实际启用：{_activeTerm?.Name ?? (_catalog is null ? "待核对" : "暂无")}";
        TermHeading.Text = _page switch
        {
            Page.Create => "建立新学期草稿",
            Page.Check when _check is { } current => $"启用前检查 · {current.Term.Name}",
            Page.Check => "启用前检查",
            Page.ActivationResult => "学期启用结果",
            _ => "学期管理"
        };
        ToolTip.SetTip(TermHeading, TermHeading.Text);
        TermDirectory.IsVisible = _page == Page.Directory;
        TermCreateView.IsVisible = _page == Page.Create;
        TermCheckView.IsVisible = _page == Page.Check;
        var source = CatalogTerms.FirstOrDefault(term => term.Id == _sourceId);
        TermDraftSource.Text = source is null ? "来源需重新核对。学生名单需另行准备。" :
            $"从 {source.Name}（{source.Period} · {source.StatusText}）继承。学生名单需另行准备。";
        TermAccessNote.Text = (CarryTermMembers.IsChecked == true, CarryTermTeaching.IsChecked == true) switch
        {
            (true, false) => "已取消继承任课。单独继承的教学空间成员权限仍可能赋予教师访问。",
            (false, true) => "已取消继承成员权限。任课记录仍会复制，但教师能否进入该教学空间取决于成员权限，请在预览里核对。",
            _ => "草稿保留组织结构和所选关系，学生名单需另行准备。"
        };
        TermSettingsCard.IsEnabled = editable;
        PreparedTerms.IsEnabled = editable;
        TermPreviewButton.IsVisible = _page == Page.Create && !pending;
        TermPreviewButton.Content = _sourceUnavailable ? "采用当前学期作来源" : _preview is null ? "预览继承" : "重新预览";
        TermPreviewButton.IsEnabled = editable && (!_sourceUnavailable || _activeTerm is not null);
        TermExistingButton.IsVisible = !pending && (_page == Page.Directory && PreparedTerms.ItemCount > 0 || _page == Page.Create && _existingId is not null);
        TermExistingButton.IsEnabled = editable && (_page == Page.Directory ? SelectedDraft is not null : _existingId is not null);
        TermOrganizationImport.IsVisible = !pending && OpenOrganizationImport is not null &&
            (_page == Page.Directory && PreparedTerms.ItemCount > 0 || _page == Page.Check && _check is not null);
        TermOrganizationImport.Content = _page == Page.Directory && SelectedTerm?.Status == "ARCHIVED" ? "批量更正" : "导入草稿组织";
        TermOrganizationImport.IsEnabled = editable && (_page == Page.Check ? _check is not null : SelectedTerm?.Status is "DRAFT" or "ARCHIVED");
        TermWeb.IsVisible = !pending && _catalog is not null && (_page == Page.Directory && !_catalog.HasAnyTerm ||
            _catalog.Terms is null && _page is Page.Directory or Page.Check);
        TermWeb.Content = _catalog?.HasAnyTerm == false ? "打开初始化入口" : "网页学期管理";
        TermPrimary.IsVisible = pending ? _canRetry : _page == Page.Create || _page == Page.Directory && (SelectedTerm ?? _activeTerm) is not null;
        TermPrimary.Content = pending ? "按原编号重试" : _page == Page.Create ? "建立草稿" : SelectedTerm is not null ? "以此建立新学期" : "新建草稿";
        TermPrimary.IsEnabled = !_busy && (pending ? _canRetry : _page == Page.Directory || _reviewed && !_sourceUnavailable);
        TermBack.Content = pending ? "关闭，稍后核对" : _page == Page.Directory ? "关闭" : "返回";
        TermRefresh.Content = pending ? "核对回执" : _page == Page.Check ? "刷新检查" : "刷新";
        TermDiscard.Content = pending ? "结束本次核对" : "放弃输入";
        TermBack.IsEnabled = TermRefresh.IsEnabled = TermDiscard.IsEnabled = !_busy;
        RenderActivation();
        RenderTermDirectory(editable);
        if (_initialDraftCheck is not null) TermBack.Content = "返回导入结果";
        ApplyLayout();
    }

    private void Notice(string? value, bool isError = false)
    {
        TermNotice.Text = value;
        TermNotice.IsVisible = !string.IsNullOrWhiteSpace(value);
        TermNotice.Classes.Set("error", isError);
    }

    private void Focus(Control target) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && target.IsEffectivelyVisible && target.IsEffectivelyEnabled)
        {
            target.BringIntoView();
            target.Focus(NavigationMethod.Tab);
        }
    }, DispatcherPriority.Loaded);

    internal bool CanClose()
    {
        if (_allowClose) return true;
        if (_writing) return false;
        if (_page is not (Page.Create or Page.Check) || !_dirty || Pending is not null) return true;
        OfferDiscard();
        return false;
    }

    internal void Close() => CloseRequested?.Invoke();

    internal void End()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        _termTeachingWindow?.CloseForScopeChange();
        _termStatusWindow?.CloseForScopeChange();
        _lifetime.Dispose();
    }
}
