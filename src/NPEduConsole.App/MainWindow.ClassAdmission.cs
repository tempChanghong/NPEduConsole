using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;
using CoreScreen = NPEduConsole.Core.Screen;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private sealed record AdmissionTarget(School School, Classroom Classroom, CoreScreen Screen, Button Opener);

    private AdmissionTarget? _admissionTarget;
    private ClassAdmission? _admission;
    private CancellationTokenSource? _admissionRequest;
    private bool _admissionUnconfirmed;

    private async void OpenAdmissionClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: CoreScreen screen } opener || _school is not { } school ||
            _classroom is not { } classroom || !classroom.Screens.Any(item => ReferenceEquals(item, screen)) ||
            screen.Devices.Count != 0 || ReferenceEquals(_admissionTarget?.Screen, screen)) return;

        CloseClassAdmission();
        CloseSoftwareMaintenance();
        var target = new AdmissionTarget(school, classroom, screen, opener);
        _admissionTarget = target;
        string screenLabel = classroom.Screens.Count(item => item.Name == screen.Name) > 1
            ? $"{screen.Name}（编号 {screen.Id}）" : screen.Name;
        AdmissionScope.Text = $"{school.Name} / {ClassroomName(classroom)} / {screenLabel}";
        ClassAdmissionCard.IsVisible = true;
        await ReadClassAdmissionAsync();
        if (_admissionTarget != target) return;
        (AdmissionSecretFields.IsVisible ? AdmissionSecret : (Control)RefreshAdmissionButton).Focus();
        ShowAdmissionCard(target);
    }

    private void CloseAdmissionClicked(object? sender, RoutedEventArgs args)
    {
        var target = _admissionTarget;
        CloseClassAdmission();
        target?.Opener.Focus();
    }

    private void CloseClassAdmission()
    {
        var request = _admissionRequest;
        _admissionRequest = null;
        request?.Cancel();
        _admissionTarget = null;
        _admission = null;
        _admissionUnconfirmed = false;
        ClearAdmissionSecrets();
        ClassAdmissionCard.IsVisible = false;
    }

    private async void RefreshAdmissionClicked(object? sender, RoutedEventArgs args) => await ReadClassAdmissionAsync();

    private async Task ReadClassAdmissionAsync()
    {
        if (_admissionRequest is not null || _admissionTarget is not { } target || _client is not { } client) return;
        using var request = new CancellationTokenSource();
        _admissionRequest = request;
        _admission = null;
        ClearAdmissionSecrets();
        AdmissionStatus.Text = "正在读取这台大屏的接入设置…";
        SetAdmissionMessage(null);
        RenderClassAdmission();
        try
        {
            var snapshot = await client.GetClassAdmissionAsync(target.School.Id, target.Classroom.Id, target.Screen.Id, request.Token);
            if (_admissionRequest != request) return;
            _admission = snapshot;
            AdmissionStatus.Text = AdmissionStatusText(snapshot);
            if (_admissionUnconfirmed && snapshot.IsReady)
                SetAdmissionMessage("学校当前已开放此大屏。若需核实刚输入的口令，请在教室端验证。");
            else if (_admissionUnconfirmed)
                SetAdmissionMessage("已重新读取学校设置。请核对当前状态后再操作。");
            else if (!snapshot.SupportsGuardedSave)
                SetAdmissionMessage("当前学校服务仅支持查看接入设置。更新学校服务后即可在此保存。");
            _admissionUnconfirmed = false;
        }
        catch (Exception error) when (IsServiceError(error))
        {
            if (_admissionRequest != request || ReturnToLoginIfExpired(error)) return;
            AdmissionStatus.Text = "暂无法读取大屏接入设置。";
            SetAdmissionMessage(ServiceMessage(error));
        }
        finally { FinishAdmissionRequest(request); }
    }

    private async void EnableAdmissionClicked(object? sender, RoutedEventArgs args)
    {
        if (_admissionRequest is not null || _client is not { } client || _admissionTarget is not { } target ||
            _admission is not { SupportsGuardedSave: true, IsReady: false } snapshot) return;

        string? secret = null;
        if (!snapshot.Configured)
        {
            secret = AdmissionSecret.Text ?? "";
            if (!ClassAdmission.IsValidSecret(secret))
            {
                SetAdmissionMessage("请填写 8 至 128 位口令，避免使用换行或其他控制字符。");
                AdmissionSecret.Focus();
                return;
            }
            if (!string.Equals(secret, AdmissionSecretConfirmation.Text, StringComparison.Ordinal))
            {
                SetAdmissionMessage("两次输入的口令不一致，请重新核对。");
                AdmissionSecretConfirmation.Focus();
                return;
            }
        }

        using var request = new CancellationTokenSource();
        _admissionRequest = request;
        ClearAdmissionSecrets();
        AdmissionStatus.Text = "正在提交这台大屏的接入设置…";
        SetAdmissionMessage(null);
        RenderClassAdmission();
        try
        {
            var saved = await client.EnableClassAdmissionAsync(snapshot, secret, Guid.NewGuid(), request.Token);
            if (_admissionRequest != request) return;
            _admission = saved;
            AdmissionStatus.Text = AdmissionStatusText(saved);
        }
        catch (Exception error) when (IsServiceError(error))
        {
            if (_admissionRequest != request || ReturnToLoginIfExpired(error)) return;
            _admission = null;
            _admissionUnconfirmed = error is ConsoleApiException { Code: "ADMISSION_UNCONFIRMED" };
            AdmissionStatus.Text = _admissionUnconfirmed ? "保存结果待核对" : "此次设置未保存";
            SetAdmissionMessage(ServiceMessage(error));
        }
        finally { FinishAdmissionRequest(request); }
        if (_admissionTarget == target)
        {
            RefreshAdmissionButton.Focus();
            ShowAdmissionCard(target);
        }
    }

    private static string AdmissionStatusText(ClassAdmission snapshot)
    {
        if (snapshot.IsReady) return "已开放直接接入，等待教室端连接并上报。";
        if (snapshot.Enabled) return "此大屏已预授权，本班尚未设置管理口令。";
        return snapshot.Configured ? "本班已有管理口令。开放此大屏时将沿用原口令。" : "本班尚未设置管理口令。";
    }

    private void RenderClassAdmission()
    {
        bool busy = _admissionRequest is not null;
        bool canEnable = !busy && _admission is { SupportsGuardedSave: true, IsReady: false };
        AdmissionSecretFields.IsVisible = canEnable && _admission is { Configured: false };
        AdmissionAuthorizationNote.IsVisible = canEnable && _admission is { Enabled: false };
        EnableAdmissionButton.IsVisible = canEnable;
        RefreshAdmissionButton.IsEnabled = !busy;
        if (_admission is { IsReady: true } && !AdmissionMessage.IsVisible)
            SetAdmissionMessage("现场在 NPEduTools 的「学校互联」核对服务，选择此大屏，用班级管理口令确认连接。终端上报后，刷新班级即可查看设备。");
    }

    private void FinishAdmissionRequest(CancellationTokenSource request)
    {
        if (_admissionRequest != request) return;
        _admissionRequest = null;
        RenderClassAdmission();
    }

    private void ClearAdmissionSecrets()
    {
        AdmissionSecret.Clear();
        AdmissionSecretConfirmation.Clear();
    }

    private void SetAdmissionMessage(string? message)
    {
        AdmissionMessage.Text = message;
        AdmissionMessage.IsVisible = message is not null;
    }

    private void ShowAdmissionCard(AdmissionTarget target) => Dispatcher.UIThread.Post(() =>
    {
        if (_admissionTarget == target) ClassAdmissionCard.BringIntoView();
    }, DispatcherPriority.Loaded);
}
