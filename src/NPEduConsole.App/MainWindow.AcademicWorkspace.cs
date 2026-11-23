using Avalonia.Controls;
using Avalonia.Input;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private sealed record AcademicPage(Control Content, Func<bool> CanClose, Action End)
    {
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Stack<AcademicPage> _academicPages = new();

    private bool ShowsAcademicPage(Control view) => _academicPages.TryPeek(out var page) && page.Content == view;

    private Task<bool> PushAcademicPage(Control view, Func<bool> canClose, Action end)
    {
        if (_academicPages.TryPeek(out var prior)) prior.Content.IsVisible = false;
        var page = new AcademicPage(view, canClose, end);
        _academicPages.Push(page);
        AcademicWorkspace.Children.Add(view);
        UpdateShellPage();
        return page.Completion.Task;
    }

    private void ReturnFromAcademicPage(Control view)
    {
        if (!_academicPages.TryPeek(out var page) || page.Content != view || !page.CanClose()) return;
        _academicPages.Pop();
        RemoveAcademicPage(page, returned: true);
        if (_academicPages.TryPeek(out var prior)) prior.Content.IsVisible = true;
        UpdateShellPage();
    }

    private bool CanLeaveAcademicWorkspace() => !_academicPages.TryPeek(out var page) || page.CanClose();

    private bool LeaveAcademicWorkspace()
    {
        if (!CanLeaveAcademicWorkspace()) return false;
        if (_academicPages.Count > 0) CloseTermPreparation();
        return true;
    }

    private void CloseAcademicWorkspace()
    {
        if (_academicPages.Count == 0) return;
        while (_academicPages.TryPop(out var page)) RemoveAcademicPage(page, returned: false);
        UpdateShellPage();
    }

    private void RemoveAcademicPage(AcademicPage page, bool returned)
    {
        page.Content.IsVisible = false;
        page.End();
        AcademicWorkspace.Children.Remove(page.Content);
        page.Completion.TrySetResult(returned);
    }

    private async void AcademicScopeUnavailable(ConsoleApiException error, ConsoleClient client, School school)
    {
        if (_client != client || _school != school || ReturnToLoginIfExpired(error) || error.StatusCode != 403) return;
        TeachingView.ForgetPausedDrafts(client, school);
        CloseTermPreparation();
        _settingSelection = true;
        SchoolPicker.ItemsSource = SchoolPicker.Items.OfType<School>().Where(row => row.Id != school.Id).ToArray();
        SchoolPicker.SelectedItem = null;
        _settingSelection = false;
        _page = ShellPage.Overview;
        SchoolSelectionMessage.Text = "已无法管理原学校。请选择仍有权限的学校。";
        await LoadSchoolAsync(null);
        if (_client == client && _school is null) SchoolPicker.Focus(NavigationMethod.Tab);
    }
}
