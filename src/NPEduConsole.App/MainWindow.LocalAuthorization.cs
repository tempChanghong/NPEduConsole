using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private Equipment? _localAuthorizationSnapshot;

    private void EditLocalAuthorizationClicked(object? sender, RoutedEventArgs args)
    {
        if (IsEditingEquipment || _equipment is not { } equipment ||
            !CanEditLocalAuthorization(equipment, CurrentMicrophoneChange))
        {
            RenderMicrophoneActions();
            return;
        }

        CancelRequest();
        _localAuthorizationSnapshot = equipment;
        bool revoking = equipment.AllowsLocalMicrophoneChange;
        LocalAuthorizationHeading.Text = revoking ? "撤销现场换麦授权" : "预授权现场换麦";
        LocalAuthorizationScope.Text = EquipmentScopeText;
        LocalAuthorizationDetails.Text = revoking
            ? "校方将关闭现场换麦权限。终端收到新策略后将按学校配置锁定设备。" +
                (equipment.MicrophoneId is not null ? "如果现场已换麦，终端会尝试切回学校指定的麦克风。" : "") +
                "终端离线时，撤销会在重新连接后同步。"
            : "每次现场更换都需验证班级管理口令，完成后设备会重新锁定。学校指定的麦克风保持原样，权限持续有效直至撤销。";
        SaveLocalAuthorizationButton.Content = revoking ? "确认撤销" : "确认授权";
        LocalAuthorizationEditor.IsVisible = true;
        RenderMicrophoneActions();
        SaveLocalAuthorizationButton.Focus();
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_localAuthorizationSnapshot, equipment)) LocalAuthorizationEditor.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private async void SaveLocalAuthorizationClicked(object? sender, RoutedEventArgs args)
    {
        if (_localAuthorizationSnapshot is not { } snapshot || !CanEditLocalAuthorization(snapshot, CurrentMicrophoneChange))
        {
            RenderMicrophoneActions();
            return;
        }

        await SaveEquipmentChangeAsync(snapshot, null, !snapshot.AllowsLocalMicrophoneChange);
    }

    private void CancelLocalAuthorizationClicked(object? sender, RoutedEventArgs args)
    {
        CloseLocalAuthorizationEditor();
        RenderMicrophoneActions();
        (EditLocalAuthorizationButton.IsVisible ? EditLocalAuthorizationButton : RefreshEquipmentButton).Focus();
    }

    private void CloseLocalAuthorizationEditor()
    {
        _localAuthorizationSnapshot = null;
        LocalAuthorizationEditor.IsVisible = false;
        SaveLocalAuthorizationButton.IsEnabled = false;
    }

    private bool CanEditLocalAuthorization(Equipment equipment, MicrophoneChange? change) =>
        CanPublishEquipment(equipment, change) &&
        (equipment.CanGrantLocalMicrophoneChange || equipment.CanRevokeLocalMicrophoneChange);

    private void RenderLocalAuthorization(Equipment? equipment, MicrophoneChange? change)
    {
        EditLocalAuthorizationButton.IsVisible = !IsEditingEquipment && equipment is not null &&
            CanEditLocalAuthorization(equipment, change);
        EditLocalAuthorizationButton.Content = equipment?.CanRevokeLocalMicrophoneChange == true
            ? "撤销现场授权" : "预授权现场换麦";
        SaveLocalAuthorizationButton.IsEnabled = _localAuthorizationSnapshot is { } snapshot &&
            CanEditLocalAuthorization(snapshot, change);
        LocalAuthorizationStatus.Text = equipment is null ? null : LocalAuthorizationStatusText(equipment);
    }

    private static string LocalAuthorizationStatusText(Equipment equipment)
    {
        if (!equipment.Locked)
            return equipment.AllowsLocalMicrophoneChange
                ? "学校策略未锁定设备，可直接在教室端选择麦克风。学校仍可撤销现场换麦权限。"
                : "学校策略未锁定设备，可直接在教室端选择麦克风。";
        if (!equipment.ServiceSupportsLocalMicrophoneChange)
            return "当前学校服务尚不支持现场换麦授权。";
        if (equipment.AllowsLocalMicrophoneChange)
        {
            const string enabled = "允许现场更换，每次需验证班级管理口令。";
            if (equipment.HasCurrentLocalMicrophoneOverride)
            {
                if (!equipment.MatchesLocalMicrophoneOverride || equipment.ErrorCode is not null)
                    return enabled + "上次现场更换的实际输入或锁定状态尚待确认。";
                return equipment.IsInventoryFresh
                    ? enabled + "上次现场更换已确认，设备已锁定。"
                    : enabled + "上次回执确认现场更换并重新锁定，回执已过期。";
            }
            if (equipment.ReportsLocalMicrophoneOverride)
                return equipment.IsInventoryFresh
                    ? enabled + "终端已上报现场换麦，整条策略仍待确认。"
                    : enabled + "上次回执上报了现场换麦，整条策略仍待确认。";
            if (!equipment.HasCurrentReceipt || equipment.ErrorCode is not null)
                return "学校已发布授权，等待终端确认。";
            if (!equipment.DeviceSupportsLocalMicrophoneChange)
                return "学校授权已发布，终端尚未上报支持现场换麦。";
            return equipment.IsInventoryFresh ? enabled : enabled + "最近一次设备回执已过期。";
        }
        if (equipment.ReceivedAt is null) return "未授权。收到设备回执后可核对是否支持现场换麦。";
        if (!equipment.DeviceSupportsLocalMicrophoneChange) return "未授权。终端尚未上报支持现场换麦，请更新终端并刷新。";
        if (!equipment.HasCurrentReceipt) return "学校未授权，等待终端确认当前策略。";
        if (!equipment.IsInventoryFresh) return "未授权。收到新的设备回执后可预授权。";
        return "未授权";
    }
}
