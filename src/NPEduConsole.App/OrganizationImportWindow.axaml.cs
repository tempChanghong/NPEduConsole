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
    private readonly CancellationTokenSource _lifetime = new();
    private OrganizationImportCatalog? _catalog;
    private OrganizationTerm? _term;
    private OrganizationDeclaration? _declaration;
    private OrganizationPreview? _preview;
    private OrganizationImportReceipt? _receipt;
    private OrganizationDifference[] _visibleReview = [];
    private bool _binding, _busy, _writing, _closed, _allowClose, _dirty, _reviewed, _discard;
    private int _gradeOrder;

    public OrganizationImportWindow() => InitializeComponent();

    internal OrganizationImportWindow(ConsoleClient client, School school, string? termId) : this()
    {
        _client = client;
        _school = school;
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
                    catch (ConsoleApiException error) when (error.Code == "ORG_IMPORT_TERM_CHANGED") { await LoadAsync(null, true); }
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
    private Operation? Pending => Operations.GetValueOrDefault(_school.Id);
    private bool CanEdit => _catalog is not null && _term is not null && !_busy && Pending is null;
    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal bool ActiveTermChanged { get; private set; }
    internal string? GoToDraftId { get; private set; }
    internal (string TermId, string WorkspaceId, bool IsGroup)? GoToTeaching { get; private set; }
    internal (string TermId, string GradeId)? GoToTeacherAllocation { get; private set; }

    private async Task LoadAsync(string? termId, bool clearTarget = false)
    {
        var catalog = await _client.GetOrganizationImportAsync(_school.Id, termId, _lifetime.Token);
        if (_closed) return;
        _catalog = catalog;
        _term = clearTarget ? null : catalog.Terms.FirstOrDefault(row => row.Id == catalog.TermId);
        _binding = true;
        try
        {
            OrgTerm.ItemsSource = catalog.Terms;
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
    }

    private async void TermSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_binding || _busy || Pending is not null || OrgTerm.SelectedItem is not OrganizationTerm term || _term?.Id == term.Id) return;
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
        _binding = false;
        TargetGradeEdited(null, null!);
    }

    private void TargetGradeEdited(object? sender, TextChangedEventArgs args)
    {
        if (_binding || _catalog is null) return;
        UpdateScope();
        RebuildGroupEditor();
        Edited();
    }

    private OrganizationGrade CurrentGrade() => new(OrgGradeCode.Text?.Trim() ?? "", OrgGradeName.Text?.Trim() ?? "", _gradeOrder);

    private OrganizationImportDraft Snapshot() => new(_term!.Id, _term.Status, CurrentGrade(),
        _subjects.Select(row => row.Snapshot()).ToArray(), _classes.Select(row => row.Snapshot()).ToArray(),
        _groups.Select(row => row.Snapshot()).ToArray(), _declaration);

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
        var target = current.Code.Length > 0 || current.Name.Length > 0 ? current : file.Grade;
        SetDocument(file with { Grade = target });
        OrgSections.SelectedIndex = 0;
        var mismatch = !string.Equals(target.Code.Trim(), file.Grade.Code.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(target.Name.Trim(), file.Grade.Name.Trim(), StringComparison.Ordinal);
        Status(mismatch ? $"所选目标：{target.Name}（{target.Code}）。文件声明：{file.Grade.Name}（{file.Grade.Code}）。请选择对应年级或换文件。" :
            "已载入文件。预览时核对其中声明的学校、学期和年级。");
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
        if (!CanEdit || _classes.Count + _groups.Count == 0) return;
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
        _visibleReview = review.Changes.Select(row => changedKeys.Contains(row.Key) ? row with { NeedsReview = true } : row).ToArray();
        if (prior is not null)
        {
            var missing = changedKeys.Where(key => !_visibleReview.Any(row => row.Key == key));
            _visibleReview = [.. _visibleReview, .. missing.Select(key =>
            {
                var previous = prior.Changes.FirstOrDefault(row => row.Key == key);
                return new OrganizationDifference(key, previous?.Path ?? "", "KEEP", previous?.Object ?? key,
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
        if (!CanEdit || !_reviewed || OrgApprove.IsChecked != true || _preview is not { CanApply: true } preview) return;
        var operation = new Operation(Snapshot(), preview, Guid.NewGuid());
        Operations[_school.Id] = operation;
        await RunAsync(async () =>
        {
            try
            {
                var result = await _client.SaveOrganizationImportAsync(_school.Id, operation.Draft, preview,
                    operation.RequestId, _lifetime.Token);
                if (_closed) return;
                Operations.Remove(_school.Id);
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
                Operations.Remove(_school.Id);
                _reviewed = false;
                throw;
            }
        }, writing: true);
    }

    private async void VerifyClicked(object? sender, RoutedEventArgs args)
    {
        if (Pending is { } operation) await RunAsync(() => VerifyReceiptAsync(operation));
    }

    private async Task VerifyReceiptAsync(Operation operation)
    {
        Status("正在按原请求只读核对服务器回执。");
        var receipt = await _client.GetOrganizationImportReceiptAsync(_school.Id, operation.Draft, operation.RequestId, _lifetime.Token);
        if (_closed) return;
        if (receipt is null) Status("服务器暂未返回本次回执。结果仍待核对，请稍后再查原请求。");
        else
        {
            Operations.Remove(_school.Id);
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
        OrgReceiptCaption.Text = $"服务器已确认 · {receipt.AdministrativeClasses.Length} 个行政班 · {receipt.CourseGroups.Length} 个教学班 · {receipt.Subjects.Length} 个学科";
        var classes = _classes.ToDictionary(row => row.Code.Trim().ToUpperInvariant(), row => row.Name);
        var groups = _groups.ToDictionary(row => row.Code.Trim().ToUpperInvariant(), row => row.Name);
        OrgReceiptClasses.ItemsSource = receipt.AdministrativeClasses
            .Select(row => new ReceiptChoice(row.Id, $"{classes.GetValueOrDefault(row.Code, row.Code)}（{row.Code}）", false))
            .Concat(receipt.CourseGroups.Select(row => new ReceiptChoice(row.Id, $"{groups.GetValueOrDefault(row.Code, row.Code)}（{row.Code}）· 走班", true))).ToArray();
        OrgReceiptClasses.SelectedIndex = OrgReceiptClasses.ItemCount > 0 ? 0 : -1;
        OrgContinue.Content = receipt.TermStatus == "DRAFT" ? "进入启用前检查" : "进入所选班级任课";
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
        if (_busy || _receipt is not { } receipt) return;
        GoToTeacherAllocation = (receipt.TermId, receipt.GradeId);
        _allowClose = true;
        Close();
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
            try { await LoadAsync(null, true); } catch (ConsoleApiException loadError) { SessionExpired?.Invoke(loadError); }
            if (!_closed) Status("目标学期状态已变化，本次零写入。表格已保留，请重新选定学期后预览。");
        }
        catch (ConsoleApiException error) when (!_closed)
        {
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
        OrgTerm.IsEnabled = !_busy && !pending && _catalog is not null;
        OrgGrade.IsEnabled = OrgGradeName.IsEnabled = OrgGradeCode.IsEnabled = edit;
        OrgOpenFile.IsEnabled = OrgExample.IsEnabled = OrgExcel.IsEnabled = edit;
        OrgClassTable.IsEnabled = OrgClassEditor.IsEnabled = OrgGroupTable.IsEnabled = OrgGroupEditor.IsEnabled = edit;
        OrgExistingSubject.IsEnabled = OrgSubjects.IsEnabled = OrgAddSubject.IsEnabled = edit;
        OrgPreview.IsVisible = !pending;
        OrgPreview.IsEnabled = edit && _classes.Count + _groups.Count > 0;
        OrgVerify.IsVisible = pending;
        OrgVerify.IsEnabled = !_busy;
        OrgApprove.IsEnabled = edit && _reviewed;
        OrgSave.IsVisible = OrgSections.SelectedIndex != 4;
        OrgSave.IsEnabled = edit && _reviewed && OrgApprove.IsChecked == true;
        OrgContinue.IsVisible = OrgSections.SelectedIndex == 4 && _receipt is not null;
        OrgTeacherAllocation.IsVisible = OrgContinue.IsVisible;
        OrgTeacherAllocation.IsEnabled = !_busy;
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
