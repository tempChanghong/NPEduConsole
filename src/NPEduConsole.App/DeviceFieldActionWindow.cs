using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

// The independent study and seating windows keep their existing entry point.
public sealed class DeviceFieldActionWindow : Window
{
    private readonly DeviceFieldActionView _field;
    private readonly Grid _pages = new();
    private TaskCompletionSource<bool>? _reportReturn;

    internal DeviceFieldActionWindow(ConsoleClient client, DeviceFieldScope scope, string school, string classroom, string screen, string device)
    {
        Title = "现场处置";
        Width = 960;
        Height = 650;
        MinWidth = 640;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _field = new DeviceFieldActionView(client, scope, school, classroom, screen, device)
        { CloseRequested = Close, OpenReportsPage = ShowReportsAsync };
        _pages.Children.Add(_field);
        Content = _pages;
        Opened += async (_, _) => { BatchWorkspaceWindow.FitSchoolWindow(this); await _field.StartAsync(); };
        Closed += (_, _) => { FinishReport(false); _field.End(); };
    }

    internal bool Revoked => _field.Revoked;

    private async Task<bool> ShowReportsAsync(DeviceRunReportsView reports)
    {
        var returned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reportReturn = returned;
        reports.CloseRequested = () => FinishReport(true);
        _field.IsVisible = false;
        _pages.Children.Add(reports);
        await reports.StartAsync();
        return await returned.Task;
    }

    private void FinishReport(bool returned)
    {
        if (_reportReturn is not { } result || _pages.Children.Count != 2 || _pages.Children[1] is not DeviceRunReportsView reports) return;
        _reportReturn = null;
        reports.End();
        _pages.Children.Remove(reports);
        if (returned) _field.IsVisible = true;
        result.SetResult(returned);
    }
}
