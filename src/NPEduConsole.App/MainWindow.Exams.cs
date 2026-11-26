using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _examOpen;

    private async void OpenScreenExamClicked(object? sender, RoutedEventArgs args)
    {
        if (_classroom is { } room && _detailScreenId is { } id && sender is Control anchor)
            await ShowScreenExamAsync(room, id, anchor);
    }

    private async Task ShowScreenExamAsync(Classroom room, string bindingId, Control anchor)
    {
        if (_client is not { } client || _school is not { } school || _academicPages.Count != 0 || _examOpen is not null) return;
        var page = _page;
        var deviceId = _device is { } device && device.ScreenId == bindingId ? device.Device.Id : null;
        using var request = new CancellationTokenSource();
        _examOpen = request;
        try
        {
            var rooms = await client.GetClassroomsAsync(school.Id, request.Token);
            if (!SameSource() || request.IsCancellationRequested || _academicPages.Count != 0) return;
            var current = rooms.SingleOrDefault(item => item.Id == room.Id && item.TermId == room.TermId);
            var screen = current?.Screens.SingleOrDefault(item => item.Id == bindingId);
            if (screen is null || current is null)
            {
                SetWorkspaceMessage("大屏或班级已变化。请刷新设备目录后重新定位。", isError: true);
                return;
            }
            var termName = _overviewTerm is { } term && term.Id == current.TermId ? term.Name : null;
            var view = new ExamWorkbenchView(client, school, current, screen, deviceId, termName);
            view.CloseRequested = () => ReturnFromAcademicPage(view);
            var exit = PushAcademicPage(view, view.CanClose, view.End);
            _examOpen = null;
            await view.StartAsync();
            if (await exit && SameSource())
                Dispatcher.UIThread.Post(() =>
                {
                    if (!SameSource() || _academicPages.Count != 0 || !anchor.IsEffectivelyVisible || !anchor.IsEffectivelyEnabled) return;
                    anchor.BringIntoView();
                    anchor.Focus(NavigationMethod.Tab);
                }, DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_client == client && _school == school && !ReturnToLoginIfExpired(error))
                SetWorkspaceMessage(error.Message, isError: true);
        }
        finally
        {
            if (_examOpen == request) _examOpen = null;
        }

        bool SameSource() => _client == client && _school == school && _page == page && _classroom?.Id == room.Id &&
            _classroom.TermId == room.TermId && (page != ShellPage.Screen || _detailScreenId == bindingId);
    }

    private void CloseScreenExam() => _examOpen?.Cancel();
}
