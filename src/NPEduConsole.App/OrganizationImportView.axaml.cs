using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class OrganizationImportView : UserControl
{
    private sealed record Operation(OrganizationImportDraft Draft, OrganizationPreview Preview, Guid RequestId);
    private sealed record GradeChoice(string? Id, string Label, OrganizationGrade? Grade)
    {
        public override string ToString() => Label;
    }
    private sealed record ReceiptChoice(string Id, string Title, bool IsGroup)
    {
        public override string ToString() => Title;
    }

    private enum WorkStage { Table, Difference, Result }

    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly bool _archivedCorrection;
    private readonly string? _fixedTermId;
    private readonly CancellationTokenSource _lifetime = new();
    private OrganizationImportCatalog? _catalog;
    private OrganizationTerm? _term;
    private OrganizationDeclaration? _declaration;
    private OrganizationPreview? _preview;
    private OrganizationImportReceipt? _receipt;
    private OrganizationDifference[] _visibleReview = [];
    private string? _fileMismatch;
    private bool _binding, _busy, _writing, _closed, _allowClose, _dirty, _reviewed, _discard, _scopeUnavailable, _retryMissingReceipt;
    private WorkStage _stage;
    private (OrganizationImportDocument Document, string Source)? _replacement;
    private bool _showExcel;
    private int _gradeOrder;
    private OrganizationGrade _observedGrade = new("", "", 0);

    public OrganizationImportView() => InitializeComponent();

    internal OrganizationImportView(ConsoleClient client, School school, string? termId, bool archivedCorrection = false) : this()
    {
        _client = client;
        _school = school;
        _fixedTermId = termId;
        _archivedCorrection = archivedCorrection;
        if (archivedCorrection)
        {
            OrgHeading.Text = "归档学期批量更正 · 教学组织";
            OrgGradeOrderField.IsVisible = OrgSchoolSubjects.IsVisible = true;
        OrgGradeOrder.ValueChanged += (_, _) => OnGradeEdited();
            OrgApprove.Content = "我已核对所选归档学期及逐项变化";
            OrgTeacherAllocation.Content = "核对本年级教师访问";
        }
        OrgScope.Text = school.Name;
        OrgDifferenceFilter.ItemsSource = new[] { "所有差异", "新增", "修改", "移除", "阻断与待复核", "一致" };
        OrgDifferenceFilter.SelectedIndex = 0;
        RebuildTables();
        Render();
        SizeChanged += (_, _) => ArrangeOrganizationLayout();
        AddHandler(KeyDownEvent, WorkspaceKeyDown, RoutingStrategies.Tunnel);
    }

    internal async Task StartAsync()
    {
        ArrangeOrganizationLayout();
        await RunAsync(async () =>
        {
            if (!await RestorePendingAsync() && !_closed) await LoadAsync(_fixedTermId);
        });
        if (!_closed && _stage == WorkStage.Table && !_showExcel)
            (Pending is not null ? OrgVerify : _fixedTermId is null ? (Control)OrgTerm : OrgGrade).Focus(NavigationMethod.Tab);
    }

    internal bool CanClose()
    {
        if (_allowClose) return true;
        if (_writing) return false;
        if (_replacement is not null) { CancelReplacement(); return false; }
        if (_showExcel) { EndExcel(); return false; }
        if (!_dirty || Pending is not null) return true;
        _discard = true;
        Render();
        OrgKeepEditing.Focus(NavigationMethod.Tab);
        return false;
    }

    internal void End()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _replacement = null;
        OrgExcelText.Clear();
        _catalog = null;
        _term = null;
        _declaration = null;
        _preview = null;
        _receipt = null;
        _visibleReview = [];
        _fields.Clear();
        _classes.Clear();
        _groups.Clear();
        _subjects.Clear();
        OrgTerm.ItemsSource = OrgGrade.ItemsSource = OrgExistingSubject.ItemsSource = null;
        OrgClasses.ItemsSource = OrgGroups.ItemsSource = OrgSubjects.ItemsSource = OrgDifferences.ItemsSource = OrgReceiptClasses.ItemsSource = null;
    }

    private bool CanEdit => _catalog is not null && _term is not null && !_busy && !_scopeUnavailable && Pending is null;
    private Window HostWindow => (Window)TopLevel.GetTopLevel(this)!;
    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal Func<Window, Task<SchoolSubject?>>? OpenSubjects { get; init; }
    internal Func<string, string, string?, Task>? OpenTeacherAllocation { get; set; }
    internal Func<OrganizationImportView, Task>? OpenReceiptTarget { get; set; }
    internal Action? CloseRequested { get; set; }
    internal bool ActiveTermChanged { get; private set; }
    internal string? GoToDraftId { get; private set; }
    internal (string TermId, string WorkspaceId, bool IsGroup)? GoToTeaching { get; private set; }
    internal (string TermId, string GradeId)? GoToTeacherAllocation { get; private set; }
    internal (string TermId, string GradeId, string WorkspaceId)? GoToHistoryClass { get; private set; }
    internal string? SelectedReceiptWorkspace { get; private set; }

    private async Task LoadAsync(string? termId, bool clearTarget = false)
    {
        var catalog = await _client.GetOrganizationImportAsync(_school.Id, termId, _lifetime.Token, _archivedCorrection);
        if (_closed) return;
        _catalog = catalog;
        _term = clearTarget ? null : catalog.Terms.FirstOrDefault(row => row.Id == catalog.TermId);
        _binding = true;
        try
        {
            OrgTerm.ItemsSource = _archivedCorrection ? catalog.Terms.Where(row => row.Id == catalog.TermId).ToArray() : catalog.Terms;
            OrgTerm.SelectedItem = _term;
            OrgGrade.ItemsSource = GradeChoices(catalog);
            OrgGrade.SelectedItem = ((GradeChoice[])OrgGrade.ItemsSource!).FirstOrDefault(row =>
                string.Equals(row.Grade?.Code, OrgGradeCode.Text?.Trim(), StringComparison.OrdinalIgnoreCase));
            OrgExistingSubject.ItemsSource = catalog.Subjects;
        }
        finally { _binding = false; }
        UpdateScope();
        RebuildDetails();
        if (!catalog.Terms.Any()) Status("没有可导入的学期。请先在学期管理建立草稿，或使用学校初始化入口。");
    }

    private static GradeChoice[] GradeChoices(OrganizationImportCatalog catalog) =>
        [new(null, "填写新年级", null), .. catalog.Grades.Select(row => new GradeChoice(row.Id, row.ToString(),
            new(row.Code, row.Name, row.SortOrder)))];

    private void UpdateScope()
    {
        var term = _term?.ToString() ?? "请选择目标学期";
        var grade = OrgGradeName.Text?.Trim();
        OrgScope.Text = $"{_school.Name} / 办理学期　{term} / {(string.IsNullOrEmpty(grade) ? "请选择或填写年级" : grade + "（" + OrgGradeCode.Text?.Trim() + "）")}";
        if (_archivedCorrection) OrgScope.Text += $"　全校当前启用　{_catalog?.Terms.FirstOrDefault(row => row.Status == "ACTIVE")?.Name ?? "暂无"}";
    }

    private async void TermSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_closed || _fixedTermId is not null || _binding || _busy || Pending is not null ||
            OrgTerm.SelectedItem is not OrganizationTerm term || _term?.Id == term.Id) return;
        ClearDisplayedReceipt();
        InvalidateReview();
        await RunAsync(async () =>
        {
            await LoadAsync(term.Id);
            if (_dirty && !_closed) Status("已切换导入目标，表格仍保留，请重新核对年级、来源和文件声明。");
        });
        if (!_closed && _term?.Id != term.Id)
        {
            _binding = true;
            OrgTerm.SelectedItem = _term;
            _binding = false;
        }
    }

    private void GradeSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_binding || !CanEdit || OrgGrade.SelectedItem is not GradeChoice choice) return;
        _binding = true;
        OrgGradeCode.Text = choice.Grade?.Code;
        OrgGradeName.Text = choice.Grade?.Name;
        _gradeOrder = choice.Grade?.SortOrder ?? 0;
        if (_archivedCorrection) OrgGradeOrder.Value = _gradeOrder;
        _binding = false;
        _observedGrade = CurrentGrade();
        UpdateScope();
        RebuildGroupEditor();
        ClearDisplayedReceipt();
        InvalidateReview();
        Render();
    }

    private void TargetGradeEdited(object? sender, TextChangedEventArgs args) => OnGradeEdited();

    private void OnGradeEdited()
    {
        if (_binding || _catalog is null) return;
        var grade = CurrentGrade();
        if (grade == _observedGrade) return;
        _observedGrade = grade;
        UpdateScope();
        RebuildGroupEditor();
        Edited();
    }

    private OrganizationGrade CurrentGrade() => new(OrgGradeCode.Text?.Trim() ?? "", OrgGradeName.Text?.Trim() ?? "",
        _archivedCorrection ? (int)(OrgGradeOrder.Value ?? 0) : _gradeOrder);

    private OrganizationImportDraft Snapshot() => new(_term!.Id, _term.Status, CurrentGrade(),
        _subjects.Select(row => row.Snapshot()).ToArray(), _classes.Select(row => row.Snapshot()).ToArray(),
        _groups.Select(row => row.Snapshot()).ToArray(), _declaration, _archivedCorrection,
        _archivedCorrection ? (OrgGrade.SelectedItem as GradeChoice)?.Id : null);

    private void SetDocument(OrganizationImportDocument document)
    {
        _binding = true;
        try
        {
            OrgGrade.SelectedItem = ((IEnumerable<GradeChoice>?)OrgGrade.ItemsSource)?.FirstOrDefault(row =>
                row.Grade is { } grade && string.Equals(grade.Code, document.Grade.Code, StringComparison.OrdinalIgnoreCase));
            OrgGradeCode.Text = document.Grade.Code;
            OrgGradeName.Text = document.Grade.Name;
            _gradeOrder = document.Grade.SortOrder;
            if (_archivedCorrection) OrgGradeOrder.Value = _gradeOrder;
            _classes.Clear();
            _classes.AddRange(document.AdministrativeClasses.Select(row => new ClassRow(row)));
            _subjects.Clear();
            _subjects.AddRange(document.Subjects.Select(row => new SubjectRow(row)));
            _groups.Clear();
            _groups.AddRange(document.CourseGroups.Select(row => new GroupRow(row)));
            _declaration = document.Declaration;
            RebuildTables();
        }
        finally { _binding = false; }
        _observedGrade = CurrentGrade();
        UpdateScope();
        Edited();
    }

    private async void OpenFileClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanEdit || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        await RunAsync(async () =>
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "打开旧组织 JSON", AllowMultiple = false,
                FileTypeFilter = [new("组织配置 JSON") { Patterns = ["*.json"] }]
            });
            if (_closed || files.Count != 1) return;
            await using var stream = await files[0].OpenReadAsync();
            if (_closed) return;
            using var reader = new StreamReader(stream);
            var text = new char[OrganizationImportDocumentReader.MaximumCharacters + 1];
            var count = await reader.ReadBlockAsync(text, _lifetime.Token);
            if (_closed) return;
            LoadOrganizationText(new string(text, 0, count));
        });
    }

    internal void LoadOrganizationText(string text)
    {
        if (_closed || _term is not { } term || _catalog is not { } catalog) return;
        var file = OrganizationImportDocumentReader.ReadLegacy(text);
        var current = CurrentGrade();
        if (file.Declaration is { } declared)
        {
            var declarations = new List<string>();
            if (declared.SchoolId is { } schoolId && schoolId != catalog.School.Id ||
                declared.SchoolCode is { } schoolCode && !SameCode(schoolCode, catalog.School.Code) ||
                declared.SchoolName is { } schoolName && schoolName.Trim() != catalog.School.Name)
                declarations.Add($"学校　所选：{catalog.School.Name}（{catalog.School.Code}）　文件：{declared.SchoolName} {declared.SchoolCode} {declared.SchoolId}".TrimEnd());
            if (declared.TermId is { } termId && termId != term.Id ||
                declared.AcademicYear is { } year && year != term.AcademicYear ||
                declared.Semester is { } semester && semester != term.Semester ||
                declared.TermName is { } termName && termName.Trim() != term.Name)
                declarations.Add($"学期　所选：{term.Name}（{term.AcademicYear}/{term.Semester}）　文件：{declared.TermName} {declared.AcademicYear}/{declared.Semester} {declared.TermId}".TrimEnd());
            if (current.Code.Length > 0 && file.Grade.Code.Length > 0 && !SameCode(current.Code, file.Grade.Code) ||
                !_archivedCorrection && current.Name.Length > 0 && file.Grade.Name.Length > 0 && current.Name != file.Grade.Name)
                declarations.Add($"年级　所选：{current.Name}（{current.Code}）　文件：{file.Grade.Name}（{file.Grade.Code}）");
            if (declarations.Count > 0)
            {
                _fileMismatch = string.Join("\n", declarations);
                SetStage(WorkStage.Table);
                InvalidateReview();
                Status("文件声明与所选目标不同，原表已保留。");
                Render();
                FocusLater(OrgKeepOriginal);
                return;
            }
        }
        var target = new OrganizationGrade(current.Code.Length == 0 ? file.Grade.Code : current.Code,
            current.Name.Length == 0 ? file.Grade.Name : current.Name, current.Code.Length == 0 ? file.Grade.SortOrder : current.SortOrder);
        if (_archivedCorrection && SameCode(target.Code, file.Grade.Code)) target = file.Grade;
        OfferReplacement(file with { Grade = target }, "所选 JSON 文件");
    }

    private static bool SameCode(string first, string second) => string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);

    private void KeepOrganizationDraftClicked(object? sender, RoutedEventArgs args)
    {
        _fileMismatch = null;
        Status("已保留原表，请重新预览。");
        Render();
        FocusLater(OrgOpenFile);
    }

    private void LoadExampleClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanEdit) return;
        var grade = CurrentGrade();
        if (string.IsNullOrEmpty(grade.Code) != string.IsNullOrEmpty(grade.Name))
        {
            Status("请填写完整的目标年级代码和名称，再载入示例。");
            (grade.Code.Length == 0 ? OrgGradeCode : OrgGradeName).Focus();
            return;
        }
        var example = OrganizationImportDocumentReader.SevenClassExample(grade);
        OfferReplacement(example, "七班制示例");
    }

    private void OfferReplacement(OrganizationImportDocument document, string source)
    {
        if (_dirty && (_classes.Count + _groups.Count + _subjects.Count > 0 ||
            _archivedCorrection && CurrentGrade() != document.Grade))
        {
            _replacement = (document, source);
            OrgReplacementMessage.Text = $"载入{source}会替换此页尚未导入的表格。原表未改变。";
            SetStage(WorkStage.Table);
            Render();
            FocusLater(OrgCancelReplacement);
        }
        else ApplyReplacement(document, source);
    }

    private void ApplyReplacement(OrganizationImportDocument document, string source)
    {
        _replacement = null;
        _fileMismatch = null;
        SetStage(WorkStage.Table);
        SetDocument(document);
        OrgSections.SelectedIndex = 0;
        Status($"已载入{source}。请预览并核对差异。");
        FocusLater(OrgPreview);
    }

    private void ConfirmReplacementClicked(object? sender, RoutedEventArgs args)
    {
        if (_replacement is { } incoming && !_busy) ApplyReplacement(incoming.Document, incoming.Source);
    }

    private void CancelReplacementClicked(object? sender, RoutedEventArgs args) => CancelReplacement();

    private void CancelReplacement()
    {
        var trigger = _replacement?.Source == "七班制示例" ? OrgExample : OrgOpenFile;
        _replacement = null;
        Status("已取消替换，原表和目标已保留。");
        Render();
        FocusLater(trigger);
    }

    private void FocusLater(Control target) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && target.IsEffectivelyVisible && target.IsEffectivelyEnabled)
        {
            target.BringIntoView();
            target.Focus(NavigationMethod.Tab);
        }
    }, DispatcherPriority.Loaded);

    private async void PreviewClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanEdit || HasUnresolvedTarget || _fileMismatch is not null || !_archivedCorrection && _classes.Count + _groups.Count == 0) return;
        await RunAsync(async () =>
        {
            var draft = Snapshot();
            var review = await _client.PreviewOrganizationImportAsync(_school.Id, draft, _lifetime.Token);
            if (_closed) return;
            ShowPreview(review, []);
            _reviewed = review.CanApply && review.Changes.Any(row => row.Action != "KEEP");
            Status(!review.CanApply ? "请修正阻断项。选择问题并定位到表格。" :
                _reviewed ? "请核对差异，再确认导入整个年级。" : "表格已与服务器一致，无需保存。");
        });
    }

    private void ShowPreview(OrganizationPreview review, IReadOnlyList<string> changedKeys, OrganizationPreview? prior = null)
    {
        _preview = review;
        _lastLocatedDifference = null;
        var precise = new HashSet<OrganizationDifference>();
        var preciseKeys = new HashSet<string>(StringComparer.Ordinal);
        if (_archivedCorrection && prior is not null)
        {
            var previousRows = prior.Changes.ToLookup(row => (row.Key, row.Path, row.Object));
            foreach (var row in review.Changes.Where(row => changedKeys.Contains(row.Key)))
            {
                if (previousRows[(row.Key, row.Path, row.Object)].Contains(row)) continue;
                precise.Add(row);
                preciseKeys.Add(row.Key);
            }
        }
        _visibleReview = review.Changes.Select(row =>
            changedKeys.Contains(row.Key) && (!preciseKeys.Contains(row.Key) || precise.Contains(row))
                ? row with { NeedsReview = true } : row).ToArray();
        if (prior is not null)
        {
            var missing = changedKeys.Where(key => !_visibleReview.Any(row => row.Key == key));
            _visibleReview = [.. _visibleReview, .. missing.Select(key =>
            {
                var previous = prior.Changes.FirstOrDefault(row => row.Key == key);
                var source = _archivedCorrection && key.StartsWith("class:", StringComparison.Ordinal)
                    ? _catalog?.AdministrativeClasses.FirstOrDefault(row => row.Code == key["class:".Length..]) : null;
                return new OrganizationDifference(key, previous?.Path ?? "", "KEEP",
                    previous?.Object ?? (source is null ? key : $"{source.Name}（{source.Code}）"),
                    previous?.Current, null, "服务器上的原确认依据已变化，请重新预览。", true);
            })];
        }
        OrgDifferenceFilter.SelectedIndex = _visibleReview.Any(row => row.Action == "BLOCK" || row.NeedsReview) ? 4 : 0;
        OrgApprove.IsChecked = false;
        SetStage(WorkStage.Difference);
        ShowDifferences();
        FocusSelectedDifference();
    }

    private void FocusSelectedDifference()
    {
        if (OrgDifferences.SelectedItem is { } selected) OrgDifferences.ScrollIntoView(selected);
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || _stage != WorkStage.Difference || !OrgDifferences.IsEffectivelyVisible) return;
            var target = OrgDifferences.SelectedItem is { } row
                ? OrgDifferences.ContainerFromItem(row) ?? OrgDifferences : OrgDifferences;
            target.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void ShowDifferences()
    {
        var filter = OrgDifferenceFilter.SelectedIndex;
        var displayed = _visibleReview.Where(row => filter switch
        {
            1 => row.Action == "ADD", 2 => row.Action == "CHANGE", 3 => row.Action == "REMOVE",
            4 => row.Action == "BLOCK" || row.NeedsReview, 5 => row.Action == "KEEP", _ => true
        }).ToArray();
        var declarationsOnly = displayed.Length > 0 && displayed.All(row => row.Path.StartsWith("declaration.", StringComparison.Ordinal));
        OrgBeforeHeader.Text = declarationsOnly ? "所选目标" : "现在";
        OrgAfterHeader.Text = declarationsOnly ? "文件声明" : "导入后";
        OrgDifferences.ItemsSource = displayed;
        OrgDifferences.SelectedIndex = displayed.Length > 0 ? 0 : -1;
        (string Code, string Label)[] kinds = [("ADD", "新增"), ("CHANGE", "修改"), ("REMOVE", "移除"), ("BLOCK", "阻断")];
        var counts = kinds
            .Select(kind => (kind.Label, Count: _visibleReview.Count(row => row.Action == kind.Code)))
            .Where(item => item.Count > 0).Select(item => $"{item.Label} {item.Count}");
        var summary = string.Join(" · ", counts);
        var shown = filter > 0 ? $"筛选出 {displayed.Length} 项" : $"共 {displayed.Length} 项";
        OrgDifferenceCount.Text = summary.Length > 0 ? $"{shown}　｜　全表 {summary}" : shown;
        OrgLocateError.IsEnabled = displayed.Length > 0;
    }

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (!_busy && _retryMissingReceipt && Pending is { } pending)
        {
            await SubmitAsync(pending);
            return;
        }
        if (!CanEdit || HasUnresolvedTarget || !_reviewed || OrgApprove.IsChecked != true || _preview is not { CanApply: true } preview) return;
        var operation = new Operation(Snapshot(), preview, Guid.NewGuid());
        RememberPending(operation);
        await SubmitAsync(operation);
    }

    private Task SubmitAsync(Operation operation) => RunAsync(async () =>
    {
        _retryMissingReceipt = false;
        var preview = operation.Preview;
        try
        {
            var result = await _client.SaveOrganizationImportAsync(_school.Id, operation.Draft, preview,
                operation.RequestId, _lifetime.Token);
            if (_closed) return;
            ClearPending(operation);
            if (result.Receipt is { } receipt) ShowReceipt(receipt);
            else if (result.Preview is { } current)
            {
                _reviewed = false;
                ShowPreview(current, result.ChangedKeys ?? [], preview);
                Status(result.State == "STALE" ? "服务器上的资料已变更。本次没有保存任何更改，原表已保留。请核对后重新预览。" :
                    "本次没有保存任何更改。请修正阻断项后重新预览。");
            }
        }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
        {
            if (!_closed) await VerifyReceiptAsync(operation);
        }
        catch (ConsoleApiException)
        {
            ClearPending(operation);
            _reviewed = false;
            throw;
        }
    }, writing: true);

    private async void VerifyClicked(object? sender, RoutedEventArgs args)
    {
        if (Pending is { } operation) await RunAsync(() => VerifyReceiptAsync(operation));
    }

    private async Task VerifyReceiptAsync(Operation operation)
    {
        _retryMissingReceipt = false;
        Status("正在按原请求只读核对服务器回执。");
        var receipt = await _client.GetOrganizationImportReceiptAsync(_school.Id, operation.Draft, operation.RequestId, _lifetime.Token);
        if (_closed) return;
        if (receipt is null)
        {
            _retryMissingReceipt = !_scopeUnavailable;
            Status(_retryMissingReceipt ? "暂未找到本次回执。可继续只读查询，或按原编号重试。" :
                "服务器暂未返回本次回执。结果仍待核对，请稍后再查原请求。");
        }
        else
        {
            ClearPending(operation);
            ShowReceipt(receipt);
            Status("已从服务器找回本次导入回执。");
        }
    }

    private void ShowReceipt(OrganizationImportReceipt receipt)
    {
        _receipt = receipt;
        ActiveTermChanged |= receipt.TermStatus == "ACTIVE";
        _preview = null;
        _reviewed = false;
        _dirty = false;
        OrgApprove.IsChecked = false;
        OrgReceiptCaption.Text = $"{(receipt.TermStatus == "ARCHIVED" ? "教学组织已更正，教师直接访问可另行办理" : "服务器已确认")} · " +
            $"{receipt.AdministrativeClasses.Length} 个行政班 · {receipt.CourseGroups.Length} 个教学班 · {receipt.Subjects.Length} 个学科";
        var classes = _classes.ToDictionary(row => row.Code.Trim().ToUpperInvariant(), row => row.Name);
        var groups = _groups.ToDictionary(row => row.Code.Trim().ToUpperInvariant(), row => row.Name);
        OrgReceiptClasses.ItemsSource = receipt.AdministrativeClasses
            .Select(row => new ReceiptChoice(row.Id, $"{classes.GetValueOrDefault(row.Code, row.Code)}（{row.Code}）", false))
            .Concat(receipt.CourseGroups.Select(row => new ReceiptChoice(row.Id, $"{groups.GetValueOrDefault(row.Code, row.Code)}（{row.Code}）· 走班", true))).ToArray();
        OrgReceiptClasses.SelectedIndex = OrgReceiptClasses.ItemCount > 0 ? 0 : -1;
        OrgContinue.Content = receipt.TermStatus switch
        {
            "DRAFT" => "进入启用前检查",
            "ARCHIVED" => "查看历史任课",
            _ => "进入所选班级任课"
        };
        SetStage(WorkStage.Result);
        Status("本次导入已由服务器确认。");
        FocusLater(OrgReceiptClasses.ItemCount > 0 ? OrgReceiptClasses : OrgContinue);
    }

    private async void ContinueClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _receipt is not { } receipt || OpenReceiptTarget is not { } open) return;
        if (receipt.TermStatus == "DRAFT") GoToDraftId = receipt.TermId;
        else if (OrgReceiptClasses.SelectedItem is ReceiptChoice room) GoToTeaching = (receipt.TermId, room.Id, room.IsGroup);
        else return;
        try { await RunAsync(() => open(this)); }
        finally { GoToDraftId = null; GoToTeaching = null; }
        FocusLater(OrgContinue);
    }

    private async void AllocateTeachersClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _scopeUnavailable || Pending is not null || OpenTeacherAllocation is not { } open) return;
        if (_receipt is { } receipt) GoToTeacherAllocation = (receipt.TermId, receipt.GradeId);
        else if (_archivedCorrection && !_dirty && _term is { } term && OrgGrade.SelectedItem is GradeChoice { Id: { } gradeId })
            GoToTeacherAllocation = (term.Id, gradeId);
        else return;
        var target = GoToTeacherAllocation.Value;
        try { await RunAsync(() => open(target.TermId, target.GradeId, SelectedReceiptWorkspace)); }
        finally { GoToTeacherAllocation = null; }
        FocusLater(OrgTeacherAllocation);
    }

    private async void HistoryClassClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _receipt is not { TermStatus: "ARCHIVED" } receipt || OpenReceiptTarget is not { } open ||
            OrgReceiptClasses.SelectedItem is not ReceiptChoice { IsGroup: false } room) return;
        GoToHistoryClass = (receipt.TermId, receipt.GradeId, room.Id);
        try { await RunAsync(() => open(this)); }
        finally { GoToHistoryClass = null; }
        FocusLater(OrgHistoryClass);
    }

    private async void SchoolSubjectsClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanEdit || !_archivedCorrection || OpenSubjects is not { } open) return;
        await RunAsync(async () =>
        {
            var subject = await open(HostWindow);
            if (_closed) return;
            InvalidateReview();
            await LoadAsync(_fixedTermId);
            if (subject is not null && !_closed) OrgExistingSubject.SelectedItem = _catalog?.Subjects.FirstOrDefault(row => row.Id == subject.Id);
            if (!_closed) Status("已刷新全校学科目录，原表保留。请重新预览。");
        });
        if (!_closed) OrgSchoolSubjects.Focus();
    }

    private void Edited()
    {
        if (_binding || _closed) return;
        if (_preview is not null || _receipt is not null) Status("表格已修改，请重新预览。");
        ClearDisplayedReceipt();
        _dirty = true;
        _discard = false;
        InvalidateReview();
        Render();
    }

    private void ClearDisplayedReceipt()
    {
        if (_receipt is null) return;
        _receipt = null;
        if (_stage == WorkStage.Result) SetStage(WorkStage.Table);
        OrgReceiptClasses.ItemsSource = null;
    }

    private void InvalidateReview()
    {
        _reviewed = false;
        _preview = null;
        OrgApprove.IsChecked = false;
        if (_visibleReview.Length > 0)
        {
            _visibleReview = [];
            ShowDifferences();
        }
    }

    private async Task RunAsync(Func<Task> action, bool writing = false)
    {
        if (_busy || _closed) return;
        _busy = true;
        _writing = writing;
        Render();
        try { await action(); }
        catch (Exception error) when (_closed && error is OperationCanceledException or ConsoleApiException or ObjectDisposedException or IOException) { }
        catch (ConsoleApiException error) when (!_closed && error.Code == "ORG_IMPORT_TERM_CHANGED")
        {
            InvalidateReview();
            SetStage(WorkStage.Table);
            if (_fixedTermId is not null) _scopeUnavailable = true;
            else try { await LoadAsync(null, true); }
            catch (OperationCanceledException) when (_closed) { }
            catch (ConsoleApiException loadError) { if (!_closed) SessionExpired?.Invoke(loadError); }
            if (!_closed) Status(_archivedCorrection ?
                "目标已不再是归档学期，本次没有保存任何更改。原表保留供核对，请返回学期目录。" :
                "目标学期状态已变化，本次没有保存任何更改。表格已保留，请重新选定学期后预览。");
        }
        catch (ConsoleApiException error) when (!_closed)
        {
            if (error.StatusCode == 403)
            {
                _scopeUnavailable = true;
                InvalidateReview();
                ClearDisplayedReceipt();
            }
            SessionExpired?.Invoke(error);
            if (!_closed) Status(Pending is null ? error.Message : "回执暂不可读，保留原请求。请重试只读核对。");
        }
        catch (ArgumentException error) when (!_closed) { Status(error.Message); }
        catch (IOException) when (!_closed) { Status("无法读取所选文件。请核对文件位置和访问权限。"); }
        finally
        {
            _busy = false;
            _writing = false;
            if (!_closed) Render();
        }
    }

    private void Render()
    {
        if (_closed || OrgStatus is null) return;
        var pending = Pending is not null;
        bool table = _stage == WorkStage.Table;
        bool difference = _stage == WorkStage.Difference;
        bool result = _stage == WorkStage.Result;
        bool confirming = _replacement is not null || _discard;
        bool editingTable = table && !_showExcel;
        bool edit = CanEdit && !confirming;
        bool canPreview = !pending && !HasUnresolvedTarget && _fileMismatch is null &&
            (_archivedCorrection ? !string.IsNullOrWhiteSpace(OrgGradeCode.Text) : _classes.Count + _groups.Count > 0);
        OrgStageLabel.Text = _showExcel ? "录入" : _stage switch
        {
            WorkStage.Difference => "核对差异", WorkStage.Result => "导入结果", _ => "编辑导入表"
        };
        OrgTargetFields.IsVisible = OrgDocumentActions.IsVisible = OrgSections.IsVisible = editingTable;
        OrgBody.IsEnabled = !confirming;
        OrgDifferencePane.IsVisible = difference;
        OrgResultPane.IsVisible = result;
        OrgExcelPane.IsVisible = _showExcel;
        OrgTerm.IsEnabled = _fixedTermId is null && !_busy && !pending && !confirming && _catalog is not null;
        OrgGrade.IsEnabled = OrgGradeName.IsEnabled = OrgGradeOrder.IsEnabled = edit;
        OrgGradeCode.IsEnabled = edit && (!_archivedCorrection || (OrgGrade.SelectedItem as GradeChoice)?.Id is null);
        OrgSchoolSubjects.IsEnabled = edit && OpenSubjects is not null;
        OrgOpenFile.IsEnabled = OrgExample.IsEnabled = OrgExcel.IsEnabled = edit;
        OrgClassTable.IsEnabled = OrgClassEditor.IsEnabled = OrgGroupTable.IsEnabled = OrgGroupEditor.IsEnabled = edit;
        OrgExistingSubject.IsEnabled = OrgSubjects.IsEnabled = OrgAddSubject.IsEnabled = edit;
        OrgPreview.IsVisible = !pending && (editingTable || difference && !_reviewed);
        OrgPreview.Content = difference ? "重新预览" : "预览导入差异";
        UpdateDifferenceAction();
        OrgFileMismatch.IsVisible = editingTable && _fileMismatch is not null;
        OrgFileMismatchText.Text = _fileMismatch;
        OrgKeepOriginal.IsEnabled = !confirming && !_busy;
        OrgPreview.IsEnabled = edit && canPreview;
        OrgVerify.IsVisible = pending;
        OrgVerify.IsEnabled = !_busy;
        OrgApprove.IsVisible = _reviewed && !pending;
        OrgApprove.IsEnabled = edit;
        OrgSave.IsVisible = pending ? _retryMissingReceipt : difference && _reviewed;
        OrgSave.IsEnabled = !_busy && (pending ? _retryMissingReceipt : difference && edit && canPreview && _reviewed && OrgApprove.IsChecked == true);
        OrgSave.Content = pending ? "按原编号重试" : _archivedCorrection
            ? $"确认更正 {_visibleReview.Count(row => row.Action is "ADD" or "CHANGE" or "REMOVE")} 项" : "确认导入";
        OrgContinue.IsVisible = result && _receipt is not null;
        OrgHistoryClass.IsVisible = OrgContinue.IsVisible && _archivedCorrection && OrgReceiptClasses.SelectedItem is ReceiptChoice { IsGroup: false };
        OrgTeacherAllocation.IsVisible = OpenTeacherAllocation is not null && (OrgContinue.IsVisible ||
            editingTable && _archivedCorrection && !pending && OrgGrade.SelectedItem is GradeChoice { Id: not null });
        OrgTeacherAllocation.IsEnabled = !_busy && !_scopeUnavailable && (!_archivedCorrection || !_dirty);
        OrgContinue.IsEnabled = !_busy && (_receipt?.TermStatus == "DRAFT" || OrgReceiptClasses.SelectedItem is ReceiptChoice);
        OrgBackToTable.IsVisible = (difference || result) && !pending;
        OrgBackToTable.IsEnabled = !_busy;
        OrgBackToReview.IsVisible = editingTable && _preview is not null && !pending;
        OrgBackToReview.IsEnabled = !_busy;
        OrgClose.IsEnabled = !_writing;
        OrgClose.Content = "返回";
        OrgReplaceActions.IsVisible = _replacement is not null;
        OrgDiscardActions.IsVisible = _discard;
        OrgStandardActions.IsVisible = !confirming && !_showExcel;
        OrgFooter.Text = $"{_classes.Count} 个行政班 · {_groups.Count} 个教学班";
        UpdatePendingActions();
    }

    private void SetStage(WorkStage stage)
    {
        _stage = stage;
        Render();
    }

    private void BackToTableClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || Pending is not null) return;
        SetStage(WorkStage.Table);
        FocusLater(OrgSections.SelectedIndex switch { 0 => OrgClasses, 1 => OrgGroups, _ => OrgSubjects });
    }

    private void BackToReviewClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _preview is null) return;
        SetStage(WorkStage.Difference);
        FocusSelectedDifference();
    }

    private void DifferenceRowChanged(object? sender, SelectionChangedEventArgs args) => UpdateDifferenceAction();

    private void UpdateDifferenceAction()
    {
        if (OrgLocateError is null || OrgPreview is null || _closed) return;
        bool needsCorrection = _stage == WorkStage.Difference && _visibleReview.Any(row => row.Action == "BLOCK");
        OrgPreview.Classes.Set("accent", !needsCorrection);
        OrgLocateError.Classes.Set("accent", needsCorrection);
        OrgLocateError.Content = needsCorrection && OrgDifferences.SelectedItem is OrganizationDifference { Action: "BLOCK" }
            ? "去修正这项" : "定位所选行";
    }

    private void Status(string value)
    {
        OrgStatus.Text = value;
        OrgStatus.IsVisible = !string.IsNullOrWhiteSpace(value);
    }
    private void ApprovalChanged(object? sender, RoutedEventArgs args) { if (_client is not null) Render(); }
    private void DifferenceFilterChanged(object? sender, SelectionChangedEventArgs args) { if (_client is not null) ShowDifferences(); }
    private void ReceiptClassSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (_client is null || _closed) return;
        SelectedReceiptWorkspace = (OrgReceiptClasses.SelectedItem as ReceiptChoice)?.Id;
        Render();
    }
    private void SectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_client is null || !ReferenceEquals(args.Source, OrgSections)) return;
        if (OrgSections.SelectedIndex == 1) RebuildGroupEditor();
        Render();
    }
    private void CloseClicked(object? sender, RoutedEventArgs args) => CloseRequested?.Invoke();

    private void KeepEditingClicked(object? sender, RoutedEventArgs args)
    {
        _discard = false;
        Render();
        FocusLater(_stage == WorkStage.Difference ? OrgDifferences : OrgSections);
    }

    private void DiscardClicked(object? sender, RoutedEventArgs args)
    {
        _allowClose = true;
        CloseRequested?.Invoke();
    }
}
