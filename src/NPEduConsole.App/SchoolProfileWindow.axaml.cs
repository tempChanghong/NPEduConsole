using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class SchoolProfileWindow : Window
{
    private sealed record PendingChange(SchoolProfile Baseline, SchoolProfileDraft Draft, string? Fingerprint, Guid Id, bool Missing = false);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, PendingChange>> Receipts = new();
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private SchoolProfile? _profile;
    private SchoolProfilePreview? _preview;
    private SchoolProfileDraft? _reviewedDraft;
    private Window? _destination;
    private string _mode = "LOCAL_PIN";
    private bool _closed, _busy, _binding, _writing, _showingResults, _compactEditorExpanded = true;

    public SchoolProfileWindow() => InitializeComponent();

    internal SchoolProfileWindow(ConsoleClient client, School school) : this()
    {
        _client = client;
        _school = school;
        ProfileScope.Text = school.Name;
        SizeChanged += (_, _) => Relayout();
        ProfileImpact.TargetRequested += row => _ = RunAsync(() => OpenImpactTargetAsync(row));
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            Relayout();
            await RunAsync(async () =>
            {
                if (Pending is { } pending)
                {
                    Bind(pending.Baseline, pending.Draft);
                    await CheckReceiptAsync(pending);
                }
                else await LoadAsync();
            });
        };
        Closing += (_, args) => args.Cancel = _writing && !_closed;
        Closed += (_, _) =>
        {
            _closed = true;
            ProfilePassword.Clear();
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
        Render();
    }

    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal SchoolProfile? SavedProfile { get; private set; }
    private Dictionary<string, PendingChange> PendingBySchool => Receipts.GetOrCreateValue(_client);
    private PendingChange? Pending => PendingBySchool.GetValueOrDefault(_school.Id);
    private string Mode => _mode;
    private bool Rotate => Mode == "SHARED_PASSWORD" && ProfileRotate.IsChecked == true;

    private SchoolProfileDraft Draft()
    {
        var name = ProfileName.Text?.Trim() ?? "";
        var fallback = ProfileOAuthFallback.IsChecked == true;
        return new(name == _profile?.Name ? null : name, Mode == _profile?.TeacherAuthMode ? null : Mode,
            fallback == _profile?.AllowOAuthTeacherLogin ? null : fallback, Rotate);
    }

    private static bool ChangesPolicy(SchoolProfileDraft draft) => draft.TeacherAuthMode is not null ||
        draft.AllowOAuthTeacherLogin.HasValue || draft.RotateSharedPassword;

    private async Task LoadAsync(SchoolProfileDraft? keep = null)
    {
        var profile = await _client.GetSchoolProfileAsync(_school.Id, _lifetime.Token);
        if (_closed) return;
        Bind(profile, keep);
        var current = await _client.PreviewSchoolProfileAsync(profile, new(null, null, null, false), _lifetime.Token);
        if (_closed) return;
        _preview = current;
        _reviewedDraft = null;
        PushImpact();
    }

    private void Bind(SchoolProfile profile, SchoolProfileDraft? keep = null)
    {
        _profile = profile;
        _binding = true;
        try
        {
            ProfileScope.Text = $"{profile.Name} · 学校代码 {profile.Code} · 现行教师方式 {ModeLabel(profile.TeacherAuthMode)}";
            ProfileName.Text = keep?.Name ?? profile.Name;
            var mode = keep?.TeacherAuthMode ?? profile.TeacherAuthMode;
            _mode = mode;
            ProfilePinMode.IsChecked = mode == "LOCAL_PIN";
            ProfileSharedMode.IsChecked = mode == "SHARED_PASSWORD";
            ProfileEmailMode.IsChecked = mode == "OAUTH_EMAIL";
            ProfileOAuthFallback.IsChecked = keep?.AllowOAuthTeacherLogin ?? profile.AllowOAuthTeacherLogin;
            ProfileRotate.IsChecked = keep?.RotateSharedPassword == true || mode == "SHARED_PASSWORD" && !profile.HasSharedTeacherPassword;
            ProfilePassword.Clear();
        }
        finally { _binding = false; }
        _preview = null;
        _reviewedDraft = null;
        PushImpact();
        Render();
    }

    private void Relayout()
    {
        bool narrow = ClientSize.Width < 930;
        ProfileCompactEditor.IsVisible = narrow;
        ProfileCompactEditor.Content = _compactEditorExpanded ? "收起设置" : "展开设置";
        ProfileEditor.IsVisible = !narrow || _compactEditorExpanded;
        ProfilePanes.ColumnDefinitions = new(narrow ? "*" : "418,*");
        ProfilePanes.RowDefinitions = new(narrow ? "Auto,*" : "*");
        Grid.SetColumn(ProfileImpactPane, narrow ? 0 : 1);
        Grid.SetRow(ProfileImpactPane, narrow ? 1 : 0);
        ProfileEditor.MaxHeight = narrow ? Math.Max(120, Math.Min(195, ClientSize.Height * .42)) : double.PositiveInfinity;
    }

    private void CompactEditorClicked(object? sender, RoutedEventArgs e)
    {
        _compactEditorExpanded = !_compactEditorExpanded;
        Relayout();
        if (_compactEditorExpanded) ProfileName.Focus();
    }

    private void MakeRoomForImpact()
    {
        _compactEditorExpanded = false;
        Relayout();
    }

    private void DraftChanged(object? sender, TextChangedEventArgs e) => DraftEdited();
    private void ModeChanged(object? sender, RoutedEventArgs e)
    {
        if (_binding || _profile is null || sender is not RadioButton {IsChecked: true}) return;
        _mode = sender == ProfileSharedMode ? "SHARED_PASSWORD" : sender == ProfileEmailMode ? "OAUTH_EMAIL" : "LOCAL_PIN";
        _binding = true;
        ProfileRotate.IsChecked = Mode == "SHARED_PASSWORD" && !_profile.HasSharedTeacherPassword;
        ProfilePassword.Clear();
        _binding = false;
        DraftEdited();
    }
    private void ToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (!_binding && sender == ProfileRotate && !Rotate) ProfilePassword.Clear();
        DraftEdited();
    }
    private void DraftEdited()
    {
        if (_binding || _closed || _profile is null) return;
        _showingResults = false;
        PushImpact();
        Render();
    }

    private void PushImpact()
    {
        var draft = Draft();
        var changed = ChangesPolicy(draft);
        var matches = _reviewedDraft == draft;
        ProfileImpactHeading.Text = _showingResults ? "保存后 · 重新核对账号与邀请" : matches ? ReviewedTitle(draft) :
            changed ? "变更后 · 请先预览教师影响" : "现行策略与下一次登录";
        ProfileImpact.Present(changed && !matches ? null : _preview?.Impact,
            _preview is not null && (!changed || matches) && Pending is null);
    }

    private string ReviewedTitle(SchoolProfileDraft draft)
    {
        var changes = new List<string>();
        if (draft.TeacherAuthMode is { } mode) changes.Add($"{ModeLabel(_profile!.TeacherAuthMode)} → {ModeLabel(mode)}");
        if (draft.AllowOAuthTeacherLogin is { } allow) changes.Add(allow ? "开启备用 OAuth" : "关闭备用 OAuth");
        if (draft.RotateSharedPassword) changes.Add("设置通用口令");
        return $"保存前 · {string.Join(" · ", changes)}";
    }

    private void Render()
    {
        if (_client is null || _closed) return;
        var draft = Draft();
        ProfileSharedFields.IsVisible = Mode == "SHARED_PASSWORD";
        ProfileRotate.IsEnabled = _profile?.HasSharedTeacherPassword == true;
        ProfileOAuthFallback.IsEnabled = Mode != "OAUTH_EMAIL";
        ProfilePassword.IsVisible = Rotate || Pending is {Missing: true, Draft.RotateSharedPassword: true};
        // Only the password input is usable on a missing receipt. Keep the original non-secret fields fixed.
        ProfileEditor.IsEnabled = !_busy && _profile is not null;
        ProfileName.IsEnabled = ProfilePinMode.IsEnabled = ProfileSharedMode.IsEnabled = ProfileEmailMode.IsEnabled = Pending is null;
        ProfileRotate.IsEnabled &= Pending is null;
        ProfileOAuthFallback.IsEnabled &= Pending is null;
        ProfilePassword.IsEnabled = Pending is null || Pending.Missing;
        ProfilePrimary.IsEnabled = !_busy && _profile is not null && (Pending is {Missing: true} || Pending is null &&
            (ChangesPolicy(draft) || draft.Name is not null));
        ProfilePrimary.Content = Pending is not null ? "按原编号重试" : ChangesPolicy(draft) && _reviewedDraft != draft
            ? "预览教师影响" : "保存学校设置";
        ProfileReadReceipt.IsVisible = Pending is not null;
        ProfileReadReceipt.IsEnabled = !_busy;
        ProfileRefresh.IsEnabled = !_busy && Pending is null;
        ProfileClose.IsEnabled = !_writing;
    }

    private async void PrimaryClicked(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_profile is not { } profile) return;
        if (Pending is {Missing: true} prior)
        {
            var retryPassword = InputPassword(prior.Draft);
            if (prior.Draft.RotateSharedPassword && retryPassword is null) return;
            await CheckReceiptAsync(prior, preserveInput: true);
            if (!_closed && Pending is {Missing: true}) await SendAsync(prior, retryPassword);
            return;
        }
        var draft = Draft();
        if (ChangesPolicy(draft) && _reviewedDraft != draft)
        {
            _preview = await _client.PreviewSchoolProfileAsync(profile, draft, _lifetime.Token);
            if (_closed) return;
            _reviewedDraft = draft;
            ProfileNotice.Text = "请核对影响名单。确认后保存，现有会话不会被统一注销。";
            MakeRoomForImpact();
            PushImpact();
            return;
        }
        var password = InputPassword(draft);
        if (draft.RotateSharedPassword && password is null) return;
        var change = new PendingChange(profile, draft, ChangesPolicy(draft) ? _preview?.Fingerprint : null, Guid.NewGuid());
        await SendAsync(change, password);
    });

    private string? InputPassword(SchoolProfileDraft draft)
    {
        if (!draft.RotateSharedPassword) return null;
        if (ProfilePassword.Text is {Length: >= 8 and <= 64} password) return password;
        ProfileNotice.Text = "请输入本次 8 至 64 个字符的通用口令。";
        _compactEditorExpanded = true;
        Relayout();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ProfilePassword.Focus());
        return null;
    }

    private async Task SendAsync(PendingChange pending, string? password)
    {
        PendingBySchool[_school.Id] = pending with {Missing = false};
        ProfilePassword.Clear();
        _writing = true;
        Render();
        ProfileNotice.Text = "正在保存当前学校设置…";
        try
        {
            SchoolProfileReceipt receipt;
            try { receipt = await _client.SaveSchoolProfileAsync(pending.Baseline, pending.Draft, pending.Fingerprint, pending.Id, password, _lifetime.Token); }
            catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
            {
                if (!_closed) await CheckReceiptAsync(pending);
                return;
            }
            catch (ConsoleApiException)
            {
                PendingBySchool.Remove(_school.Id);
                throw;
            }
            if (!_closed) await ApplyReceiptAsync(receipt);
        }
        finally { _writing = false; }
    }

    private async Task CheckReceiptAsync(PendingChange pending, bool preserveInput = false)
    {
        if (!preserveInput) ProfilePassword.Clear();
        var receipt = await _client.GetSchoolProfileReceiptAsync(_school.Id, pending.Id, _lifetime.Token);
        if (_closed) return;
        if (receipt is not null) await ApplyReceiptAsync(receipt);
        else
        {
            PendingBySchool[_school.Id] = pending with {Missing = true};
            if (pending.Draft.RotateSharedPassword) { _compactEditorExpanded = true; Relayout(); }
            ProfileNotice.Text = pending.Draft.RotateSharedPassword
                ? "暂未查到回执。可用原编号重试，需重新输入口令。如原写入稍后确认，只认原操作。"
                : "暂未查到回执，可以按原编号重试。";
        }
    }

    private async Task ApplyReceiptAsync(SchoolProfileReceipt receipt)
    {
        PendingBySchool.Remove(_school.Id);
        SavedProfile = receipt.Profile;
        _showingResults = true;
        Bind(receipt.Profile);
        MakeRoomForImpact();
        var message = receipt.PasswordRotated ? "已核对本次口令设置已保存。回执不含口令，如无法确认最初提交值，请另行轮换。" : "服务器已确认本次设置。";
        ProfileNotice.Text = message;
        try
        {
            await LoadAsync();
            if (!_closed)
            {
                SavedProfile = _profile ?? receipt.Profile;
                ProfileNotice.Text = message;
            }
        }
        catch (ConsoleApiException error) when (!_closed && error.StatusCode != 401)
        {
            HandleAccessFailure(error);
            ProfileNotice.Text = $"{message} 当前影响名单暂不可用。{error.Message}";
        }
    }

    private async Task OpenImpactTargetAsync(TeacherLoginImpactRow row)
    {
        if (Pending is not null || _destination is not null || _profile is null) return;
        switch (row.Action)
        {
            case "TEACHER_ACCOUNT":
                _destination = new TeacherAccountsWindow(_client, _school, row.Id) {SessionExpired = SessionExpired};
                break;
            case "WORKSPACE_INVITATION" when row.TermId is not null:
                _destination = new TeacherAllocationWindow(_client, _school, row.TermId, null)
                    {SessionExpired = SessionExpired, InitialInvitationId = row.Id};
                break;
            default: return;
        }
        var previousDraft = Draft();
        try { await _destination.ShowDialog(this); }
        finally { _destination = null; }
        if (_closed) return;
        await LoadAsync(previousDraft);
        if (!_closed && _profile is { } baseline && ChangesPolicy(previousDraft))
        {
            _preview = await _client.PreviewSchoolProfileAsync(baseline, previousDraft, _lifetime.Token);
            if (!_closed) { _reviewedDraft = previousDraft; PushImpact(); }
        }
        if (!_closed) ProfileImpact.FocusTarget(row.Kind, row.Id);
    }

    private async void RefreshClicked(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await LoadAsync(Draft());
        if (!_closed) ProfileNotice.Text = "";
    });
    private async void ReceiptClicked(object? sender, RoutedEventArgs e)
    {
        if (Pending is { } pending) await RunAsync(() => CheckReceiptAsync(pending));
    }
    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();

    internal void CloseForScopeChange()
    {
        _closed = true;
        if (_destination is TeacherAccountsWindow teacher) teacher.CloseForScopeChange();
        if (_destination is TeacherAllocationWindow allocation) allocation.CloseForScopeChange();
        Close();
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (_busy || _closed) return;
        _busy = true;
        Render();
        try { await work(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error) when (!_closed)
        {
            ProfileNotice.Text = error.Message;
            HandleAccessFailure(error);
            if (error.Code is "ORGANIZATION_VERSION_CONFLICT" or "SCHOOL_PROFILE_IMPACT_CHANGED")
            {
                var prior = _profile;
                var keep = Draft();
                _reviewedDraft = null;
                try
                {
                    await LoadAsync(keep);
                    if (!_closed && _profile is { } current && prior is not null)
                        ProfileNotice.Text = $"{error.Message} 当前学校名称 {current.Name}，教师方式 {ModeLabel(current.TeacherAuthMode)}，备用 OAuth {(current.AllowOAuthTeacherLogin ? "开启" : "关闭")}。";
                }
                catch (ConsoleApiException refreshError) when (!_closed)
                {
                    HandleAccessFailure(refreshError);
                    ProfileNotice.Text = refreshError.Message;
                }
            }
        }
        finally { _busy = false; Render(); }
    }

    private void HandleAccessFailure(ConsoleApiException error)
    {
        if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        if (error.StatusCode != 403) return;
        _profile = null;
        _preview = null;
        _reviewedDraft = null;
        _binding = true;
        ProfileName.Clear();
        ProfilePassword.Clear();
        _binding = false;
        PushImpact();
    }

    private static string ModeLabel(string mode) => mode switch {"SHARED_PASSWORD" => "学校通用口令", "OAUTH_EMAIL" => "OAuth 邮箱", _ => "个人 PIN"};
}
