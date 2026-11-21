using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;
using CoreScreen = NPEduConsole.Core.Screen;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private enum FirstScreenState { Submitting, Unconfirmed, Review, ReadyToRetry, Registered, Finished }

    private sealed class FirstScreenPreparation(ConsoleClient client, School school, Classroom room)
    {
        public ConsoleClient Client { get; } = client;
        public School School { get; } = school;
        public string ClassId { get; } = room.Id;
        public string? TermId { get; } = room.TermId;
        public string ClassName { get; } = ClassroomName(room);
        public Guid RequestId { get; } = Guid.NewGuid();
        public FirstScreenState State { get; set; }
        public string Message { get; set; } = "正在登记本班的教室大屏…";
        public string? ScreenId { get; set; }
        public bool Refreshing { get; set; }
        public bool Matches(Classroom? room) => room?.Id == ClassId && room.TermId == TermId;
    }

    private readonly Dictionary<string, Button> _admissionButtons = new(StringComparer.Ordinal);
    private FirstScreenPreparation? _firstScreen;
    private CancellationTokenSource? _firstScreenRequest;

    private async void PrepareFirstScreenClicked(object? sender, RoutedEventArgs args)
    {
        if (_firstScreenRequest is not null || _client is not { } client || _school is not { } school ||
            _classroom is not { CanPrepareFirstScreen: true } room || _showClassStudy || _showSchoolAudit || _showSchoolOverview) return;
        if (_firstScreen is { } earlier && (earlier.State != FirstScreenState.ReadyToRetry || earlier.Refreshing || !earlier.Matches(room))) return;

        var preparation = _firstScreen ??= new FirstScreenPreparation(client, school, room);
        preparation.State = FirstScreenState.Submitting;
        preparation.Message = "正在登记本班的教室大屏…";
        using var request = new CancellationTokenSource();
        _firstScreenRequest = request;
        RenderFirstScreen();
        try
        {
            var id = await client.PrepareFirstScreenAsync(room, preparation.RequestId, request.Token);
            if (!IsCurrentFirstScreen(preparation)) return;
            preparation.ScreenId = id;
            preparation.State = FirstScreenState.Registered;
            preparation.Message = "教室大屏已登记。刷新后可继续配置口令接入。";
        }
        catch (Exception error) when (IsServiceError(error))
        {
            if (!IsCurrentFirstScreen(preparation) || ReturnToLoginIfExpired(error)) return;
            (preparation.State, preparation.Message) = error switch
            {
                ConsoleApiException { StatusCode: 0 or 408 or >= 500 } => (FirstScreenState.Unconfirmed,
                    "登记结果未确认，学校可能已创建此大屏。请先刷新列表核对。"),
                ConsoleApiException { StatusCode: 409 } => (FirstScreenState.Review, ServiceMessage(error)),
                ConsoleApiException { StatusCode: 404 or 426 } => (FirstScreenState.Finished,
                    "当前学校服务不支持在此登记首台大屏。请核对 KV 版本和班级状态。"),
                _ => (FirstScreenState.Finished, ServiceMessage(error))
            };
        }
        finally
        {
            if (_firstScreenRequest == request)
            {
                _firstScreenRequest = null;
                RenderFirstScreen();
            }
        }

        if (IsCurrentFirstScreen(preparation) && preparation.Matches(_classroom) && preparation.State == FirstScreenState.Registered && !_showClassStudy && !_showSchoolAudit && !_showSchoolOverview)
            await RefreshFirstScreenAsync(preparation);
    }

    private async void RefreshFirstScreenClicked(object? sender, RoutedEventArgs args)
    {
        if (_firstScreen is { } preparation) await RefreshFirstScreenAsync(preparation);
    }

    private async Task RefreshFirstScreenAsync(FirstScreenPreparation preparation)
    {
        if (!IsCurrentFirstScreen(preparation) || _firstScreenRequest is not null || preparation.Refreshing) return;
        preparation.Refreshing = true;
        RenderFirstScreen();
        try
        {
            bool refreshed = await LoadSchoolAsync(preparation.School, preparation.ClassId);
            if (!IsCurrentFirstScreen(preparation)) return;
            if (!refreshed || _classroom is not { } room || !preparation.Matches(room))
            {
                preparation.Message = preparation.ScreenId is not null
                    ? (refreshed ? "教室大屏已登记，当前学期列表中无法定位原班级。请核对班级后再刷新。"
                        : "教室大屏已登记，列表刷新失败。可重新刷新并继续配置已登记的大屏。")
                    : (refreshed ? "当前学期列表中无法定位原班级。请核对学期和班级后再操作。"
                        : "暂时无法核对大屏列表。请刷新后再操作。");
                if (preparation.State == FirstScreenState.ReadyToRetry) preparation.State = FirstScreenState.Review;
                return;
            }

            if (preparation.ScreenId is { } id)
                await OpenPreparedScreenAsync(preparation, room, id);
            else if (room.CanPrepareFirstScreen)
            {
                preparation.State = FirstScreenState.ReadyToRetry;
                preparation.Message = "刷新后仍未查到本班的大屏。可以重试原登记请求。";
            }
            else
            {
                preparation.State = FirstScreenState.Finished;
                preparation.Message = room.Screens.Count > 0
                    ? "列表已刷新。请先核对本班现有大屏，再选择对应的「配置口令接入」。"
                    : "列表已刷新。当前无法继续登记，请核对本班已有记录或设备状态。";
            }
        }
        finally
        {
            preparation.Refreshing = false;
            if (IsCurrentFirstScreen(preparation)) RenderFirstScreen();
        }
    }

    private async Task OpenPreparedScreenAsync(FirstScreenPreparation preparation, Classroom room, string id)
    {
        var screen = room.Screens.FirstOrDefault(candidate => candidate.Id == id);
        if (screen is null || screen.DeviceOverviewUnavailable)
        {
            preparation.Message = screen is null ? "教室大屏已登记，但列表中未找到服务器确认的大屏。请刷新并核对该班。"
                : "教室大屏已登记，设备状态暂不可用。可刷新后继续配置口令接入。";
            return;
        }
        if (screen.Devices.Count > 0)
        {
            preparation.State = FirstScreenState.Finished;
            preparation.Message = "教室大屏已登记，刷新时已查到接入设备。";
            return;
        }
        if (_showClassStudy || _showSchoolAudit || _showSchoolOverview || !_admissionButtons.TryGetValue(id, out var opener) ||
            opener.Tag is not CoreScreen target || !ReferenceEquals(screen, target)) return;

        preparation.State = FirstScreenState.Finished;
        preparation.Message = "教室大屏已登记。请核对下方口令接入设置，登记本身不会开放接入。";
        RenderFirstScreen();
        await OpenClassAdmissionAsync(screen, opener);
    }

    private bool IsCurrentFirstScreen(FirstScreenPreparation preparation) =>
        _firstScreen == preparation && _client == preparation.Client && _school == preparation.School;

    private void ResetFirstScreen()
    {
        var request = _firstScreenRequest;
        _firstScreenRequest = null;
        _firstScreen = null;
        request?.Cancel();
        RenderFirstScreen();
    }

    private void RenderFirstScreen()
    {
        var preparation = _firstScreen;
        bool eligible = _classroom?.CanPrepareFirstScreen == true;
        bool filteredOut = _classroom is null && !string.IsNullOrWhiteSpace(ClassSearch.Text);
        FirstScreenCard.IsVisible = eligible || preparation is not null && !filteredOut;
        bool retry = preparation?.State == FirstScreenState.ReadyToRetry;
        PrepareFirstScreenButton.IsVisible = eligible && (preparation is null || retry);
        PrepareFirstScreenButton.IsEnabled = preparation?.Refreshing != true;
        PrepareFirstScreenButton.Content = retry ? "重试准备" : "准备教室大屏";
        RefreshFirstScreenButton.IsVisible = preparation?.State is FirstScreenState.Registered or FirstScreenState.Unconfirmed or FirstScreenState.Review;
        RefreshFirstScreenButton.IsEnabled = preparation?.Refreshing != true;
        FirstScreenHeading.Text = preparation is null ? "本班暂无教室大屏" : $"{preparation.ClassName} · 教室大屏";
        FirstScreenFeedback.Text = preparation?.Refreshing == true ? "正在刷新班级和大屏列表…"
            : preparation?.Message ?? "先登记「教室大屏」，然后可继续配置班级管理口令接入。";
    }
}
