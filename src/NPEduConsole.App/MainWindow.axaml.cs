using System.Globalization;
using System.Net.Http;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NPEduConsole.Core;
using CoreScreen = NPEduConsole.Core.Screen;

namespace NPEduConsole.App;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<Button, DeviceSelection> _deviceButtons = new();
    private ConsoleClient? _client;
    private CancellationTokenSource? _request;
    private School? _school;
    private Classroom? _classroom;
    private DeviceSelection? _device;
    private bool _settingSelection;
    private bool _signingOut;
    private bool _allowClose;
    private bool _closeAfterSignOut;
    private bool _showClassStudy;
    private bool _showSchoolAudit;
    private bool _serverStatusOpen;

    private sealed record DeviceSelection(string ScreenId, string ScreenName, Device Device);

    public MainWindow()
    {
        InitializeComponent();
        SchoolPicker.ItemTemplate = new FuncDataTemplate<School>((school, _) =>
            new TextBlock { Text = school?.Name });
        ClassPicker.ItemTemplate = new FuncDataTemplate<Classroom>((room, _) =>
            new TextBlock { Text = ClassroomName(room), TextWrapping = TextWrapping.Wrap });
        MicrophonePicker.ItemTemplate = new FuncDataTemplate<MicrophoneChoice>((choice, _) =>
        {
            var label = new TextBlock { Text = choice?.Label };
            if (choice is not null && choice.Label != choice.Microphone.Name)
                ToolTip.SetTip(label, choice.Microphone.Id);
            return label;
        });
        _equipmentPoll.Tick += PollEquipment;
        _softwarePoll.Tick += PollSoftware;
        _schoolStatusTimer.Tick += PollSchoolServerStatus;
        StudyWorkspace.SessionExpired += error => ReturnToLoginIfExpired(error);
        ScreenReplacementWorkspace.SessionExpired += error => ReturnToLoginIfExpired(error);
        ScreenReplacementWorkspace.CloseRequested += () => CloseScreenReplacement(restoreFocus: true);
        ScreenReplacementWorkspace.HandoverObserved += RetireReplacedDevice;
        SchoolAuditWorkspace.SessionExpired += error => ReturnToLoginIfExpired(error);
        SchoolAuditWorkspace.BackRequested += () =>
        {
            _showSchoolAudit = false;
            UpdateSchoolAuditWorkspace();
            OpenSchoolAuditButton.Focus();
        };
        SchoolOverviewWorkspace.SessionExpired += error => ReturnToLoginIfExpired(error);
        SchoolOverviewWorkspace.TaskRequested += async (overview, task) => await OpenSchoolOverviewTaskAsync(overview, task);
        Activated += (_, _) => RefreshAfterWebOverviewTask();
        LoginView.AddHandler(KeyDownEvent, OnLoginKeyDown, RoutingStrategies.Tunnel);
        Closing += OnClosing;
    }

    private async void LoginClicked(object? sender, RoutedEventArgs args) => await LoginAsync();

    private async void OpenServerStatusClicked(object? sender, RoutedEventArgs args)
    {
        if (_serverStatusOpen || _request is not null || _signingOut || _client is not null || !LoginView.IsVisible) return;
        _serverStatusOpen = true;
        try { await new ServerStatusWindow(ServerAddress.Text).ShowDialog(this); }
        finally
        {
            _serverStatusOpen = false;
            if (LoginView.IsVisible) OpenServerStatusButton.Focus();
        }
    }

    private async void OnLoginKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || !LoginView.IsVisible || !LoginButton.IsEnabled) return;
        args.Handled = true;
        await LoginAsync();
    }

    private async Task LoginAsync()
    {
        if (_request is not null || _signingOut || _client is not null) return;
        string address = (ServerAddress.Text ?? "").Trim();
        string schoolCode = (SchoolCode.Text ?? "").Trim();
        string username = (Username.Text ?? "").Trim();
        string password = Password.Text ?? "";
        if (address.Length == 0)
        {
            SetLoginMessage("请输入完整的服务地址，例如 https://school.example.edu。");
            ServerAddress.Focus();
            return;
        }

        if (schoolCode.Length == 0 || username.Length == 0 || password.Length == 0)
        {
            SetLoginMessage("请填写学校代码、短账号和密码。");
            (schoolCode.Length == 0 ? SchoolCode : username.Length == 0 ? Username : Password).Focus();
            return;
        }

        using var request = StartRequest();
        SetLoginMessage("");
        SetLoginBusy(true);
        ConsoleClient? candidate = null;
        try
        {
            candidate = new ConsoleClient(address);
            Password.Clear();
            var schools = await candidate.LoginAsync(schoolCode, username, password, request.Token);
            if (!IsCurrent(request)) return;
            _client = candidate;
            candidate = null;
            _settingSelection = true;
            SchoolPicker.ItemsSource = schools;
            _settingSelection = false;
            SchoolPicker.IsEnabled = schools.Count > 0;
            AccountName.Text = username;
            LoginView.IsVisible = false;
            Workspace.IsVisible = AccountBar.IsVisible = true;
            _ = RefreshSchoolServerStatusAsync();
            ClassListMessage.Text = schools.Count == 0 ? "此账号暂无获授权学校。" : "请先选择学校。";
            SetWorkspaceMessage(schools.Count == 0 ? "此账号暂无获授权学校。" : "请选择学校和班级。");
        }
        catch (Exception error) when (IsServiceError(error))
        {
            if (IsCurrent(request)) SetLoginMessage(ServiceMessage(error));
        }
        finally
        {
            candidate?.Dispose();
            if (FinishRequest(request))
            {
                SetLoginBusy(false);
                if (_client is not null)
                {
                    SchoolPicker.Focus();
                    if (SchoolPicker.ItemCount == 1) SchoolPicker.SelectedIndex = 0;
                }
                else (candidate is null ? ServerAddress : Password).Focus();
            }
        }
    }

    private async void SchoolChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_settingSelection && _client is not null)
        {
            _showClassStudy = false;
            _showSchoolAudit = false;
            var school = SchoolPicker.SelectedItem as School;
            _showSchoolOverview = school is not null;
            _overviewJourney = false;
            _overviewNavigation = null;
            _overviewWebReturn = null;
            await LoadSchoolAsync(school);
        }
    }

    private async Task<bool> LoadSchoolAsync(School? school, string? restoredClass = null, DeviceSelection? restoredDevice = null)
    {
        if (_client is not { } client) return false;
        using var request = StartRequest();
        if (_firstScreen is { } preparation && (preparation.State == FirstScreenState.Finished ||
            preparation.School != school || preparation.ClassId != restoredClass)) ResetFirstScreen();
        if (_school != school)
        {
            ChangeClassCreationSchool(school);
            ResetClassMaintenance();
        }
        _school = school;
        InactiveClassesFilter.IsEnabled = school is not null;
        bool inactiveOnly = InactiveClassesFilter.IsChecked == true;
        OpenClassCreationButton.IsEnabled = school is not null;
        UpdateSchoolAuditWorkspace();
        RefreshButton.IsEnabled = school is not null;
        _settingSelection = true;
        ClassPicker.ItemsSource = null;
        _settingSelection = false;
        SetClassroom(null);
        ClassListMessage.Text = school is null ? "请先选择学校。" : "正在读取班级…";
        ClassListMessage.IsVisible = true;
        if (school is null)
        {
            SetWorkspaceMessage("请选择学校和班级。");
            FinishRequest(request);
            return false;
        }

        WorkspaceProgress.IsVisible = true;
        SetWorkspaceMessage("正在读取该学校的班级…");
        Classroom? selected = null;
        bool loaded = false;
        try
        {
            IReadOnlyList<Classroom> rooms = inactiveOnly
                ? (await client.GetInactiveCurrentClassesAsync(school.Id, request.Token)).Select(row =>
                    new Classroom(row.Id, row.Name, row.GradeName, []) { TermId = row.TermId, IsActive = false }).ToArray()
                : await client.GetClassroomsAsync(school.Id, request.Token);
            if (!IsCurrent(request)) return false;
            _settingSelection = true;
            ClassPicker.ItemsSource = rooms;
            selected = rooms.FirstOrDefault(room => room.Id == restoredClass);
            ClassPicker.SelectedItem = selected;
            _settingSelection = false;
            ClassListMessage.IsVisible = rooms.Count == 0;
            ClassListMessage.Text = inactiveOnly ? "当前学期暂无已停用行政班。" : "该学校暂无可查看班级。";
            SetWorkspaceMessage(rooms.Count == 0 ? ClassListMessage.Text : "请选择班级。");
            loaded = true;
        }
        catch (Exception error) when (IsServiceError(error))
        {
            if (IsCurrent(request) && !ReturnToLoginIfExpired(error))
            {
                ClassListMessage.Text = "读取失败，可点击刷新重试。";
                SetWorkspaceMessage(ServiceMessage(error), isError: true);
            }
        }
        finally
        {
            if (FinishRequest(request)) WorkspaceProgress.IsVisible = false;
        }

        if (selected is not null && _client == client && _school == school)
            await SelectClassroomAsync(selected, restoredDevice, userRefresh: restoredDevice is not null);
        return loaded;
    }

    private async void ClassChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_settingSelection && _client is not null)
        {
            var room = ClassPicker.SelectedItem as Classroom;
            if (room is not null && _showSchoolOverview)
            {
                _showSchoolOverview = false;
                _overviewNavigation = null;
                _overviewJourney = true;
                UpdateSchoolAuditWorkspace();
            }
            if (_firstScreen is { } preparation && !preparation.Matches(room)) ResetFirstScreen();
            await SelectClassroomAsync(room);
        }
    }

    private async Task SelectClassroomAsync(Classroom? room, DeviceSelection? restoredDevice = null, bool userRefresh = false)
    {
        CancelRequest();
        WorkspaceProgress.IsVisible = false;
        SetClassroom(room);
        if (room is null)
        {
            SetWorkspaceMessage("请选择班级。");
            return;
        }
        if (!room.IsActive)
        {
            SetWorkspaceMessage("该班已停用。可重新启用原班，再查看学生名单与大屏。");
            return;
        }

        foreach (var screen in room.Screens)
            ScreenCards.Children.Add(BuildScreenCard(screen));
        ScreensHeading.IsVisible = room.Screens.Count > 0;
        SetWorkspaceMessage(room.Screens.Count > 0 || room.CanPrepareFirstScreen ? "" : room.HasInactiveScreens
            ? "本班有停用的大屏记录，请核对原有大屏。" : "本班暂无可用大屏。可刷新班级，核对当前大屏状态。");
        var previous = restoredDevice is null ? null : _deviceButtons.Values.FirstOrDefault(device =>
            device.ScreenId == restoredDevice.ScreenId && device.Device.Id == restoredDevice.Device.Id);
        var selected = previous ?? (_deviceButtons.Count == 1 ? _deviceButtons.Values.Single() : null);
        if (selected is not null) await LoadDeviceAsync(selected, userRefresh: userRefresh);
    }

    private Control BuildScreenCard(CoreScreen screen)
    {
        var list = new StackPanel { Spacing = 8 };
        var card = new Border { Child = list, Padding = new Thickness(17, 13), Classes = { "card" }, Focusable = true };
        AutomationProperties.SetAutomationId(card, "SchoolScreen-" + screen.Id);
        _overviewScreenCards[screen.Id] = card;
        var summary = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        summary.Children.Add(new TextBlock { Text = ScreenDisplayName(screen), FontSize = 16, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var replacement = BuildScreenReplacementButton(screen);
        Grid.SetColumn(replacement, 1);
        summary.Children.Add(replacement);
        list.Children.Add(summary);
        if (screen.DeviceOverviewUnavailable)
        {
            list.Children.Add(new TextBlock { Text = "设备状态暂不可用", Classes = { "muted" } });
            return card;
        }
        if (screen.Devices.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "该大屏暂无有效绑定设备。", Classes = { "muted" } });
            var configure = new Button { Content = "配置口令接入", Classes = { "action" }, Tag = screen };
            AutomationProperties.SetName(configure, $"配置口令接入：{screen.Name}");
            AutomationProperties.SetAutomationId(configure, "ConfigureClassAdmission-" + screen.Id);
            configure.Click += OpenAdmissionClicked;
            _admissionButtons[screen.Id] = configure;
            list.Children.Add(configure);
        }
        foreach (var device in screen.Devices)
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock { Text = device.Name, Classes = { "label" } });
            row.Children.Add(new TextBlock
            {
                Text = $"{ConnectivityText(device.Connectivity)}  ·  最后上报 {FormatTime(device.LastSeenAt)}",
                Classes = { "muted" },
                FontSize = 12
            });
            var button = new Button
            {
                Content = row, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Classes = { "action", "device" }
            };
            AutomationProperties.SetName(button, $"查看设备 {device.Name}");
            _deviceButtons.Add(button, new(screen.Id, screen.Name, device));
            button.Click += DeviceClicked;
            list.Children.Add(button);
        }

        return card;
    }

    private async void DeviceClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Button button && _deviceButtons.TryGetValue(button, out var selection))
        {
            CloseClassAdmission();
            CloseScreenReplacement();
            await LoadDeviceAsync(selection, scrollIntoView: true);
        }
    }

    private async Task LoadDeviceAsync(DeviceSelection selection, bool scrollIntoView = false,
        bool userRefresh = false, bool background = false)
    {
        if (_client is not { } client || _school is not { } school) return;
        if (!background)
        {
            if (_device != selection) CloseSoftwareMaintenance();
            ResetEquipmentView();
            EquipmentDetails.IsVisible = false;
            WorkspaceProgress.IsVisible = true;
            SetEquipmentStatus("正在读取设备策略和回执…");
        }
        using var request = StartRequest();
        _device = selection;
        foreach (var (button, device) in _deviceButtons)
            button.Classes.Set("selected", device == selection);
        EquipmentCard.IsVisible = true;
        EquipmentDevice.Text = selection.Device.Name;
        RenderMicrophoneActions();
        if (scrollIntoView)
            Dispatcher.UIThread.Post(() =>
            {
                if (_device == selection && EquipmentCard.IsVisible) EquipmentCard.BringIntoView();
            }, DispatcherPriority.Loaded);
        try
        {
            var equipment = await client.GetEquipmentAsync(school.Id, selection.Device.Id, request.Token);
            if (!IsCurrent(request)) return;
            _equipment = equipment;
            ObserveMicrophoneChange(equipment, userRefresh);
            SetEquipmentStatus(equipment.StatusText);
            SchoolPolicy.Text = equipment.Locked ? "要求锁定" : "未要求锁定";
            MicrophoneName.Text = equipment.MicrophoneName ?? "未指定";
            PublishedVersion.Text = equipment.PublishedRevision.ToString(CultureInfo.InvariantCulture);
            AppliedVersion.Text = equipment.ReceivedAt.HasValue && equipment.AppliedRevision is { } revision
                ? revision.ToString(CultureInfo.InvariantCulture) : "待确认";
            EquipmentReceipt.Text = FormatTime(equipment.ReceivedAt);
            ReportedMicrophone.Text = equipment.ReportedMicrophoneName ?? "暂无具体输入上报";
            EquipmentEditingStatus.Text = equipment.EditingStatusText;
            EquipmentDetails.IsVisible = true;
        }
        catch (Exception error) when (IsServiceError(error))
        {
            if (IsCurrent(request) && !ReturnToLoginIfExpired(error))
            {
                _equipmentReadFailed = true;
                SetEquipmentStatus(ServiceMessage(error), isError: true);
            }
        }
        finally
        {
            if (FinishRequest(request))
            {
                WorkspaceProgress.IsVisible = false;
                RenderMicrophoneActions();
            }
        }
    }

    private async void RefreshClicked(object? sender, RoutedEventArgs args)
    {
        if (_firstScreen is { } preparation && preparation.State != FirstScreenState.Finished)
        {
            await RefreshFirstScreenAsync(preparation);
            return;
        }
        if (_createdClassToOpen is { } created && _client is { } client && _school is { } school)
            await OpenCreatedClassRosterAsync(client, school, created);
        else await LoadSchoolAsync(_school, _classroom?.Id, _device);
    }

    private async void LogoutClicked(object? sender, RoutedEventArgs args) => await SignOutAsync();

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_allowClose) return;
        CancelRequest();
        if (_client is null && !_signingOut) return;
        args.Cancel = true;
        _closeAfterSignOut = true;
        if (!_signingOut) await SignOutAsync();
    }

    private async Task SignOutAsync()
    {
        if (_client is not { } client) return;
        _signingOut = true;
        ResetSession();
        SetLoginBusy(true);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await client.LogoutAsync(deadline.Token);
            SetLoginMessage("");
        }
        catch (Exception error) when (IsServiceError(error))
        {
            SetLoginMessage("本机已退出，服务器注销暂未确认。");
        }
        finally
        {
            client.Dispose();
            _signingOut = false;
            SetLoginBusy(false);
            if (_closeAfterSignOut)
            {
                _allowClose = true;
                Dispatcher.UIThread.Post(Close);
            }
        }
    }

    private bool ReturnToLoginIfExpired(Exception error)
    {
        if (error is not ConsoleApiException { StatusCode: 401 }) return false;
        var client = _client;
        ResetSession();
        client?.Dispose();
        SetLoginMessage("登录已失效，请重新登录。");
        Password.Focus();
        return true;
    }

    private void ResetSession()
    {
        CancelRequest();
        ResetSchoolServerStatus();
        CloseClassRoster();
        ResetClassCreation();
        ResetClassMaintenance();
        ResetFirstScreen();
        _client = null;
        _showClassStudy = false;
        _showSchoolAudit = false;
        _showSchoolOverview = _overviewJourney = false;
        _overviewWebReturn = null;
        _overviewNavigation = null;
        CancelMicrophoneChanges();
        CancelSoftwareSession();
        _school = null;
        UpdateSchoolAuditWorkspace();
        _settingSelection = true;
        SchoolPicker.ItemsSource = ClassPicker.ItemsSource = null;
        _settingSelection = false;
        SetClassroom(null);
        AccountName.Text = null;
        WorkspaceProgress.IsVisible = Workspace.IsVisible = AccountBar.IsVisible = false;
        LoginView.IsVisible = true;
        Password.Clear();
    }

    private void SetClassroom(Classroom? room)
    {
        if (_classroom?.Id != room?.Id || _classroom?.TermId != room?.TermId) CloseClassRoster();
        OpenClassRosterButton.IsEnabled = room?.IsActive == true;
        OpenClassRosterButton.IsVisible = room?.IsActive != false;
        OpenClassMaintenanceButton.IsVisible = room?.TermId is not null;
        OpenClassMaintenanceButton.Content = room?.IsActive == false ? "重新启用" : "维护班级";
        CloseClassAdmission();
        CloseScreenReplacement();
        CloseSoftwareMaintenance();
        ResetEquipmentView();
        _classroom = room;
        _device = null;
        _deviceButtons.Clear();
        _admissionButtons.Clear();
        _overviewScreenCards.Clear();
        ScreenCards.Children.Clear();
        ScreensHeading.IsVisible = EquipmentCard.IsVisible = EquipmentDetails.IsVisible = false;
        EquipmentDevice.Text = EquipmentStatus.Text = null;
        ClassContext.Text = room is null ? _school?.Name : $"{_school?.Name} / {ClassroomName(room)}";
        UpdateStudyWorkspace();
        RenderFirstScreen();
    }

    private void ClassStudyClicked(object? sender, RoutedEventArgs args)
    {
        if (_classroom is null) return;
        _showClassStudy = !_showClassStudy;
        UpdateStudyWorkspace();
    }

    private void SchoolAuditClicked(object? sender, RoutedEventArgs args)
    {
        _showSchoolOverview = false;
        _overviewNavigation = null;
        _showSchoolAudit = !_showSchoolAudit;
        UpdateSchoolAuditWorkspace();
    }

    private void UpdateSchoolAuditWorkspace()
    {
        bool show = _showSchoolAudit && _school is not null;
        bool overview = _showSchoolOverview && _school is not null && !show;
        if (show) CloseScreenReplacement();
        OpenSchoolAuditButton.IsEnabled = _school is not null;
        OpenSchoolAuditButton.Classes.Set("accent", show);
        ClassPickerHeading.IsVisible = ClassPickerPanel.IsVisible = InactiveClassesFilter.IsVisible = !show;
        ClassManagementWorkspace.IsVisible = !show && !overview;
        ReturnToSchoolOverviewButton.IsVisible = ClassManagementWorkspace.IsVisible && _overviewJourney;
        SchoolAuditWorkspace.IsVisible = show;
        SchoolAuditWorkspace.SetScope(show ? _client : null, show ? _school : null);
        OpenSchoolOverviewButton.IsEnabled = _school is not null;
        OpenSchoolOverviewButton.Classes.Set("accent", overview);
        SchoolOverviewWorkspace.IsVisible = overview;
        SchoolOverviewWorkspace.SetScope(overview ? _client : null, overview ? _school : null);
    }

    private void UpdateStudyWorkspace()
    {
        bool show = _showClassStudy && _classroom?.IsActive == true;
        if (show) CloseScreenReplacement();
        OpenClassStudyButton.IsVisible = _classroom?.IsActive == true;
        OpenClassStudyButton.Content = show ? "返回教室设备" : "本班学校自习";
        ClassWorkspaceTitle.Text = show ? "本班学校自习" : _classroom?.IsActive == false ? "已停用班级" : "教室设备";
        ClassDeviceWorkspace.IsVisible = !show;
        StudyWorkspace.IsVisible = show;
        if (show) StudyWorkspace.SetScope(_client, _school?.Id, _classroom?.TermId, _classroom);
        else StudyWorkspace.SetScope(null, null, null, null);
        ClassWorkspaceScroll.Offset = default;
    }

    private CancellationTokenSource StartRequest()
    {
        CancelRequest();
        return _request = new CancellationTokenSource();
    }

    private bool IsCurrent(CancellationTokenSource request) => ReferenceEquals(_request, request) && !request.IsCancellationRequested;

    private bool FinishRequest(CancellationTokenSource request)
    {
        if (!IsCurrent(request)) return false;
        _request = null;
        return true;
    }

    private void CancelRequest()
    {
        var request = _request;
        _request = null;
        request?.Cancel();
    }

    private void SetLoginBusy(bool busy)
    {
        LoginButton.IsEnabled = ServerAddress.IsEnabled = SchoolCode.IsEnabled = Username.IsEnabled = Password.IsEnabled = !busy;
        OpenServerStatusButton.IsEnabled = !busy;
        LoginProgress.IsVisible = busy;
        LoginButton.Content = busy ? (_signingOut ? "正在退出…" : "正在登录…") : "登录";
    }

    private void SetLoginMessage(string message)
    {
        LoginMessage.Text = message;
        LoginFeedback.IsVisible = message.Length > 0;
    }

    private void SetWorkspaceMessage(string message, bool isError = false)
    {
        WorkspaceNotice.IsVisible = message.Length > 0;
        WorkspaceMessage.Text = message;
        if (isError) WorkspaceMessage.Foreground = Brush.Parse("#9B2D36");
        else WorkspaceMessage.ClearValue(TextBlock.ForegroundProperty);
    }

    private void SetEquipmentStatus(string message, bool isError = false)
    {
        EquipmentStatus.Text = message;
        if (isError) EquipmentStatus.Foreground = Brush.Parse("#9B2D36");
        else EquipmentStatus.ClearValue(TextBlock.ForegroundProperty);
    }

    private static string ClassroomName(Classroom? room) =>
        room is null ? "" :
        string.IsNullOrWhiteSpace(room.GradeName) || room.Name.StartsWith(room.GradeName, StringComparison.Ordinal)
            ? room.Name : $"{room.GradeName} · {room.Name}";

    private static string ConnectivityText(string status) => status switch
    {
        "ONLINE" => "最近观测在线",
        "OFFLINE" => "最近观测离线",
        _ => "连接状态未知"
    };

    private static string FormatTime(DateTimeOffset? time) =>
        time?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "暂无";

    private static bool IsServiceError(Exception error) =>
        error is ConsoleApiException or HttpRequestException or OperationCanceledException or ArgumentException or System.Text.Json.JsonException;

    private static string ServiceMessage(Exception error) => error switch
    {
        ConsoleApiException service => service.Message,
        OperationCanceledException => "请求超时，请稍后重试。",
        HttpRequestException => "无法连接学校服务，请核对地址和网络。",
        ArgumentException => "服务地址或登录信息格式不正确。",
        _ => "学校服务返回的数据无法读取，请稍后重试。"
    };
}
