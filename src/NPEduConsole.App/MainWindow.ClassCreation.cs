using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, ClassCreationSubmission> _unconfirmedClassCreations = new(StringComparer.Ordinal);
    private ClassCreationWindow? _classCreationWindow;
    private CreatedAdministrativeClass? _createdClassToOpen;
    private CreatedAdministrativeClass? _openingCreatedClass;

    private async void OpenClassCreationClicked(object? sender, RoutedEventArgs args)
    {
        if (_classCreationWindow is not null || _openingCreatedClass is not null || _classRosterWindow is not null ||
            _client is not { } client || _school is not { } school) return;
        var window = new ClassCreationWindow(client, school, _unconfirmedClassCreations.GetValueOrDefault(school.Id));
        _classCreationWindow = window;
        window.PendingChanged += pending =>
        {
            if (_classCreationWindow != window || _client != client || _school != school) return;
            if (pending is null) _unconfirmedClassCreations.Remove(school.Id);
            else _unconfirmedClassCreations[school.Id] = pending;
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
            if (_unconfirmedClassCreations.ContainsKey(school.Id)) ShowUnconfirmedClassCreation();
            else if (window.CloseNotice is { } notice) SetClassCreationNotice(notice);
            else if (_createdClassToOpen is null) SetClassCreationNotice(null);
            OpenClassCreationButton.Focus();
            return;
        }

        _createdClassToOpen = created;
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

    private void ChangeClassCreationSchool(School? school)
    {
        var window = _classCreationWindow;
        _classCreationWindow = null;
        _createdClassToOpen = _openingCreatedClass = null;
        window?.CloseForScopeChange();
        if (school is not null && _unconfirmedClassCreations.ContainsKey(school.Id)) ShowUnconfirmedClassCreation();
        else SetClassCreationNotice(null);
    }

    private void ResetClassCreation()
    {
        _unconfirmedClassCreations.Clear();
        ChangeClassCreationSchool(null);
        OpenClassCreationButton.IsEnabled = false;
    }

    private void ShowUnconfirmedClassCreation() => SetClassCreationNotice("上一笔建班结果未确认。重新打开「新建行政班」可用原请求重试确认。");

    private void SetClassCreationNotice(string? notice)
    {
        ClassCreationNoticeText.Text = notice;
        ClassCreationNotice.IsVisible = notice is not null;
    }
}
