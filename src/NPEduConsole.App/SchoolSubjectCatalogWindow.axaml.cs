using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed record SubjectDirectoryRow(SchoolSubject Subject)
{
    public string AutomationId => $"SchoolSubject-{Subject.Id}";
    public string CategoryName => SchoolSubjectCatalogWindow.CategoryLabel(Subject.Category);
}

public sealed partial class SchoolSubjectCatalogWindow : Window
{
    private sealed record CategoryChoice(string Code, string Label) { public override string ToString() => Label; }
    private sealed record PendingCreation(Guid RequestId, SchoolSubjectFields Fields);

    private static readonly CategoryChoice[] Categories = new[] { "CORE", "ELECTIVE", "OTHER" }
        .Select(code => new CategoryChoice(code, CategoryLabel(code))).ToArray();
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, PendingCreation>> PendingBySession = new();

    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly Dictionary<string, PendingCreation> _pendingBySchool = null!;
    private readonly bool _returnToSetup;
    private readonly string _returnDestination = "开科";
    private SchoolSubjectCatalog? _catalog;
    private SchoolSubjectCatalog? _observedCatalog;
    private SchoolSubject? _baseline;
    private SchoolSubject? _serverValue;
    private SchoolSubject? _changedCreation;
    private SchoolSubjectFields? _uncertainEdit;
    private PendingCreation? _pending;
    private string? _selectedId;
    private (string Name, string Code, string Order, int Category) _initialFields;
    private bool _creating, _dirty, _busy, _writing, _retryCreate, _requiresRead, _subjectMissing, _closed, _allowClose, _rebinding;
    private Avalonia.Vector _restoreOffset;

    public SchoolSubjectCatalogWindow() => InitializeComponent();

    internal SchoolSubjectCatalogWindow(ConsoleClient client, School school, bool returnToSetup = false, string returnDestination = "开科") : this()
    {
        _client = client;
        _school = school;
        _returnToSetup = returnToSetup;
        _returnDestination = returnDestination;
        _pendingBySchool = PendingBySession.GetOrCreateValue(client);
        _pending = _pendingBySchool.GetValueOrDefault(school.Id);
        CatalogScope.Text = $"{school.Name} · 全校共用";
        SubjectCategory.ItemsSource = Categories;
        SubjectCreateScope.IsVisible = returnToSetup;
        if (returnToSetup && returnDestination != "开科")
            SubjectCreateScope.Text = $"学科保存到全校目录。返回{returnDestination}后仍需确认归档组织差异。";
        SizeChanged += (_, _) => ArrangePanes();
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            await RunAsync(LoadAsync);
        };
        Closing += (_, args) =>
        {
            args.Cancel = !_allowClose && (_writing || _dirty && _pending is null && _uncertainEdit is null);
            if (args.Cancel && !_writing) OfferDiscard();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
        Render();
    }

    internal SchoolSubject? CreatedSubject { get; private set; }
    internal Action<ConsoleApiException>? SessionExpired { get; init; }

    internal static string CategoryLabel(string code) => code switch { "CORE" => "基础科目", "ELECTIVE" => "选考科目", _ => "其他科目" };

    private bool HasEditor => _creating || _baseline is not null;
    private bool Narrow => ClientSize.Width < 930;
    private ScrollViewer? DirectoryScroll => SubjectDirectoryRows.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private async Task LoadAsync()
    {
        Status("正在读取本校学科目录…");
        var catalog = await _client.GetSchoolSubjectsAsync(_school.Id, _lifetime.Token);
        if (_closed) return;
        _catalog = catalog;
        BindDirectory();
        if (_pending is { } pending)
        {
            if (!_creating) BeginCreate(pending.Fields);
            await VerifyCreationAsync();
        }
        else if (_returnToSetup)
        {
            Status(null);
            BeginCreate();
        }
        else
        {
            Status(null);
            RestoreDirectory();
        }
    }

    private void ArrangePanes()
    {
        bool showEditor = HasEditor;
        bool showDirectory = !_returnToSetup && (!Narrow || !showEditor);
        bool split = showDirectory && showEditor;
        if (CatalogWorkspace.ColumnDefinitions.Count != (split ? 2 : 1))
            CatalogWorkspace.ColumnDefinitions = new ColumnDefinitions(split ? "1.14*,*" : "*");
        Grid.SetColumn(SubjectEditorPane, split ? 1 : 0);
        SubjectEditorPane.Margin = split ? new Avalonia.Thickness(12, 0, 0, 0) : default;
        SubjectDirectoryPane.IsVisible = showDirectory;
        SubjectEditorPane.IsVisible = showEditor;
        SubjectEditorTitle.IsVisible = split;
        CatalogTitle.Text = showEditor && !split ? SubjectEditorTitle.Text : "学科目录";
    }

    private void Render()
    {
        ArrangePanes();
        var ready = _catalog is not null && !_busy;
        bool awaiting = _pending is not null || _uncertainEdit is not null;
        SubjectDirectoryRows.IsEnabled = ready && !awaiting;
        SubjectSearch.IsEnabled = ready && !awaiting;
        SubjectSearch.IsVisible = _catalog?.Subjects.Count > 0;
        NewSubjectButton.IsEnabled = ready && !awaiting;
        EditSubjectButton.IsEnabled = ready && !awaiting && SubjectDirectoryRows.SelectedItem is not null && !HasEditor;
        EditSubjectButton.IsVisible = SubjectDirectoryRows.SelectedItem is not null && !HasEditor;
        NewSubjectButton.Classes.Set("accent", !HasEditor);
        SubjectName.IsEnabled = SubjectCode.IsEnabled = SubjectCategory.IsEnabled = SubjectOrder.IsEnabled = ready && !awaiting;
        CloseSubjectCatalogButton.IsEnabled = !_writing;
        CloseSubjectCatalogButton.Content = _returnToSetup ? $"返回{_returnDestination}" : HasEditor ? "返回目录" : "关闭";
        ReloadSubjectsButton.IsVisible = (!_returnToSetup && !HasEditor) || awaiting || _requiresRead || _catalog is null;
        ReloadSubjectsButton.IsEnabled = !_busy;
        ReloadSubjectsButton.Content = awaiting || _requiresRead ? "核对服务器" : "刷新目录";
        SaveSubjectButton.IsVisible = HasEditor;
        SaveSubjectButton.Content = _retryCreate ? "按原请求重试" : _returnToSetup ? $"创建并返回{_returnDestination}" : _creating ? "创建学科" : "保存学科";
        SaveSubjectButton.IsEnabled = ready && (_retryCreate || !awaiting && !_requiresRead && !_subjectMissing && _serverValue is null &&
            !SubjectCodeError.IsVisible && (_creating || _dirty));
        SaveSubjectButton.Classes.Set("accent", !awaiting || _retryCreate);
        ReloadSubjectsButton.Classes.Set("accent", _requiresRead && _serverValue is null || awaiting && !_retryCreate && _changedCreation is null);
        SubjectConflict.IsVisible = _serverValue is not null || _changedCreation is not null;
        AdoptSubjectVersionButton.Content = _changedCreation is null ? "采用当前版本，保留草稿" : "使用这门学科";
        AdoptSubjectVersionButton.IsEnabled = ready;
    }

    private void BindDirectory()
    {
        string filter = (SubjectSearch.Text ?? "").Trim();
        IEnumerable<SchoolSubject> subjects = _catalog?.Subjects ?? [];
        var rows = subjects.Where(subject => subject.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            subject.Code.Contains(filter, StringComparison.OrdinalIgnoreCase)).Select(subject => new SubjectDirectoryRow(subject)).ToArray();
        _rebinding = true;
        SubjectDirectoryRows.ItemsSource = rows;
        SubjectDirectoryRows.SelectedItem = rows.SingleOrDefault(row => row.Subject.Id == _selectedId);
        _rebinding = false;
        SubjectDirectoryEmpty.Text = _catalog is null ? "" : filter.Length == 0 ? "本校还没有学科。" : "没有符合条件的学科。";
        SubjectDirectoryEmpty.IsVisible = rows.Length == 0;
        SubjectDirectoryRows.IsVisible = SubjectDirectoryHeaders.IsVisible = rows.Length > 0;
        Render();
    }

    private void FilterChanged(object? sender, TextChangedEventArgs args)
    {
        if (SubjectDirectoryRows is not null) BindDirectory();
    }

    private void SubjectSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (_rebinding || _busy || _pending is not null || SubjectDirectoryRows.SelectedItem is not SubjectDirectoryRow row) return;
        if (_baseline?.Id == row.Subject.Id) return;
        if (_dirty)
        {
            _rebinding = true;
            SubjectDirectoryRows.SelectedItem = args.RemovedItems.Cast<object>().FirstOrDefault();
            _rebinding = false;
            OfferDiscard();
            return;
        }
        BeginEdit(row.Subject);
    }

    private void EditClicked(object? sender, RoutedEventArgs args)
    {
        if (EditSubjectButton.IsEnabled && SubjectDirectoryRows.SelectedItem is SubjectDirectoryRow row) BeginEdit(row.Subject);
    }

    private void DirectoryKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || !EditSubjectButton.IsEnabled || SubjectDirectoryRows.SelectedItem is not SubjectDirectoryRow row) return;
        args.Handled = true;
        BeginEdit(row.Subject);
    }

    private void BeginEdit(SchoolSubject subject)
    {
        _restoreOffset = DirectoryScroll?.Offset ?? default;
        _creating = false;
        _baseline = subject;
        _selectedId = subject.Id;
        SetEditor(subject.Fields, $"编辑 {subject.Name}");
    }

    private void NewClicked(object? sender, RoutedEventArgs args)
    {
        if (_dirty) { OfferDiscard(); return; }
        _restoreOffset = DirectoryScroll?.Offset ?? default;
        Status(null);
        BeginCreate();
    }

    private void BeginCreate(SchoolSubjectFields? fields = null)
    {
        _creating = true;
        _baseline = null;
        SetEditor(fields ?? new("", "", "OTHER", 0), "新建学科");
    }

    private void SetEditor(SchoolSubjectFields fields, string title)
    {
        _serverValue = null;
        _observedCatalog = null;
        _requiresRead = _subjectMissing = false;
        _rebinding = true;
        SubjectEditorTitle.Text = title;
        SubjectName.Text = fields.Name;
        SubjectCode.Text = fields.Code;
        SubjectCategory.SelectedIndex = Array.FindIndex(Categories, category => category.Code == fields.Category);
        SubjectOrder.Text = fields.SortOrder.ToString(CultureInfo.InvariantCulture);
        _initialFields = CurrentFields();
        _dirty = false;
        _rebinding = false;
        DiscardSubjectButton.IsVisible = false;
        ClearFieldErrors();
        Render();
        Focus(SubjectName);
    }

    private (string Name, string Code, string Order, int Category) CurrentFields() =>
        (SubjectName.Text ?? "", SubjectCode.Text ?? "", SubjectOrder.Text ?? "", SubjectCategory.SelectedIndex);

    private void FieldChanged(object? sender, TextChangedEventArgs args)
    {
        if (sender == SubjectName) SubjectNameError.IsVisible = false;
        if (sender == SubjectCode) SubjectCodeError.IsVisible = false;
        if (sender == SubjectOrder) SubjectOrderError.IsVisible = false;
        MarkDraft();
    }

    private void CategoryChanged(object? sender, SelectionChangedEventArgs args) => MarkDraft();

    private void MarkDraft()
    {
        if (_rebinding || !HasEditor) return;
        _dirty = CurrentFields() != _initialFields;
        Render();
    }

    private SchoolSubjectFields? ValidateDraft()
    {
        ClearFieldErrors();
        var (name, code, order, category) = CurrentFields();
        name = name.Trim();
        code = code.Trim().ToUpperInvariant();
        if (name.Length is < 1 or > 191)
        {
            FieldError(SubjectNameError, SubjectName, "名称需为 1 至 191 个字符。");
            return null;
        }
        if (!Regex.IsMatch(code, @"\A[A-Z0-9][A-Z0-9._-]{1,63}\z"))
        {
            FieldError(SubjectCodeError, SubjectCode, "代码为 2 至 64 位，以字母或数字开头，可用点、横线和下划线。");
            return null;
        }
        if (!int.TryParse(order.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int sort) || sort is < -10000 or > 10000)
        {
            FieldError(SubjectOrderError, SubjectOrder, "请输入 -10000 至 10000 之间的整数。");
            return null;
        }
        if (category < 0) { Focus(SubjectCategory); return null; }
        return new(code, name, Categories[category].Code, sort);
    }

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (!SaveSubjectButton.IsEnabled || _catalog is null || (_pending?.Fields ?? ValidateDraft()) is not { } fields) return;
        await RunAsync(async () =>
        {
            _writing = true;
            Render();
            Status("正在保存学科…");
            if (_creating) await CreateAsync(fields);
            else await UpdateAsync(fields);
        });
    }

    private async Task CreateAsync(SchoolSubjectFields fields)
    {
        _pending ??= new(Guid.NewGuid(), fields);
        _pendingBySchool[_school.Id] = _pending;
        _retryCreate = false;
        SchoolSubject subject;
        try { subject = await _client.CreateSchoolSubjectAsync(_catalog!, fields, _pending.RequestId, _lifetime.Token); }
        catch (ConsoleApiException error) when (Uncertain(error))
        {
            await VerifyCreationAsync();
            return;
        }
        catch (ConsoleApiException error)
        {
            ForgetCreation();
            if (error.Code == "SUBJECT_CODE_EXISTS") { Duplicate(error); return; }
            throw;
        }
        if (!_closed) await ConfirmCreationAsync(subject);
    }

    private async Task VerifyCreationAsync()
    {
        if (_pending is not { } pending) return;
        _retryCreate = false;
        _changedCreation = null;
        Status("正在按本次请求编号核对创建回执…");
        var subject = await _client.GetSchoolSubjectCreationAsync(_catalog!, pending.RequestId, _lifetime.Token);
        if (_closed) return;
        if (subject is null)
        {
            var current = await _client.GetSchoolSubjectsAsync(_school.Id, _lifetime.Token);
            if (_closed) return;
            _catalog = current;
            BindDirectory();
            _retryCreate = true;
            var occupying = current.Subjects.FirstOrDefault(item => item.Code == pending.Fields.Code);
            string observation = occupying is null ? "目前未读到同码学科。" : $"本校已有同码学科「{occupying.Name}」，归属仍待核对。";
            Status($"暂未收到本次创建回执。{observation}可以再次核对，或按原请求重试。");
            return;
        }
        if (subject.Fields != pending.Fields)
        {
            _changedCreation = subject;
            ShowServerValues(subject);
            Status("已核对到本次创建回执。学科当前值已变化，请核对后再使用。", error: true);
            Render();
            Focus(AdoptSubjectVersionButton);
            return;
        }
        await ConfirmCreationAsync(subject);
    }

    private async Task ConfirmCreationAsync(SchoolSubject subject)
    {
        ForgetCreation();
        if (_returnToSetup)
        {
            CreatedSubject = subject;
            _allowClose = true;
            Close();
            return;
        }
        await ShowSavedAsync(subject, $"已确认创建 {subject.Name}（{subject.Code}）。");
    }

    private void ForgetCreation()
    {
        _pendingBySchool.Remove(_school.Id);
        _pending = null;
        _changedCreation = null;
        _retryCreate = false;
    }

    private async Task UpdateAsync(SchoolSubjectFields fields)
    {
        var baseline = _baseline!;
        SchoolSubject saved;
        try { saved = await _client.UpdateSchoolSubjectAsync(_catalog!, baseline.Id, fields, _lifetime.Token); }
        catch (ConsoleApiException error) when (error.Code == "SUBJECT_CODE_EXISTS") { Duplicate(error); return; }
        catch (ConsoleApiException error) when (Uncertain(error) || error.Code is "ORGANIZATION_VERSION_CONFLICT" or "SUBJECT_NOT_FOUND")
        {
            if (Uncertain(error)) _uncertainEdit = fields;
            _requiresRead = true;
            await ObserveEditAsync();
            return;
        }
        if (!_closed) await ShowSavedAsync(saved, $"已保存 {saved.Name}（{saved.Code}）。");
    }

    private async Task ObserveEditAsync()
    {
        Status("正在读取这门学科在学校目录中的当前值…");
        var catalog = await _client.GetSchoolSubjectsAsync(_school.Id, _lifetime.Token);
        if (_closed) return;
        var current = catalog.Subjects.SingleOrDefault(subject => subject.Id == _baseline?.Id);
        bool sameAsSubmitted = _uncertainEdit is { } submitted && current?.Fields == submitted;
        _uncertainEdit = null;
        if (current is null)
        {
            _catalog = catalog;
            _serverValue = null;
            _observedCatalog = null;
            _requiresRead = false;
            _subjectMissing = true;
            BindDirectory();
            Status("这门学科已不在本校目录。草稿仍在，请返回目录重新选择。", error: true);
            return;
        }
        if (sameAsSubmitted)
        {
            _catalog = catalog;
            FinishEdit(current.Id);
            Status("已核对，学校当前学科与本次提交一致。");
            return;
        }
        _observedCatalog = catalog;
        _serverValue = current;
        ShowServerValues(current);
        Status("草稿已保留。请比较草稿与学校当前值，再决定是否采用当前版本继续编辑。", error: true);
        Render();
        Focus(AdoptSubjectVersionButton);
    }

    private async Task ShowSavedAsync(SchoolSubject subject, string message)
    {
        Status(message);
        try
        {
            var catalog = await _client.GetSchoolSubjectsAsync(_school.Id, _lifetime.Token);
            if (_closed) return;
            _catalog = catalog;
            FinishEdit(subject.Id);
        }
        catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403))
        {
            if (!_closed)
            {
                _catalog = null;
                FinishEdit(subject.Id);
                Status($"{message}目录刷新失败，可再次刷新。");
            }
        }
    }

    private async void AdoptClicked(object? sender, RoutedEventArgs args)
    {
        if (_changedCreation is { } created)
        {
            await RunAsync(() => ConfirmCreationAsync(created));
            return;
        }
        if (_observedCatalog is not { } catalog || _serverValue is not { } current) return;
        _catalog = catalog;
        _baseline = current;
        _observedCatalog = null;
        _serverValue = null;
        _requiresRead = false;
        Status("已采用学校当前版本。草稿仍可修改，确认后再保存。");
        Render();
        Focus(SubjectName);
    }

    private async void ReloadClicked(object? sender, RoutedEventArgs args) => await RunAsync(
        _catalog is null ? LoadAsync : _pending is not null ? VerifyCreationAsync : _requiresRead ? ObserveEditAsync : LoadAsync);

    private void CloseClicked(object? sender, RoutedEventArgs args)
    {
        if (_returnToSetup || !HasEditor || _pending is not null || _uncertainEdit is not null) { Close(); return; }
        if (_dirty) { OfferDiscard(); return; }
        FinishEdit(_selectedId);
        Status(null);
    }

    private void DiscardClicked(object? sender, RoutedEventArgs args)
    {
        _dirty = false;
        if (_returnToSetup) { Close(); return; }
        FinishEdit(_selectedId);
        Status("已放弃草稿。");
    }

    private void FinishEdit(string? selectedId)
    {
        _catalog = _observedCatalog ?? _catalog;
        _creating = _dirty = _requiresRead = _subjectMissing = false;
        _baseline = _serverValue = null;
        _observedCatalog = null;
        _selectedId = selectedId;
        DiscardSubjectButton.IsVisible = false;
        Render();
        RestoreDirectory();
    }

    private void RestoreDirectory()
    {
        BindDirectory();
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || !SubjectDirectoryPane.IsEffectivelyVisible) return;
            if (DirectoryScroll is { } scroll) scroll.Offset = _restoreOffset;
            if (SubjectDirectoryRows.SelectedItem is { } selected)
            {
                SubjectDirectoryRows.ScrollIntoView(selected);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_closed && SubjectDirectoryRows.SelectedItem == selected &&
                        SubjectDirectoryRows.ContainerFromItem(selected) is ListBoxItem container) container.Focus();
                }, DispatcherPriority.Loaded);
            }
            else if (SubjectSearch.IsEffectivelyVisible) SubjectSearch.Focus();
            else NewSubjectButton.Focus();
        }, DispatcherPriority.Loaded);
    }

    private async Task RunAsync(Func<Task> run)
    {
        if (_busy || _closed) return;
        _busy = true;
        Render();
        try { await run(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error) when (!_closed)
        {
            if (error.StatusCode is 401 or 403)
            {
                ForgetCreation();
                _uncertainEdit = null;
                _catalog = null;
                FinishEdit(null);
                if (error.StatusCode == 401) SessionExpired?.Invoke(error);
            }
            else if (error.Code == "SUBJECT_CREATION_TARGET_CHANGED") ForgetCreation();
            if (!_closed) Status(_pending is null && _uncertainEdit is null ? error.Message : $"保存结果仍需核对。{error.Message}", error: true);
        }
        catch (ConsoleApiException) when (_closed) { }
        finally
        {
            _busy = _writing = false;
            if (!_closed) Render();
        }
    }

    internal void CloseForScopeChange() { _allowClose = true; Close(); }

    private void OfferDiscard()
    {
        Status("你有未保存的草稿。可以继续修改，或放弃草稿后返回。");
        DiscardSubjectButton.IsVisible = true;
    }

    private void Duplicate(ConsoleApiException error)
    {
        Status(null);
        FieldError(SubjectCodeError, SubjectCode, error.Message);
    }

    private void Status(string? message, bool error = false)
    {
        CatalogFeedback.Text = message;
        CatalogFeedback.IsVisible = message is not null;
        CatalogFeedback.Classes.Set("muted", !error);
        CatalogFeedback.Classes.Set("error", error);
    }

    private void ShowServerValues(SchoolSubject current) => SubjectServerValues.Text =
        $"{current.Name} · {current.Code} · {CategoryLabel(current.Category)} · 顺序 {current.SortOrder}";

    private void ClearFieldErrors() => SubjectNameError.IsVisible = SubjectCodeError.IsVisible = SubjectOrderError.IsVisible = false;

    private void FieldError(TextBlock label, Control input, string message)
    {
        label.Text = message;
        label.IsVisible = true;
        Focus(input);
    }

    private void Focus(Control control) => Dispatcher.UIThread.Post(() =>
    {
        if (_closed || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled) return;
        control.BringIntoView();
        control.Focus(NavigationMethod.Unspecified);
    }, DispatcherPriority.Loaded);

    private static bool Uncertain(ConsoleApiException error) => error.StatusCode is 0 or 408 or >= 500;
}
