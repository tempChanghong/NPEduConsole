using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TermClassManagementView : UserControl
{
    internal string ReturnLabel { set => StructureClose.Content = value; }

    private readonly CancellationTokenSource _lifetime = new();
    private ConsoleClient? _client;
    private School? _school;
    private string _termId = "";
    private string? _initialClass, _initialGrade;
    private TermClassStructure? _structure;
    private bool _closed, _loading, _changingGrade, _showDetail;
    private CancellationTokenSource? _rosterRead;
    private Action? _closeChild;
    private PendingCreations? _creations;
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<(string SchoolId, string TermId), PendingCreations>> Pending = new();

    private sealed record GradeChoice(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class PendingCreations
    {
        public ClassCreationSubmission? Single;
        public GradeCreationSubmission? Grade;
        public ClassBatchSubmission? Batch;
    }

    public TermClassManagementView()
    {
        InitializeComponent();
        StructureDirectory.SelectionChanged += (_, _) => ShowSelection();
        StructureDirectory.OpenRequested += (_, _) => OpenSelection();
        SizeChanged += (_, _) => ApplyLayout();
    }

    internal TermClassManagementView(ConsoleClient client, School school, ManagedAcademicTerm term,
        string? initialClass = null, string? initialGrade = null) : this()
    {
        _client = client;
        _school = school;
        _termId = term.Id;
        _initialClass = initialClass;
        _initialGrade = initialGrade;
        _showDetail = initialClass is not null;
        var entries = Pending.GetOrCreateValue(client);
        var key = (school.Id, term.Id);
        if (!entries.TryGetValue(key, out _creations)) entries[key] = _creations = new();
        StructureTarget.Text = $"{school.Name} / {term.Name}";
        StructureActiveTerm.Text = "正在读取学期状态…";
        ShowSelection();
    }

    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal Action? CloseRequested { get; set; }
    internal Func<ClassMaintenanceView, Task<bool>> OpenMaintenance { get; init; } = null!;
    internal Func<ClassRosterView, Task<bool>> OpenRoster { get; init; } = null!;
    internal bool CanClose() => _closeChild is null;
    private Window HostWindow => (Window)TopLevel.GetTopLevel(this)!;
    private bool Narrow => Bounds.Width < 920;
    private string? SelectedGradeId => (StructureGrade.SelectedItem as GradeChoice)?.Id;
    private TermAdministrativeClass? CurrentClass => _structure?.Classes.FirstOrDefault(room => room.Id == StructureDirectory.Selected?.Id);

    internal async Task StartAsync()
    {
        await ReadAsync(_initialClass, _initialGrade);
        if (_showDetail && CurrentClass is not null) RestoreFocus(StructureRoster);
        else if (!_closed) StructureDirectory.RestoreFocus();
    }

    internal void End()
    {
        if (_closed) return;
        _closed = true;
        _closeChild?.Invoke();
        CancelRosterRead();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task ReadAsync(string? selectClass = null, string? selectGrade = null)
    {
        if (_client is not { } client || _school is not { } school || _closed || _loading) return;
        _loading = true;
        selectClass ??= StructureDirectory.Selected?.Id;
        selectGrade ??= SelectedGradeId;
        StructureReload.IsEnabled = false;
        StructureColumns.IsEnabled = false;
        CancelRosterRead();
        Notice("正在读取所选学期的年级和班级…");
        try
        {
            var structure = await client.GetTermClassStructureAsync(school.Id, _termId, _lifetime.Token);
            if (_closed) return;
            _structure = structure;
            StructureTarget.Text = $"{school.Name} / {structure.Term.Name} · {structure.Term.StatusLabel}";
            StructureActiveTerm.Text = $"全校当前启用学期　{structure.ActiveTerm?.Name ?? "暂无"}";
            if (selectClass is not null && structure.Classes.FirstOrDefault(room => room.Id == selectClass) is { } room && selectGrade is not null)
                selectGrade = room.GradeId;
            var grades = new List<GradeChoice> { new(null, "全部年级") };
            grades.AddRange(structure.Creation.Grades.Select(grade => new GradeChoice(grade.Id, $"{grade.Name}（{grade.Code}）")));
            _changingGrade = true;
            StructureGrade.ItemsSource = grades;
            StructureGrade.SelectedItem = grades.FirstOrDefault(item => item.Id == selectGrade) ?? grades[0];
            _changingGrade = false;
            SetRows(selectClass);
            Notice(structure.Creation.Grades.Count == 0 ? "所选学期暂无年级，可先新建年级再开班。" : null);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            _structure = null;
            SetRows(null);
            Notice(error.Message, true);
            Access(error);
        }
        finally
        {
            _loading = false;
            if (!_closed)
            {
                StructureReload.IsEnabled = true;
                StructureColumns.IsEnabled = true;
                ShowSelection();
            }
        }
    }

    private void Notice(string? message, bool error = false)
    {
        StructureNotice.Text = message;
        StructureNotice.IsVisible = message is not null;
        StructureNotice.Classes.Set("error", error);
    }

    private void GradeChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_changingGrade && !_closed) SetRows(null);
    }

    private void SetRows(string? selectClass)
    {
        var gradeId = SelectedGradeId;
        var rows = _structure?.Classes.Where(room => gradeId is null || room.GradeId == gradeId)
            .Select(room => new TermClassChoice(room.Id, room.Name, room.Code, room.GradeName, room.IsActive)).ToArray() ?? [];
        StructureDirectory.SetRows(rows, selectClass);
        ShowSelection();
    }

    private void ShowSelection()
    {
        if (_closed) return;
        var room = CurrentClass;
        StructureOpenClass.IsEnabled = StructureEditClass.IsEnabled = StructureRoster.IsEnabled = room is not null;
        StructureClassTeaching.IsEnabled = room is { IsActive: true, GradeId: not null };
        StructureCreateGrade.IsEnabled = _structure is not null;
        StructureCreateClass.IsEnabled = _structure is { } directory &&
            (directory.Creation.Grades.Count > 0 || _creations is { Single: not null } or { Batch: not null });
        StructureGradeEdit.IsEnabled = _structure is not null && SelectedGradeId is not null;
        StructureClassName.Text = room?.Name ?? "选择一个行政班";
        StructureClassMeta.Text = room is null ? null :
            $"{room.GradeName} · {room.Code} · {(room.IsActive ? "启用中" : "已停用")} · {(room.IsStudentSelectable ? "允许" : "禁止")}学生自选";
        StructureRoster.Content = room?.IsActive == false || _structure?.Term.Status == "ARCHIVED" ? "查看名单" : "学生名单";
        if (!_loading) _ = ReadRosterSummaryAsync(room);
        ApplyLayout();
    }

    private async Task ReadRosterSummaryAsync(TermAdministrativeClass? room)
    {
        CancelRosterRead();
        StructureRosterRows.IsVisible = false;
        StructureRosterRows.ItemsSource = null;
        StructureRosterSummary.Text = null;
        if (room is null || _client is not { } client || _structure is not { } current || Narrow && !_showDetail) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _rosterRead = request;
        StructureRosterSummary.Text = "正在读取名单…";
        try
        {
            var roster = await client.GetTermClassRosterAsync(current.Creation, room.Id, request.Token);
            if (_closed || _rosterRead != request) return;
            StructureRosterSummary.Text = roster.Students.Count == 0 ? "学生名单为空" : $"学生名单 · {roster.Students.Count} 人";
            StructureRosterRows.ItemsSource = roster.Students.Select(student => student.StudentNumber is { } number
                ? $"{student.Name}　学号 {number}" : student.Name).ToArray();
            StructureRosterRows.IsVisible = roster.Students.Count != 0;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (!_closed && _rosterRead == request)
            {
                StructureRosterSummary.Text = error.Message;
                Access(error);
            }
        }
        finally { if (_rosterRead == request) _rosterRead = null; }
    }

    private void ApplyLayout()
    {
        if (_closed) return;
        StructureColumns.ColumnDefinitions[1].Width = Narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        StructureColumns.ColumnSpacing = Narrow ? 0 : 12;
        Grid.SetColumn(StructureDetailPane, Narrow ? 0 : 1);
        StructureDirectoryPane.IsVisible = !Narrow || !_showDetail;
        StructureDetailPane.IsVisible = !Narrow || _showDetail;
        StructureBackToClasses.IsVisible = Narrow;
        StructureOpenClass.IsVisible = Narrow;
    }

    private void OpenSelection()
    {
        if (CurrentClass is null) return;
        _showDetail = true;
        ShowSelection();
        RestoreFocus(StructureRoster);
    }

    private void BackToClassesClicked(object? sender, RoutedEventArgs args)
    {
        _showDetail = false;
        CancelRosterRead();
        ApplyLayout();
        StructureDirectory.RestoreFocus();
    }

    private void RestoreFocus(Control control) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && control.IsEffectivelyVisible && control.IsEffectivelyEnabled) control.Focus();
    }, DispatcherPriority.Loaded);

    private void CancelRosterRead()
    {
        var previous = _rosterRead;
        _rosterRead = null;
        previous?.Cancel();
    }

    private void Access(ConsoleApiException error)
    {
        if (error.StatusCode is 401 or 403) SessionExpired?.Invoke(error);
        if (error.StatusCode == 403 && !_closed)
        {
            _structure = null;
            SetRows(null);
            Notice(error.Message, true);
        }
    }

    private void OpenClassClicked(object? sender, RoutedEventArgs args) => OpenSelection();
    private async void ReloadClicked(object? sender, RoutedEventArgs args) => await ReadAsync();
    private async void CreateGradeClicked(object? sender, RoutedEventArgs args) => await OpenCreationAsync(true);
    private async void CreateClassClicked(object? sender, RoutedEventArgs args) => await OpenCreationAsync(false);
    private void CloseClicked(object? sender, RoutedEventArgs args) => CloseRequested?.Invoke();

    private async Task OpenCreationAsync(bool grade)
    {
        if (_client is not { } client || _school is not { } school || _structure is not { } directory || _creations is not { } pending) return;
        var window = new ClassCreationWindow(client, school, pending.Single, pending.Grade, pending.Batch,
            grade ? directory.Term.Id : null, directory.Creation, SelectedGradeId);
        window.PendingChanged += change => pending.Single = change;
        window.PendingGradeChanged += change => pending.Grade = change;
        window.PendingBatchChanged += change => pending.Batch = change;
        window.SessionExpired += Access;
        await ShowChildAsync(window, window.CloseForScopeChange);
        if (_closed) return;
        await ReadAsync(window.ConfirmedClass?.Id);
        if (_closed) return;
        if (window.ConfirmedClass is { } created && _structure?.Classes.FirstOrDefault(room => room.Id == created.Id) is { } room)
        {
            _showDetail = true;
            await OpenRosterAsync(room);
        }
        else if (window.ConfirmedClass is not null)
            Notice("学校已确认建班。请刷新所选学期，按新班级编号核对后再打开名单。", true);
        else
            RestoreFocus(grade ? StructureCreateGrade : StructureCreateClass);
    }

    private async void GradeEditClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _structure?.Creation is not { } context || SelectedGradeId is not { } gradeId ||
            context.Grades.FirstOrDefault(grade => grade.Id == gradeId) is not { } grade) return;
        var window = new TermGradeEditWindow(client, context, grade) { SessionExpired = Access };
        await ShowChildAsync(window, window.CloseForScopeChange);
        if (!_closed) await ReadAsync(selectGrade: gradeId);
        RestoreFocus(StructureGradeEdit);
    }

    private async void EditClassClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _structure is not { } directory || CurrentClass is not { } room) return;
        CancelRosterRead();
        if (await OpenMaintenance(new ClassMaintenanceView(client, school, ClassroomOf(room), directory.Creation)) && !_closed)
            await ReadAsync(room.Id);
        RestoreFocus(StructureEditClass);
    }

    private async void RosterClicked(object? sender, RoutedEventArgs args)
    {
        if (CurrentClass is { } room) await OpenRosterAsync(room);
    }

    private async void TeachingClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school ||
            CurrentClass is not { IsActive: true, GradeId: { } gradeId } room) return;
        var window = new GradeTeachingPlanWindow(client, school, gradeId, null, null, room.TermId, room.Id) { SessionExpired = Access };
        await ShowChildAsync(window, window.CloseForScopeChange);
        if (_closed) return;
        StructureColumns.IsEnabled = StructureReload.IsEnabled = true;
        RestoreFocus(StructureClassTeaching);
    }

    private async Task OpenRosterAsync(TermAdministrativeClass room)
    {
        if (_closed || _client is not { } client || _school is not { } school || _structure is not { } directory) return;
        CancelRosterRead();
        if (await OpenRoster(new ClassRosterView(client, school, ClassroomOf(room), directory.Creation)) && !_closed)
            await ReadAsync(room.Id);
        RestoreFocus(StructureRoster);
    }

    private static Classroom ClassroomOf(TermAdministrativeClass room) => new(room.Id, room.Name, room.GradeName, [])
        { TermId = room.TermId, GradeId = room.GradeId, Code = room.Code, IsActive = room.IsActive };

    private async Task ShowChildAsync(Window window, Action close)
    {
        _closeChild = close;
        // Keep commands disabled until the caller has refreshed the directory.
        StructureColumns.IsEnabled = false;
        StructureReload.IsEnabled = false;
        CancelRosterRead();
        try { await window.ShowDialog(HostWindow); }
        finally { _closeChild = null; }
    }
}
