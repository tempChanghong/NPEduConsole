using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private ExamWorkbenchWindow? _examWindow;
    private CancellationTokenSource? _examOpen;

    private async void OpenScreenExamClicked(object? sender, RoutedEventArgs args)
    {
        if (_classroom is { } room && _detailScreenId is { } id && sender is Control anchor)
            await ShowScreenExamAsync(room, id, anchor);
    }

    private async Task ShowScreenExamAsync(Classroom room, string bindingId, Control anchor)
    {
        if (_client is not { } client || _school is not { } school || _examWindow is not null || _examOpen is not null) return;
        var page = _page;
        using var request = new CancellationTokenSource();
        _examOpen = request;
        try
        {
            var rooms = await client.GetClassroomsAsync(school.Id, request.Token);
            if (_client != client || _school != school || _page != page || _classroom?.Id != room.Id || request.IsCancellationRequested ||
                _page == ShellPage.Screen && _detailScreenId != bindingId) return;
            var current = rooms.SingleOrDefault(item => item.Id == room.Id && item.TermId == room.TermId);
            var screen = current?.Screens.SingleOrDefault(item => item.Id == bindingId);
            if (screen is null || current is null)
            {
                SetWorkspaceMessage("大屏或班级已变化。请刷新设备目录后重新定位。", isError: true);
                return;
            }
            var window = new ExamWorkbenchWindow(client, school, current, screen, _device?.Device.Id);
            _examWindow = window;
            try { await window.ShowDialog(this); }
            finally { if (_examWindow == window) _examWindow = null; }
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
            if (_client == client && _school == school && anchor.IsEffectivelyVisible)
                Dispatcher.UIThread.Post(() =>
                {
                    if (_client == client && _school == school && anchor.IsEffectivelyVisible) anchor.Focus();
                }, DispatcherPriority.Loaded);
        }
    }

    private void CloseScreenExam()
    {
        _examOpen?.Cancel();
        _examWindow?.Close();
    }
}
