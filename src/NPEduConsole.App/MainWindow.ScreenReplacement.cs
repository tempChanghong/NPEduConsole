using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using NPEduConsole.Core;
using CoreScreen = NPEduConsole.Core.Screen;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private Button? _replacementOpener;

    private string ScreenDisplayName(CoreScreen screen) => _classroom?.Screens.Count(candidate => candidate.Name == screen.Name) > 1
        ? $"{screen.Name}（编号 {screen.Id}）" : screen.Name;

    private Button BuildScreenReplacementButton(CoreScreen screen)
    {
        var opener = new Button { Content = "换机审核", Classes = { "action" }, Tag = screen };
        AutomationProperties.SetName(opener, $"换机审核：{ScreenDisplayName(screen)}");
        AutomationProperties.SetAutomationId(opener, "OpenScreenReplacement-" + screen.Id);
        opener.Click += async (_, _) =>
        {
            if (_client is not { } client || _school is not { } school || _classroom is not { } room ||
                _showClassStudy || _showSchoolAudit || !room.Screens.Any(candidate => ReferenceEquals(candidate, screen))) return;
            if (ReferenceEquals(_replacementOpener, opener))
            {
                ScreenReplacementWorkspace.BringIntoView();
                return;
            }
            CloseClassAdmission();
            CloseSoftwareMaintenance();
            _replacementOpener = opener;
            var load = ScreenReplacementWorkspace.OpenAsync(client, school.Id, screen.Id, $"{school.Name} / {ClassroomName(room)} / {ScreenDisplayName(screen)}");
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_replacementOpener, opener)) ScreenReplacementWorkspace.BringIntoView();
            }, DispatcherPriority.Loaded);
            await load;
        };
        return opener;
    }

    private void CloseScreenReplacement(bool restoreFocus = false)
    {
        var opener = _replacementOpener;
        if (opener is null) return;
        _replacementOpener = null;
        ScreenReplacementWorkspace.Reset();
        if (restoreFocus) opener.Focus();
    }

    private void RetireReplacedDevice(ScreenReplacementDetail handover)
    {
        if (_replacementOpener?.Tag is not CoreScreen screen) return;
        bool IsRetired(DeviceSelection selection) => selection.ScreenId == screen.Id &&
            Guid.TryParse(selection.Device.Id, out var id) && id == handover.Request.ExpectedDeviceId;

        foreach (var (button, selection) in _deviceButtons)
        {
            if (!IsRetired(selection)) continue;
            button.IsEnabled = false;
            if (button.Content is StackPanel row && row.Children[1] is TextBlock status)
                status.Text = "旧机已停用 · 刷新班级后查看新机";
            AutomationProperties.SetName(button, $"{selection.Device.Name} 已停用，请刷新班级后查看新机");
        }

        if (_device is not { } current || !IsRetired(current)) return;
        CancelRequest();
        _device = null;
        WorkspaceProgress.IsVisible = EquipmentCard.IsVisible = EquipmentDetails.IsVisible = false;
        ResetEquipmentView();
    }
}
