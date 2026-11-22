using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class OrganizationImportWindow : Window
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

    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, Operation>> PendingByClient = new();
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
    private int _gradeOrder;

    public OrganizationImportWindow() => InitializeComponent();

    internal OrganizationImportWindow(ConsoleClient client, School school, string? termId, bool archivedCorrection = false) : this()
    {
        _client = client;
        _school = school;
        _fixedTermId = termId;
        _archivedCorrection = archivedCorrection;
        if (archivedCorrection)
        {
            Title = "归档学期批量更正";
            OrgHeading.Text = "归档学期批量更正 · 教学组织";
            OrgGradeOrderField.IsVisible = OrgSchoolSubjects.IsVisible = true;
            OrgGradeOrder.ValueChanged += (_, _) => TargetGradeEdited(null, null!);
            OrgApprove.Content = "我已核对所选归档学期及逐项变化";
            OrgTeacherAllocation.Content = "核对本年级教师访问";
        }
        OrgScope.Text = school.Name;
        OrgDifferenceFilter.ItemsSource = new[] { "所有差异", "新增", "修改", "移除", "阻断与待复核", "一致" };
        OrgDifferenceFilter.SelectedIndex = 0;
        RebuildTables();
        Render();
        SizeChanged += (_, _) => ArrangeOrganizationLayout();
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            ArrangeOrganizationLayout();
            await RunAsync(async () =>
            {
                if (Pending is { } pending)
                {
                    try { await LoadAsync(pending.Draft.TermId); }
                    catch (ConsoleApiException error) when (error.Code == "ORG_IMPORT_TERM_CHANGED")
                    { if (_archivedCorrection) _scopeUnavailable = true; else await LoadAsync(null, true); }
                    if (_closed) return;
                    SetDocument(new(pending.Draft.Grade, pending.Draft.Subjects, pending.Draft.AdministrativeClasses,
                        pending.Draft.CourseGroups, pending.Draft.Declaration));
                    await VerifyReceiptAsync(pending);
                }
                else await LoadAsync(termId);
            });
        };
        Closing += (_, args) =>
        {
            args.Cancel = !_allowClose && (_writing || _dirty && Pending is null && !_discard);
            if (args.Cancel && !_writing)
            {
                _discard = true;
                Status("还有未导入的表格。再次点「放弃并返回」关闭，继续编辑会取消放弃。");
                OrgClose.Content = "放弃并返回";
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    private Dictionary<string, Operation> Operations => PendingByClient.GetOrCreateValue(_client);
    private string PendingKey => _archivedCorrection ? _school.Id + "/" + _fixedTermId : _school.Id;
    private Operation? Pending => Operations.GetValueOrDefault(PendingKey);
    private bool CanEdit => _catalog is not null && _term is not null && !_busy && !_scopeUnavailable && Pending is null;
    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal Func<Window, Task<SchoolSubject?>>? OpenSubjects { get; init; }
    internal bool ActiveTermChanged { get; private set; }
    internal string? GoToDraftId { get; private set; }
    internal (string TermId, string WorkspaceId, bool IsGroup)? GoToTeaching { get; private set; }
    internal (string TermId, string GradeId)? GoToTeacherAllocation { get; private set; }
    internal (string TermId, string GradeId, string WorkspaceId)? GoToHistoryClass { get; private set; }
    internal string? SelectedReceiptWorkspace => (OrgReceiptClasses.SelectedItem as ReceiptChoice)?.Id;

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
        OrgScope.Text = $"{_school.Name} / {term} / {(string.IsNullOrEmpty(grade) ? "请选择或填写年级" : grade + "（" + OrgGradeCode.Text?.Trim() + "）")}";
        if (_archivedCorrection) OrgScope.Text += $"　全校当前启用　{_catalog?.Terms.FirstOrDefault(row => row.Status == "ACTIVE")?.Name ?? "暂无"}";
    }

    private async void TermSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_archivedCorrection || _binding || _busy || Pending is not null || OrgTerm.SelectedItem is not OrganizationTerm term || _term?.Id == term.Id) return;
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
        if (_archivedCorrection)
        {
            UpdateScope();
            RebuildGroupEditor();
            ClearDisplayedReceipt();
            InvalidateReview();
            Render();
            return;
        }
        TargetGradeEdited(null, null!);
    }

    private void TargetGradeEdited(object? sender, TextChangedEventArgs args)
    {
        if (_binding || _catalog is null) return;
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
        UpdateScope();
        Edited();
    }

    private async void OpenFileClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanEdit) return;
        await RunAsync(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "打开旧组织 JSON", AllowMultiple = false,
                FileTypeFilter = [new("组织配置 JSON") { Patterns = ["*.json"] }]
            });
            if (_closed || files.Count != 1) return;
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var text = new char[OrganizationImportDocumentReader.MaximumCharacters + 1];
            var count = await reader.ReadBlockAsync(text, _lifetime.Token);
            if (_closed) return;
            LoadOrganizationText(new string(text, 0, count));
        });
    }

    internal void LoadOrganizationText(string text)
    {
        var file = OrganizationImportDocumentReader.ReadLegacy(text);
        var current = CurrentGrade();
        if (_archivedCorrection && _term is { } term && _catalog is { } catalog && file.Declaration is { } declared)
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
            if (!string.IsNullOrWhiteSpace(current.Code) && !SameCode(current.Code, file.Grade.Code))
                declarations.Add($"年级　所选：{current.Name}（{current.Code}）　文件：{file.Grade.Name}（{file.Grade.Code}）");
            if (declarations.Count > 0)
            {
                _fileMismatch = string.Join("\n", declarations);
                InvalidateReview();
                Status("文件声明与所选目标不同，原表已保留。");
                Render();
                return;
            }
        }
        _fileMismatch = null;
        var target = current.Code.Length > 0 || current.Name.Length > 0 ? current : file.Grade;
        if (_archivedCorrection && string.Equals(target.Code, file.Grade.Code, StringComparison.OrdinalIgnoreCase)) target = file.Grade;
        SetDocument(file with { Grade = target });
        OrgSections.SelectedIndex = 0;
        var mismatch = !string.Equals(target.Code.Trim(), file.Grade.Code.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(target.Name.Trim(), file.Grade.Name.Trim(), StringComparison.Ordinal);
        Status(mismatch ? $"所选目标：{target.Name}（{target.Code}）。文件声明：{file.Grade.Name}（{file.Grade.Code}）。请选择对应年级或换文件。" :
            "已载入文件。预览时核对其中声明的学校、学期和年级。");
    }

    private static bool SameCode(string first, string second) => string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);

    private void KeepOrganizationDraftClicked(object? sender, RoutedEventArgs args)
    {
        _fileMismatch = null;
        Status("已保留原表，请重新预览。");
        Render();
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
        SetDocument(example);
        OrgSections.SelectedIndex = 0;
        Status("示例已绑定页头所选目标。请核对每班的实际学科与来源。");
    }

    private async void PreviewClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanEdit || _fileMismatch is not null || !_archivedCorrection && _classes.Count + _groups.Count == 0) return;
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
        OrgSections.SelectedIndex = 3;
        ShowDifferences();
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed && OrgSections.SelectedIndex == 3 && OrgDifferences.SelectedItem is { } selected &&
                OrgDifferences.ContainerFromItem(selected) is Control item)
                item.Focus(NavigationMethod.Tab);
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
        OrgDifferenceCount.Text = $"新增 {_visibleReview.Count(row => row.Action == "ADD")} · 修改 {_visibleReview.Count(row => row.Action == "CHANGE")} · " +
            $"移除 {_visibleReview.Count(row => row.Action == "REMOVE")} · 阻断 {_visibleReview.Count(row => row.Action == "BLOCK")}";
        OrgLocateError.IsEnabled = displayed.Length > 0;
    }

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (!_busy && _retryMissingReceipt && Pending is { } pending)
        {
            await SubmitAsync(pending);
            return;
        }
        if (!CanEdit || !_reviewed || OrgApprove.IsChecked != true || _preview is not { CanApply: true } preview) return;
        var operation = new Operation(Snapshot(), preview, Guid.NewGuid());
        Operations[PendingKey] = operation;
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
            Operations.Remove(PendingKey);
            if (result.Receipt is { } receipt) ShowReceipt(receipt);
            else if (result.Preview is { } current)
            {
                _reviewed = false;
                ShowPreview(current, result.ChangedKeys ?? [], preview);
                Status(result.State == "STALE" ? "服务器已有变化，本次零写入。草稿已保留，请定位变化并重新预览。" :
                    "本次零写入。请修正阻断项后重新预览。");
            }
        }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
        {
            if (!_closed) await VerifyReceiptAsync(operation);
        }
        catch (ConsoleApiException)
        {
            Operations.Remove(PendingKey);
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
            _retryMissingReceipt = _archivedCorrection && !_scopeUnavailable;
            Status(_retryMissingReceipt ? "暂未找到本次回执。可继续只读查询，或按原编号重试。" :
                "服务器暂未返回本次回执。结果仍待核对，请稍后再查原请求。");
        }
        else
        {
            Operations.Remove(PendingKey);
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
        OrgResultTab.IsVisible = true;
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
        OrgSections.SelectedIndex = 4;
        Status("本次导入已由服务器确认。");
    }

    private void ContinueClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _receipt is not { } receipt) return;
        if (receipt.TermStatus == "DRAFT") GoToDraftId = receipt.TermId;
        else if (OrgReceiptClasses.SelectedItem is ReceiptChoice room) GoToTeaching = (receipt.TermId, room.Id, room.IsGroup);
        else return;
        _allowClose = true;
        Close();
    }

    private void AllocateTeachersClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _scopeUnavailable || Pending is not null) return;
        if (_receipt is { } receipt) GoToTeacherAllocation = (receipt.TermId, receipt.GradeId);
        else if (_archivedCorrection && !_dirty && _term is { } term && OrgGrade.SelectedItem is GradeChoice { Id: { } gradeId })
            GoToTeacherAllocation = (term.Id, gradeId);
        else return;
        _allowClose = true;
        Close();
    }

    private void HistoryClassClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _receipt is not { TermStatus: "ARCHIVED" } receipt || OrgReceiptClasses.SelectedItem is not ReceiptChoice { IsGroup: false } room) return;
        GoToHistoryClass = (receipt.TermId, receipt.GradeId, room.Id);
        _allowClose = true;
        Close();
    }

    private async void SchoolSubjectsClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanEdit || !_archivedCorrection || OpenSubjects is not { } open) return;
        await RunAsync(async () =>
        {
            var subject = await open(this);
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
        if (_binding) return;
        if (_preview is not null || _receipt is not null) Status("表格已修改，请重新预览。");
        ClearDisplayedReceipt();
        _dirty = true;
        _discard = false;
        OrgClose.Content = "返回";
        InvalidateReview();
        Render();
    }

    private void ClearDisplayedReceipt()
    {
        if (_receipt is null) return;
        _receipt = null;
        if (OrgSections.SelectedIndex == 4) OrgSections.SelectedIndex = 0;
        OrgResultTab.IsVisible = false;
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
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error) when (!_closed && error.Code == "ORG_IMPORT_TERM_CHANGED")
        {
            InvalidateReview();
            if (_archivedCorrection) _scopeUnavailable = true;
            else try { await LoadAsync(null, true); } catch (ConsoleApiException loadError) { SessionExpired?.Invoke(loadError); }
            if (!_closed) Status(_archivedCorrection ?
                "目标已不再是归档学期，本次零写入。原表保留供核对，请返回学期目录。" :
                "目标学期状态已变化，本次零写入。表格已保留，请重新选定学期后预览。");
        }
        catch (ConsoleApiException error) when (!_closed)
        {
            if (_archivedCorrection && error.StatusCode == 403)
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
        if (OrgStatus is null) return;
        var pending = Pending is not null;
        var edit = CanEdit;
        OrgTerm.IsEnabled = !_archivedCorrection && !_busy && !pending && _catalog is not null;
        OrgGrade.IsEnabled = OrgGradeName.IsEnabled = OrgGradeOrder.IsEnabled = edit;
        OrgGradeCode.IsEnabled = edit && (!_archivedCorrection || (OrgGrade.SelectedItem as GradeChoice)?.Id is null);
        OrgSchoolSubjects.IsEnabled = edit && OpenSubjects is not null;
        OrgOpenFile.IsEnabled = OrgExample.IsEnabled = OrgExcel.IsEnabled = edit;
        OrgClassTable.IsEnabled = OrgClassEditor.IsEnabled = OrgGroupTable.IsEnabled = OrgGroupEditor.IsEnabled = edit;
        OrgExistingSubject.IsEnabled = OrgSubjects.IsEnabled = OrgAddSubject.IsEnabled = edit;
        OrgPreview.IsVisible = !pending && (!_archivedCorrection || OrgSections.SelectedIndex != 4);
        OrgFileMismatch.IsVisible = _fileMismatch is not null;
        OrgFileMismatchText.Text = _fileMismatch;
        ToolTip.SetTip(OrgFileMismatchText, _fileMismatch);
        OrgPreview.IsEnabled = edit && _fileMismatch is null &&
            (_archivedCorrection ? !string.IsNullOrWhiteSpace(OrgGradeCode.Text) : _classes.Count + _groups.Count > 0);
        OrgVerify.IsVisible = pending;
        OrgVerify.IsEnabled = !_busy;
        OrgApprove.IsEnabled = edit && _reviewed;
        OrgSave.IsVisible = OrgSections.SelectedIndex != 4 && (!pending || _retryMissingReceipt || !_archivedCorrection);
        OrgSave.IsEnabled = !_busy && (pending ? _retryMissingReceipt : edit && _reviewed && OrgApprove.IsChecked == true);
        if (_archivedCorrection)
            OrgSave.Content = pending ? "按原编号重试" : $"确认更正 {_visibleReview.Count(row => row.Action is "ADD" or "CHANGE" or "REMOVE")} 项";
        OrgContinue.IsVisible = OrgSections.SelectedIndex == 4 && _receipt is not null;
        OrgHistoryClass.IsVisible = OrgContinue.IsVisible && _archivedCorrection && OrgReceiptClasses.SelectedItem is ReceiptChoice { IsGroup: false };
        OrgTeacherAllocation.IsVisible = OrgContinue.IsVisible || _archivedCorrection && !pending && OrgGrade.SelectedItem is GradeChoice { Id: not null };
        OrgTeacherAllocation.IsEnabled = !_busy && !_scopeUnavailable && (!_archivedCorrection || !_dirty);
        OrgContinue.IsEnabled = !_busy && (_receipt?.TermStatus == "DRAFT" || OrgReceiptClasses.SelectedItem is ReceiptChoice);
        OrgClose.IsEnabled = !_writing;
        OrgFooter.Text = $"{_classes.Count} 个行政班 · {_groups.Count} 个教学班";
    }

    private void Status(string value) { OrgStatus.Text = value; ToolTip.SetTip(OrgStatus, value); }
    private void ApprovalChanged(object? sender, RoutedEventArgs args) { if (_client is not null) Render(); }
    private void DifferenceFilterChanged(object? sender, SelectionChangedEventArgs args) { if (_client is not null) ShowDifferences(); }
    private void ReceiptClassSelected(object? sender, SelectionChangedEventArgs args) { if (_client is not null) Render(); }
    private void SectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_client is null || !ReferenceEquals(args.Source, OrgSections)) return;
        if (OrgSections.SelectedIndex == 1) RebuildGroupEditor();
        Render();
    }
    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();
    internal void CloseForScopeChange() { _allowClose = true; Close(); }
}
