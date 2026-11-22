using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class StaffPolicyWindow : Window
{
    private sealed record Submission(StaffPolicySnapshot Baseline, StaffLeadershipPolicies Draft, StaffPolicyPreview Preview, Guid Id);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, Submission>> PendingByClient = new();
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["gradeLeaderMustBeHomeroom"] = "年级组长需兼任本年级班主任",
        ["gradeLeaderMustTeach"] = "年级组长需在本年级任课",
        ["homeroomMustTeach"] = "班主任需在本年级任课"
    };

    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private StaffPolicySnapshot _baseline = null!;
    private StaffPolicySnapshot? _observed;
    private StaffPolicyPreview? _preview, _lastImpact;
    private bool _closed, _busy, _binding, _allowClose, _denied, _retry, _reviewFirst;

    public StaffPolicyWindow() => InitializeComponent();

    internal StaffPolicyWindow(ConsoleClient client, School school, StaffPolicySnapshot snapshot) : this()
    {
        _client = client;
        _school = school;
        Bind(snapshot);
        SizeChanged += (_, _) => Arrange();
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            Arrange();
            if (Pending is { } pending)
            {
                Bind(pending.Baseline, pending.Draft);
                await RunAsync(() => VerifyAsync(pending));
            }
        };
        Closing += (_, args) =>
        {
            if (_allowClose || _denied) return;
            args.Cancel = _busy || Pending is null && Dirty;
            if (args.Cancel && !_busy) Status("仍有未保存的规则草稿。可继续核对或放弃修改。");
        };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
        Render();
    }

    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal bool PolicyChanged { get; private set; }

    private Dictionary<string, Submission> PendingOperations => PendingByClient.GetOrCreateValue(_client);
    private Submission? Pending => PendingOperations.GetValueOrDefault(_school.Id);
    private StaffLeadershipPolicies Draft => new(PolicyLeaderHome.IsChecked == true, PolicyLeaderTeach.IsChecked == true, PolicyHomeTeach.IsChecked == true);
    private bool Dirty => Draft != _baseline.Policies;
    private static string Flag(bool enabled) => enabled ? "启用" : "关闭";

    private void Bind(StaffPolicySnapshot current, StaffLeadershipPolicies? draft = null)
    {
        _baseline = current;
        _observed = null;
        _preview = null;
        _binding = true;
        var values = draft ?? current.Policies;
        PolicyLeaderHome.IsChecked = values.GradeLeaderMustBeHomeroom;
        PolicyLeaderTeach.IsChecked = values.GradeLeaderMustTeach;
        PolicyHomeTeach.IsChecked = values.HomeroomMustTeach;
        _binding = false;
        PolicyScope.Text = $"{_school.Name} · 全校生效\n核对范围　{current.Term?.ToString() ?? "未选学期"}";
        ShowInitialImpact();
    }

    private void ShowInitialImpact() => ShowRows(_baseline.Term is null ? "此范围没有可核对的学期。全校规则仍可保存。"
        : "预览会列出本学期因所改规则新增或消失的提醒。", []);

    private void Arrange()
    {
        bool narrow = ClientSize.Width < 900;
        Grid.SetRow(PolicyChoices, narrow && _reviewFirst ? 2 : 1);
        Grid.SetRow(PolicyReview, narrow && !_reviewFirst ? 2 : 1);
        Grid.SetColumn(PolicyReview, narrow ? 0 : 1);
        PolicyContent.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 3, GridUnitType.Star);
        PolicyContent.ColumnSpacing = narrow ? 0 : 10;
    }

    private void RuleChanged(object? sender, RoutedEventArgs args)
    {
        if (_client is null || _binding || _busy || Pending is not null) return;
        _preview = _lastImpact = null;
        if (_observed is { } observed) ShowComparison(observed);
        else { ShowInitialImpact(); Status(""); }
        Render();
    }

    private void Render()
    {
        if (_closed || _client is null) return;
        bool pending = Pending is not null;
        PolicyChoices.IsEnabled = !_busy && !_denied && !pending;
        PolicyChoices.IsVisible = PolicyReview.IsVisible = !_denied;
        PolicyPrimary.Content = pending ? (_retry ? "按原编号重试" : "核对回执") :
            _observed is not null ? "按当前值重新预览" : _preview is not null ? "确认保存全校规则" : "预览规则影响";
        PolicyPrimary.IsEnabled = !_busy && !_denied && (pending || _observed is not null || Dirty);
        PolicyRecheck.IsVisible = pending && _retry;
        PolicyRecheck.IsEnabled = !_busy && !_denied;
        PolicyBack.IsEnabled = !_busy;
        PolicyDiscard.IsVisible = !_denied && (pending ? _observed is not null : Dirty);
        PolicyDiscard.IsEnabled = !_busy;
        PolicyDiscard.Content = pending ? "结束核对" : "放弃修改";
    }

    private async void PrimaryClicked(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (Pending is { } waiting)
        {
            if (_retry) await SendAsync(waiting);
            else await VerifyAsync(waiting);
            return;
        }
        if (_observed is { } observed)
        {
            var desired = Rebase(observed.Policies);
            Bind(observed, desired);
            if (!Dirty)
            {
                Status("服务器当前规则已与草稿一致，没有需要写入的修改。");
                return;
            }
        }
        if (_preview is { } preview)
        {
            var request = new Submission(_baseline, Draft, preview, Guid.NewGuid());
            PendingOperations[_school.Id] = request;
            await SendAsync(request);
        }
        else await PreviewAsync();
    });

    private StaffLeadershipPolicies Rebase(StaffLeadershipPolicies now)
    {
        var draft = Draft;
        var old = _baseline.Policies;
        return new(old.GradeLeaderMustBeHomeroom != draft.GradeLeaderMustBeHomeroom ? draft.GradeLeaderMustBeHomeroom : now.GradeLeaderMustBeHomeroom,
            old.GradeLeaderMustTeach != draft.GradeLeaderMustTeach ? draft.GradeLeaderMustTeach : now.GradeLeaderMustTeach,
            old.HomeroomMustTeach != draft.HomeroomMustTeach ? draft.HomeroomMustTeach : now.HomeroomMustTeach);
    }

    private async Task PreviewAsync()
    {
        var preview = await _client.PreviewStaffPolicyAsync(_baseline, Draft, _lifetime.Token);
        if (_closed) return;
        if (preview.State == "STALE")
        {
            Compare(preview.Current, "规则或学期范围已变化，请核对学校当前值。");
            return;
        }
        _preview = preview;
        var rows = ImpactRows(preview).ToList();
        if (_lastImpact is { } last)
        {
            var now = rows.ToHashSet();
            rows.InsertRange(0, ImpactRows(last).Where(row => !now.Contains(row)).Select(row => "原预览中的影响已变化　" + row));
            _lastImpact = null;
        }
        ShowRows($"新增 {preview.Added.Count} 项提醒，消失 {preview.Removed.Count} 项。", rows, "影响预览");
        Status("核对影响后确认保存。");
        Focus(PolicyImpactHeading);
    }

    private async Task SendAsync(Submission request)
    {
        _retry = false;
        _observed = null;
        try
        {
            var result = await _client.SaveStaffPolicyAsync(request.Baseline, request.Draft, request.Preview, request.Id, _lifetime.Token);
            if (_closed) return;
            PendingOperations.Remove(_school.Id);
            if (result.Receipt is { } receipt) Finish(receipt, result.Current);
            else
            {
                _lastImpact = request.Preview;
                Compare(result.Current, "学校规则或受影响的岗位已变化，本次整次零写入。草稿已保留。");
                if (result.CurrentImpact is { } now)
                {
                    var oldImpact = ImpactRows(request.Preview).ToHashSet();
                    var newImpact = now.Added.Select(row => Format("新增", row)).Concat(now.Removed.Select(row => Format("消失", row))).ToHashSet();
                    ShowRows("受影响对象已变化，请核对后重新预览。",
                        oldImpact.Except(newImpact).Select(row => "原预览　" + row)
                            .Concat(newImpact.Except(oldImpact).Select(row => "当前影响　" + row)), "影响已有变化");
                }
            }
        }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 || error.StatusCode >= 500)
        {
            if (!_closed) await VerifyAsync(request);
        }
        catch (ConsoleApiException)
        {
            if (!_closed) PendingOperations.Remove(_school.Id);
            throw;
        }
    }

    private async Task VerifyAsync(Submission request)
    {
        var receipt = await _client.GetStaffPolicyReceiptAsync(request.Baseline, request.Draft, request.Id, _lifetime.Token);
        if (_closed) return;
        var current = await _client.GetStaffPolicyAsync(_school.Id, request.Baseline.Term?.Id, _lifetime.Token);
        if (_closed) return;
        if (receipt is not null)
        {
            PendingOperations.Remove(_school.Id);
            Finish(receipt, current);
            return;
        }
        _preview = null;
        _observed = current;
        var live = ConsoleClient.StaffPolicyValues(current.Policies);
        var original = ConsoleClient.StaffPolicyValues(request.Baseline.Policies);
        var desired = ConsoleClient.StaffPolicyValues(request.Draft);
        var fields = request.Preview.ChangedFields;
        bool same = fields.All(key => live[key] == desired[key]);
        _retry = fields.All(key => live[key] == original[key]) && current.ScopeVersion == request.Baseline.ScopeVersion;
        ShowComparison(current);
        Status(same ? "服务器当前值与本次提交一致，暂未找到本账号的保存回执。"
            : "暂未找到本账号的保存回执。已读取服务器当前规则。");
    }

    private void Finish(StaffPolicyReceipt receipt, StaffPolicySnapshot current)
    {
        PolicyChanged = true;
        Bind(current);
        var saved = ConsoleClient.StaffPolicyValues(receipt.Policies);
        var live = ConsoleClient.StaffPolicyValues(current.Policies);
        ShowRows("本人当时保存的值与服务器现在的值", receipt.ChangedFields.Select(key =>
            $"{Labels[key]}：本人当时 {Flag(saved[key])}，服务器现在 {Flag(live[key])}"), "保存结果");
        Status("已确认本账号的规则保存回执。");
        Focus(PolicyBack);
    }

    private void Compare(StaffPolicySnapshot current, string message)
    {
        _observed = current;
        _preview = null;
        ShowComparison(current);
        Status(message);
        Focus(PolicyImpactHeading);
    }

    private void ShowComparison(StaffPolicySnapshot current)
    {
        var old = ConsoleClient.StaffPolicyValues(_baseline.Policies);
        var draft = ConsoleClient.StaffPolicyValues(Draft);
        var live = ConsoleClient.StaffPolicyValues(current.Policies);
        ShowRows("对照原值、学校当前值和保留的草稿", Labels.Keys.Where(key => old[key] != draft[key] || old[key] != live[key])
            .Select(key => $"{Labels[key]}：原值 {Flag(old[key])}／学校当前 {Flag(live[key])}／草稿 {Flag(draft[key])}"), "核对当前规则");
    }

    private static IEnumerable<string> ImpactRows(StaffPolicyPreview preview) =>
        preview.Added.Select(row => Format("新增", row)).Concat(preview.Removed.Select(row => Format("消失", row)));

    private static string Format(string action, StaffReviewIssue row) =>
        $"{action}　{row.GradeName}{(row.ClassName is { } room ? " / " + room : "")}　{row.Message}" +
        (row.TeacherLabel is { } label ? $"（{label}）" : "");

    private void ShowRows(string summary, IEnumerable<string> rows, string? heading = null)
    {
        _reviewFirst = heading is not null;
        PolicyImpactHeading.Text = heading ?? "所选学期的提醒";
        PolicyImpactSummary.Text = summary;
        PolicyImpactRows.ItemsSource = rows.ToArray();
        Arrange();
    }

    private void Status(string message) => PolicyStatus.Text = message;
    private void Focus(Control control) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && control.IsEffectivelyVisible) { control.BringIntoView(); control.Focus(); }
    }, DispatcherPriority.Loaded);

    private async Task RunAsync(Func<Task> work)
    {
        if (_busy || _closed || _denied) return;
        _busy = true;
        Render();
        try { await work(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            if (error.StatusCode is 401 or 403)
            {
                _denied = true;
                PendingOperations.Remove(_school.Id);
                ShowRows("", []);
                PolicyScope.Text = _school.Name;
                if (error.StatusCode == 401) SessionExpired?.Invoke(error);
            }
            if (!_closed) Status(Pending is null ? error.Message : "尚未核对本次保存。 " + error.Message);
        }
        finally { _busy = false; Render(); }
    }

    private async void RecheckClicked(object? sender, RoutedEventArgs args)
    {
        if (Pending is { } pending) await RunAsync(() => VerifyAsync(pending));
    }

    private void DiscardClicked(object? sender, RoutedEventArgs args)
    {
        PendingOperations.Remove(_school.Id);
        Bind(_observed ?? _baseline);
        _lastImpact = null;
        Status("已结束本次核对。");
        Render();
    }

    private void BackClicked(object? sender, RoutedEventArgs args) => Close();
    internal void CloseForScopeChange() { _allowClose = true; Close(); }
}
