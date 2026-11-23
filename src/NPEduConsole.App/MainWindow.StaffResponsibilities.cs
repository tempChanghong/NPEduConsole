using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private StaffResponsibilitiesWindow? _staffWindow;
    private CancellationTokenSource? _staffGradeLookup;

    private async void OpenClassResponsibilityClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _classroom is not { IsActive: true, TermId: { } term } room) return;
        await ShowStaffResponsibilitiesAsync(client, school, term, "classes", room.Id, null);
        if (_staffWindow is null && _client == client && _school == school && _classroom?.Id == room.Id) OpenClassResponsibilityButton.Focus();
    }

    private async void OpenGradeResponsibilityClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes || _staffWindow is not null || _staffGradeLookup is not null) return;
        bool fromSchool = sender == SchoolGradeStaffButton;
        var entry = fromSchool ? SchoolGradeStaffButton : OpenGradeResponsibilityButton;
        SchoolAffairsNotice.IsVisible = false;
        if (!fromSchool && _classroom is { IsActive: true, GradeId: { } grade, TermId: { } term })
        {
            await OpenAsync(term, grade);
            return;
        }
        using var lookup = _staffGradeLookup = new CancellationTokenSource();
        try
        {
            var context = await client.GetClassCreationContextAsync(school.Id, lookup.Token);
            if (!Current() || lookup.IsCancellationRequested) return;
            if (context.TermId is not { } currentTerm || context.Grades.Count == 0)
            {
                Notice(context.TermId is null ? "暂无启用学期。请先在学期管理中核对学期。" : "当前学期还没有年级。请先创建年级。");
                return;
            }
            if (context.Grades.Count == 1) { await OpenAsync(currentTerm, context.Grades[0].Id); return; }
            var menu = new MenuFlyout();
            foreach (var row in context.Grades)
            {
                var item = new MenuItem { Header = $"{row.Name}（{row.Code}）" };
                item.Click += async (_, _) => await OpenAsync(currentTerm, row.Id);
                menu.Items.Add(item);
            }
            menu.ShowAt(entry);
        }
        catch (OperationCanceledException) when (lookup.IsCancellationRequested) { }
        catch (ConsoleApiException error) when (Current() && !lookup.IsCancellationRequested)
        {
            if (!ReturnToLoginIfExpired(error)) Notice(error.Message);
        }
        catch (ConsoleApiException) { }
        finally { if (_staffGradeLookup == lookup) _staffGradeLookup = null; }

        void Notice(string message)
        {
            if (!fromSchool) { SetWorkspaceMessage(message, isError: true); return; }
            SchoolAffairsNotice.Text = message;
            SchoolAffairsNotice.IsVisible = true;
        }

        bool Current() => _client == client && _school == school && _page == ShellPage.Classes && entry.IsEffectivelyVisible;
        async Task OpenAsync(string termId, string gradeId)
        {
            if (!Current()) return;
            await ShowStaffResponsibilitiesAsync(client, school, termId, "grades", gradeId, null);
            if (Current() && _staffWindow is null) entry.Focus();
        }
    }

    private async Task ShowStaffResponsibilitiesAsync(ConsoleClient client, School school, string termId, string kind, string targetId, string? accountId,
        Window? owner = null, string? currentTermName = null)
    {
        if (_staffWindow is not null || _client != client || _school != school) return;
        var window = new StaffResponsibilitiesWindow(client, school, termId, kind, targetId, accountId, currentTermName: currentTermName)
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); }
        };
        _staffWindow = window;
        window.Closed += ClearWindow;
        try { await window.ShowDialog(owner ?? this); }
        finally
        {
            window.Closed -= ClearWindow;
            ClearWindow(null, EventArgs.Empty);
        }

        void ClearWindow(object? sender, EventArgs args)
        {
            if (_staffWindow == window) _staffWindow = null;
        }
    }

    private void CloseStaffResponsibilities()
    {
        _staffGradeLookup?.Cancel();
        _staffGradeLookup = null;
        var window = _staffWindow;
        _staffWindow = null;
        window?.CloseForScopeChange();
    }
}
