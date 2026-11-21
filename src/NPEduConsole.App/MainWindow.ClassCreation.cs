using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, ClassCreationSubmission> _unconfirmedClassCreations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GradeCreationSubmission> _unconfirmedGradeCreations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClassBatchSubmission> _unconfirmedClassBatches = new(StringComparer.Ordinal);
    private ClassCreationWindow? _classCreationWindow;
    private CreatedAdministrativeClass? _createdClassToOpen;
    private CreatedAdministrativeClass? _openingCreatedClass;

    private async void OpenClassCreationClicked(object? sender, RoutedEventArgs args)
    {
        var school = _school;
        await OpenClassCreationAsync();
        if (_showSchoolOverview && _school == school)
        {
            await SchoolOverviewWorkspace.RefreshAsync();
        }
    }

    private async Task OpenClassCreationAsync(string? initialGradeTermId = null)
    {
        if (_classCreationWindow is not null || _openingCreatedClass is not null || _classRosterWindow is not null ||
            _client is not { } client || _school is not { } school) return;
        SetClassMaintenanceNotice(null);
        var window = new ClassCreationWindow(client, school, _unconfirmedClassCreations.GetValueOrDefault(school.Id),
            _unconfirmedGradeCreations.GetValueOrDefault(school.Id), _unconfirmedClassBatches.GetValueOrDefault(school.Id),
            initialGradeTermId);
        _classCreationWindow = window;
        window.PendingChanged += pending =>
        {
            if (_classCreationWindow != window || _client != client || _school != school) return;
            if (pending is null) _unconfirmedClassCreations.Remove(school.Id);
            else _unconfirmedClassCreations[school.Id] = pending;
        };
        window.PendingGradeChanged += pending =>
        {
            if (_classCreationWindow != window || _client != client || _school != school) return;
            if (pending is null) _unconfirmedGradeCreations.Remove(school.Id);
            else _unconfirmedGradeCreations[school.Id] = pending;
        };
        window.PendingBatchChanged += pending =>
        {
            if (_classCreationWindow != window || _client != client || _school != school) return;
            if (pending is null) _unconfirmedClassBatches.Remove(school.Id);
            else _unconfirmedClassBatches[school.Id] = pending;
        };
        window.SessionExpired += error =>
        {
            if (_classCreationWindow == window && _client == client) ReturnToLoginIfExpired(error);
        };
        window.Closed += (_, _) =>
        {
            if (_classCreationWindow == window && window.ConfirmedClass is null) _classCreationWindow = null;
        };
        CreatedAdministrativeClass? created;
        try { created = await window.ShowDialog<CreatedAdministrativeClass?>(this); }
        finally
        {
            if (_classCreationWindow == window) _classCreationWindow = null;
        }
        if (_client != client || _school != school || _classCreationWindow is not null) return;

        if (created is null)
        {
            bool gradeRefreshed = false;
            if (window.GradeDirectoryChanged || window.ConfirmedBatchCount is not null)
            {
                gradeRefreshed = await RefreshAfterGradeRenameAsync(client, school);
                if (_client != client || _school != school || _classCreationWindow is not null) return;
            }
            if (_unconfirmedGradeCreations.ContainsKey(school.Id)) ShowUnconfirmedGradeCreation();
            else if (_unconfirmedClassCreations.ContainsKey(school.Id)) ShowUnconfirmedClassCreation();
            else if (_unconfirmedClassBatches.ContainsKey(school.Id)) ShowUnconfirmedClassBatch();
            else if (window.ConfirmedBatchCount is { } batchCount)
                SetClassCreationNotice(gradeRefreshed
                    ? $"已创建 {batchCount} 个班。可从班级列表打开学生名单。"
                    : $"已确认创建 {batchCount} 个班。班级列表暂未刷新，可点击「刷新」重试。");
            else if (window.RenameConfirmed && window.RenamedGradeName is { } renamed)
                SetClassCreationNotice(gradeRefreshed
                    ? $"本次保存的名称「{renamed}」已确认。班级列表已刷新。"
                    : $"本次保存的名称「{renamed}」已确认。班级列表暂未刷新，可点击「刷新」重试。");
            else if (window.CloseNotice is { } notice) SetClassCreationNotice(notice);
            else if (window.GradeDirectoryChanged)
                SetClassCreationNotice(gradeRefreshed
                    ? "已按服务器当前名称刷新年级，所选班级已保留。"
                    : "年级名称可能已在服务器更新，但班级列表暂未刷新；所选班级已保留，可点击「刷新」重试。");
            else if (_createdClassToOpen is null) SetClassCreationNotice(null);
            OpenClassCreationButton.Focus();
            return;
        }

        _createdClassToOpen = created;
        SetInactiveClassesFilter(false);
        await OpenCreatedClassRosterAsync(client, school, created);
    }

    private async Task OpenCreatedClassRosterAsync(ConsoleClient client, School school, CreatedAdministrativeClass created)
    {
        if (_openingCreatedClass == created) return;
        _openingCreatedClass = created;
        try
        {
            SetClassCreationNotice($"已创建「{created.Name}」。正在刷新班级列表并打开学生名单…");
            bool refreshed = await LoadSchoolAsync(school, created.Id);
            if (_client != client || _school != school || _createdClassToOpen != created) return;
            var room = _classroom;
            if (!refreshed || room?.Id != created.Id || room.TermId != created.TermId)
            {
                SetClassCreationNotice(!refreshed
                    ? $"已创建「{created.Name}」，但班级列表未能刷新。点击「刷新」可按已确认的班级继续录入名单。"
                    : $"已创建「{created.Name}」，当前列表中未找到该班。请核对当前学期，再点击「刷新」。");
                return;
            }

            _createdClassToOpen = null;
            SetClassCreationNotice($"已创建「{created.Name}」，可录入首份学生名单。");
            bool saved = await ShowClassRosterAsync(client, school, room);
            if (_client == client && _school == school)
                SetClassCreationNotice(saved ? $"已创建「{created.Name}」，本次名单录入结果已确认。"
                    : $"已创建「{created.Name}」，本次未确认名单保存。可打开「学生名单」查看当前记录并继续录入。");
        }
        finally
        {
            if (_openingCreatedClass == created) _openingCreatedClass = null;
        }
    }

    private async Task<bool> RefreshAfterGradeRenameAsync(ConsoleClient client, School school)
    {
        if (_client != client || _school != school) return false;
        var selected = _classroom;
        var selectedDevice = _device;
        var previousRooms = (ClassPicker.ItemsSource as IEnumerable<Classroom>)?.ToArray() ?? [];
        bool refreshed = await LoadSchoolAsync(school, selected?.Id, selectedDevice);
        if (_client != client || _school != school) return false;
        if (refreshed || selected is null || ClassPicker.ItemsSource is not null) return refreshed;

        // A failed list refresh must not silently clear the class the manager was working in.
        _settingSelection = true;
        ClassPicker.ItemsSource = previousRooms;
        var restored = previousRooms.FirstOrDefault(room => room.Id == selected.Id);
        ClassPicker.SelectedItem = restored;
        _settingSelection = false;
        if (restored is not null) await SelectClassroomAsync(restored, selectedDevice);
        return false;
    }

    private void ChangeClassCreationSchool(School? school)
    {
        var window = _classCreationWindow;
        _classCreationWindow = null;
        _createdClassToOpen = _openingCreatedClass = null;
        window?.CloseForScopeChange();
        if (school is not null && _unconfirmedGradeCreations.ContainsKey(school.Id)) ShowUnconfirmedGradeCreation();
        else if (school is not null && _unconfirmedClassCreations.ContainsKey(school.Id)) ShowUnconfirmedClassCreation();
        else if (school is not null && _unconfirmedClassBatches.ContainsKey(school.Id)) ShowUnconfirmedClassBatch();
        else SetClassCreationNotice(null);
    }

    private void ResetClassCreation()
    {
        _unconfirmedClassCreations.Clear();
        _unconfirmedGradeCreations.Clear();
        _unconfirmedClassBatches.Clear();
        ChangeClassCreationSchool(null);
        OpenClassCreationButton.IsEnabled = false;
    }

    private void ShowUnconfirmedClassCreation() => SetClassCreationNotice("上一笔建班结果未确认。重新打开「新建行政班」可用原请求重试确认。");
    private void ShowUnconfirmedGradeCreation() => SetClassCreationNotice("上次创建年级的结果待核对。重新打开「新建行政班」，可查询原请求。");
    private void ShowUnconfirmedClassBatch() => SetClassCreationNotice("上次批量开班结果待核对。重新打开「新建行政班」，可继续核对原批次。");

    private void SetClassCreationNotice(string? notice)
    {
        ClassCreationNoticeText.Text = notice;
        ClassCreationNotice.IsVisible = notice is not null;
    }
}
