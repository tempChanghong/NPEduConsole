using System.ComponentModel;
using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed class WebsitePolicyTimingRow : INotifyPropertyChanged
{
    private bool _isAllDay;
    private bool _isLesson;
    private bool _isBreak;
    private bool _isAfterSchool;
    private bool _canEdit = true;
    private readonly Action<string, string?> _changed;

    public WebsitePolicyTimingRow(int index, string host, string? period, bool canEdit, Action<string, string?> changed)
    {
        Host = host;
        AllDayAutomationId = $"TimingAlways_{index}";
        AllDayAutomationName = $"{host} 全天";
        LessonAutomationId = $"TimingLesson_{index}";
        LessonAutomationName = $"{host} 上课";
        BreakAutomationId = $"TimingBreak_{index}";
        BreakAutomationName = $"{host} 课间";
        AfterSchoolAutomationId = $"TimingAfterSchool_{index}";
        AfterSchoolAutomationName = $"{host} 放学后";
        (_isAllDay, _isLesson, _isBreak, _isAfterSchool) = WebsitePolicyTiming.DecodeSelection(period);
        _canEdit = canEdit;
        _changed = changed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Host { get; }
    public string AllDayAutomationId { get; }
    public string AllDayAutomationName { get; }
    public string LessonAutomationId { get; }
    public string LessonAutomationName { get; }
    public string BreakAutomationId { get; }
    public string BreakAutomationName { get; }
    public string AfterSchoolAutomationId { get; }
    public string AfterSchoolAutomationName { get; }

    public string? CurrentPeriod => WebsitePolicyTiming.EncodeSelection(_isAllDay, _isLesson, _isBreak, _isAfterSchool);
    public bool IsIncomplete => CurrentPeriod is null;

    public bool IsAllDay
    {
        get => _isAllDay;
        set
        {
            if (_isAllDay == value) return;
            _isAllDay = value;
            if (value)
            {
                SetField(ref _isLesson, false, nameof(IsLesson));
                SetField(ref _isBreak, false, nameof(IsBreak));
                SetField(ref _isAfterSchool, false, nameof(IsAfterSchool));
            }
            NotifySelectionChanged(nameof(IsAllDay));
        }
    }

    public bool IsLesson
    {
        get => _isLesson;
        set => SetTimedFlag(ref _isLesson, value, nameof(IsLesson));
    }

    public bool IsBreak
    {
        get => _isBreak;
        set => SetTimedFlag(ref _isBreak, value, nameof(IsBreak));
    }

    public bool IsAfterSchool
    {
        get => _isAfterSchool;
        set => SetTimedFlag(ref _isAfterSchool, value, nameof(IsAfterSchool));
    }

    public bool CanEdit
    {
        get => _canEdit;
        set => SetField(ref _canEdit, value, nameof(CanEdit));
    }

    private void SetTimedFlag(ref bool field, bool value, string propertyName)
    {
        if (field == value) return;
        field = value;
        if (value) SetField(ref _isAllDay, false, nameof(IsAllDay));
        NotifySelectionChanged(propertyName);
    }

    private void SetField(ref bool field, bool value, string propertyName)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new(propertyName));
    }

    private void NotifySelectionChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new(propertyName));
        PropertyChanged?.Invoke(this, new(nameof(IsIncomplete)));
        _changed(Host, CurrentPeriod);
    }
}

public static class WebsitePolicyTiming
{
    public const string PeriodAlways = "ALWAYS";
    public const string PeriodLesson = "LESSON";
    public const string PeriodBreak = "BREAK";
    public const string PeriodAfterSchool = "AFTER_SCHOOL";
    public const string BehaviorBlock = "BLOCK";
    public const string BehaviorAllow = "ALLOW";

    public static string? EncodeSelection(bool isAllDay, bool isLesson, bool isBreak, bool isAfterSchool)
    {
        if (isAllDay) return PeriodAlways;
        var parts = new List<string>(3);
        if (isLesson) parts.Add(PeriodLesson);
        if (isBreak) parts.Add(PeriodBreak);
        if (isAfterSchool) parts.Add(PeriodAfterSchool);
        return parts.Count == 0 ? null : string.Join('|', parts);
    }

    public static (bool IsAllDay, bool IsLesson, bool IsBreak, bool IsAfterSchool) DecodeSelection(string? period)
    {
        var normalized = TryNormalizePeriod(period);
        if (normalized is null) return (false, false, false, false);
        if (normalized == PeriodAlways) return (true, false, false, false);
        bool lesson = false, @break = false, afterSchool = false;
        foreach (var token in normalized.Split('|'))
        {
            if (token == PeriodLesson) lesson = true;
            else if (token == PeriodBreak) @break = true;
            else if (token == PeriodAfterSchool) afterSchool = true;
        }
        return (false, lesson, @break, afterSchool);
    }

    public static string? TryNormalizePeriod(string? period)
    {
        if (string.IsNullOrWhiteSpace(period)) return null;
        if (string.Equals(period, PeriodAlways, StringComparison.Ordinal)) return PeriodAlways;
        bool lesson = false, @break = false, afterSchool = false;
        foreach (var token in period.Split('|'))
        {
            switch (token)
            {
                case PeriodLesson: lesson = true; break;
                case PeriodBreak: @break = true; break;
                case PeriodAfterSchool: afterSchool = true; break;
                default: return null;
            }
        }
        return EncodeSelection(false, lesson, @break, afterSchool);
    }

    public static string PeriodLabel(string? period)
    {
        var (isAllDay, isLesson, isBreak, isAfterSchool) = DecodeSelection(period);
        if (isAllDay) return "全天";
        var labels = new List<string>(3);
        if (isLesson) labels.Add("上课");
        if (isBreak) labels.Add("课间");
        if (isAfterSchool) labels.Add("放学后");
        return labels.Count == 0 ? "未选时段" : string.Join('+', labels);
    }

    public static string NormalizeUnavailable(string? behavior) =>
        string.Equals(behavior, BehaviorAllow, StringComparison.Ordinal) ? BehaviorAllow : BehaviorBlock;

    public static string? SchedulePhaseLabel(string? phase) => phase switch
    {
        "LESSON" => "课表：上课",
        "BREAK" => "课表：课间",
        "AFTER_SCHOOL" => "课表：放学后",
        "NO_ACTIVE_PERIOD" => "课表：当前无活动时段",
        "UNAVAILABLE" => "课表：不可核对",
        _ => null
    };

    public static bool HasTimedContent(WebsitePolicyDraft? draft) =>
        draft is not null &&
        (draft.Rules.Any(rule => !string.Equals(TryNormalizePeriod(rule.Period), PeriodAlways, StringComparison.Ordinal)) ||
            string.Equals(draft.UnavailableBehavior, BehaviorAllow, StringComparison.Ordinal));

    public static WebsitePolicyDraft? SavedDraftOf(WebsitePolicySnapshot scope)
    {
        if (scope.BeforeDraft is { } beforeDraft)
            return new(
                beforeDraft.Rules.Select(rule => new WebsitePolicyRule(rule.Host, TryNormalizePeriod(rule.Period) ?? PeriodAlways)).ToArray(),
                NormalizeUnavailable(beforeDraft.UnavailableBehavior));
        if (scope.BeforeHosts is { } beforeHosts)
            return new(beforeHosts.Select(host => new WebsitePolicyRule(host, PeriodAlways)).ToArray(), BehaviorBlock);
        return null;
    }

    public static bool TryBuildDraft(
        bool inherit,
        IReadOnlyList<string> hosts,
        IReadOnlyDictionary<string, string?> periods,
        string unavailableBehavior,
        out WebsitePolicyDraft? draft)
    {
        if (inherit)
        {
            draft = null;
            return true;
        }
        var rules = new WebsitePolicyRule[hosts.Count];
        bool hasTimed = false;
        for (int i = 0; i < hosts.Count; i++)
        {
            string host = hosts[i];
            string? raw = periods.TryGetValue(host, out var stored) ? stored : PeriodAlways;
            string? normalized = TryNormalizePeriod(raw);
            if (normalized is null)
            {
                draft = null;
                return false;
            }
            if (normalized != PeriodAlways) hasTimed = true;
            rules[i] = new WebsitePolicyRule(host, normalized);
        }
        draft = new(rules, hasTimed ? NormalizeUnavailable(unavailableBehavior) : BehaviorBlock);
        return true;
    }

    public static bool DraftEquals(WebsitePolicyDraft? left, WebsitePolicyDraft? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (!string.Equals(NormalizeUnavailable(left.UnavailableBehavior), NormalizeUnavailable(right.UnavailableBehavior), StringComparison.Ordinal))
            return false;
        if (left.Rules.Length != right.Rules.Length) return false;
        for (int i = 0; i < left.Rules.Length; i++)
        {
            if (!string.Equals(left.Rules[i].Host, right.Rules[i].Host, StringComparison.Ordinal) ||
                !string.Equals(TryNormalizePeriod(left.Rules[i].Period), TryNormalizePeriod(right.Rules[i].Period), StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    public static bool ShouldBlockDowngrade(WebsitePolicySnapshot scope, WebsitePolicyDraft? currentDraft) =>
        !scope.TimingSupported && (HasTimedContent(scope.BeforeDraft) || HasTimedContent(currentDraft));

    public static bool TryResolvePublishDraft(
        WebsitePolicySnapshot preview,
        WebsitePolicyDraft? localDraft,
        out WebsitePolicyDraft? publishDraft)
    {
        if (!preview.TimingSupported)
        {
            publishDraft = null;
            return !HasTimedContent(localDraft);
        }
        if (preview.AfterDraft is { } afterDraft)
        {
            publishDraft = afterDraft;
            return true;
        }
        if (preview.AfterHosts is null)
        {
            publishDraft = null;
            return true;
        }
        if (HasTimedContent(localDraft))
        {
            publishDraft = null;
            return false;
        }
        publishDraft = new(preview.AfterHosts.Select(host => new WebsitePolicyRule(host, PeriodAlways)).ToArray(), BehaviorBlock);
        return true;
    }

    public static string FormatEffective(WebsitePolicyEffective policy)
    {
        string source = policy.Source switch { "GRADE" => "年级默认", "CLASS" => "本班自定", _ => "未设置" };
        if (policy.Rules is { Length: > 0 } rules)
        {
            bool hasTimed = rules.Any(rule => !string.Equals(TryNormalizePeriod(rule.Period), PeriodAlways, StringComparison.Ordinal));
            if (!hasTimed)
                return $"{source} · {string.Join("、", rules.Select(rule => rule.Host))}";
            string ruleText = string.Join("、", rules.Select(rule => $"{rule.Host}（{PeriodLabel(rule.Period)}）"));
            string fallback = string.Equals(policy.UnavailableBehavior, BehaviorAllow, StringComparison.Ordinal)
                ? "课表不可核对时暂不限制"
                : "课表不可核对时限制";
            return $"{source} · {ruleText} · {fallback}";
        }
        return $"{source} · {(policy.BlockedHosts.Length == 0 ? "无下发域名" : string.Join("、", policy.BlockedHosts))}";
    }

    public static string FormatScreenStatus(WebsitePolicyScreen screen)
    {
        string status = screen.Status switch
        {
            "RECEIVED_NOT_APPLIED" => "设备已收到 · Edge 生效未确认",
            "WRITTEN_PENDING" => "已写入 Windows · 等待 Edge 前台确认",
            "CLEARED_PENDING" => "本产品名下的拦截已清除 · Edge 放行未确认",
            "EDGE_CONFIRMED" => "Edge 前台已观察到名单内各网站被拦截",
            "CONFLICT" => "本机已有规则或清单冲突 · 未确认生效",
            "FAILED" => "写入或核对失败 · 请检查本机后台和 Windows 策略",
            "TIMING_UPDATE_REQUIRED" => "教室端需更新 · 暂不支持分时规则",
            "NOT_PAIRED" => "未配对原生设备",
            _ => "等待当前绑定的设备报告"
        };
        if (SchedulePhaseLabel(screen.SchedulePhase) is { } phase)
            status += screen.PhaseHeld == true ? $" · {phase}（短时沿用）" : $" · {phase}";
        if (screen.TimingSupported == false && screen.Status != "TIMING_UPDATE_REQUIRED") status += " · 教室端未支持分时";
        else if (screen.TimingSupported == true && screen.SchedulePhase is null) status += " · 支持分时";
        if (screen.ReceivedAt is { } receivedAt) status += $" · {receivedAt.ToLocalTime():MM-dd HH:mm:ss}";
        return status;
    }
}

public sealed partial class WebsitePolicyWindow
{
    private readonly Dictionary<string, string?> _hostPeriods = new(StringComparer.OrdinalIgnoreCase);
    private string _unavailableBehavior = WebsitePolicyTiming.BehaviorBlock;
    private WebsitePolicyTimingRow[] _timingRows = [];
    private string[] _timingRowHosts = [];

    private void ResetTimingState(WebsitePolicySnapshot scope)
    {
        var savedDraft = WebsitePolicyTiming.SavedDraftOf(scope);
        _downgradeBlocked = WebsitePolicyTiming.ShouldBlockDowngrade(scope, savedDraft);
        _hostPeriods.Clear();
        if (savedDraft is not null)
        {
            foreach (var rule in savedDraft.Rules)
                _hostPeriods[rule.Host] = WebsitePolicyTiming.TryNormalizePeriod(rule.Period) ?? WebsitePolicyTiming.PeriodAlways;
            _unavailableBehavior = WebsitePolicyTiming.NormalizeUnavailable(savedDraft.UnavailableBehavior);
        }
        else
        {
            _unavailableBehavior = WebsitePolicyTiming.BehaviorBlock;
        }
        _updating = true;
        Inherit.IsChecked = savedDraft is null;
        Hosts.Text = savedDraft is null ? "" : string.Join(Environment.NewLine, savedDraft.Rules.Select(rule => rule.Host));
        UnavailableBehavior.SelectedIndex = _unavailableBehavior == WebsitePolicyTiming.BehaviorAllow ? 1 : 0;
        _updating = false;
        RebuildTimingRows(savedDraft?.Rules.Select(rule => rule.Host).ToArray() ?? [], editable: !_downgradeBlocked);
    }

    private bool TryBuildCurrentDraft(out WebsitePolicyDraft? draft) =>
        WebsitePolicyTiming.TryBuildDraft(Inherit.IsChecked == true, DraftHostLines(), _hostPeriods, _unavailableBehavior, out draft);

    private bool HasIncompleteTimingSelection() =>
        _scope is { TimingSupported: true } && Inherit.IsChecked != true && _timingRows.Any(row => row.IsIncomplete);

    private bool HasAnyNonAllDayRow() =>
        _scope is not null && Inherit.IsChecked != true && _timingRows.Any(row => !row.IsAllDay);

    private void RecomputeDirtyState()
    {
        if (_scope is null)
        {
            _dirty = false;
            return;
        }
        if (_scope.TimingSupported || _downgradeBlocked)
        {
            _dirty = !TryBuildCurrentDraft(out var currentDraft) ||
                !WebsitePolicyTiming.DraftEquals(currentDraft, WebsitePolicyTiming.SavedDraftOf(_scope));
            return;
        }
        var draft = DraftHosts();
        var saved = _scope.BeforeHosts;
        _dirty = draft is null ? saved is not null : saved is null || !draft.SequenceEqual(saved);
    }

    private void SyncTimingRowsFromHosts()
    {
        if (_scope is null) return;
        var hosts = Inherit.IsChecked == true ? [] : DraftHostLines();
        if (hosts.SequenceEqual(_timingRowHosts, StringComparer.Ordinal)) return;
        bool editable = !_reading && !_posting && _pending.Value is null && !_downgradeBlocked;
        RebuildTimingRows(hosts, editable);
    }

    private void RebuildTimingRows(string[] hosts, bool editable)
    {
        _timingRowHosts = hosts;
        _timingRows = hosts.Select((host, index) => new WebsitePolicyTimingRow(
            index,
            host,
            _hostPeriods.TryGetValue(host, out var period) ? period : WebsitePolicyTiming.PeriodAlways,
            editable,
            TimingPeriodChanged)).ToArray();
        TimingRows.ItemsSource = _timingRows;
    }

    private void TimingPeriodChanged(string host, string? period)
    {
        if (_updating || _scope is null || _pending.Value is not null || _downgradeBlocked) return;
        _hostPeriods[host] = period;
        _preview = null;
        RecomputeDirtyState();
        RenderRows();
        UpdateActions();
    }

    private void UnavailableBehaviorChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_updating || _scope is null || _pending.Value is not null || _downgradeBlocked || UnavailableBehavior is null) return;
        _unavailableBehavior = UnavailableBehavior.SelectedIndex == 1
            ? WebsitePolicyTiming.BehaviorAllow
            : WebsitePolicyTiming.BehaviorBlock;
        _preview = null;
        RecomputeDirtyState();
        RenderRows();
        UpdateActions();
    }

    private void UpdateTimingControls(bool editable)
    {
        if (TimingSection is null || UnavailablePanel is null || UnavailableBehavior is null || TimingNotice is null) return;
        bool hasScope = _scope is not null;
        bool inherit = Inherit.IsChecked == true;
        bool timingSupported = _scope?.TimingSupported == true;
        bool showTimingSection = hasScope && !inherit && _timingRows.Length > 0 && (timingSupported || _downgradeBlocked);
        TimingSection.IsVisible = showTimingSection;
        TimingHeading.IsVisible = showTimingSection && !_compact;
        foreach (var row in _timingRows) row.CanEdit = editable && timingSupported;

        bool hasTimed = showTimingSection && HasAnyNonAllDayRow();
        UnavailablePanel.IsVisible = hasTimed;
        UnavailableBehavior.IsEnabled = editable && timingSupported;
        if (hasTimed)
        {
            int expectedIndex = _unavailableBehavior == WebsitePolicyTiming.BehaviorAllow ? 1 : 0;
            if (UnavailableBehavior.SelectedIndex != expectedIndex)
            {
                _updating = true;
                UnavailableBehavior.SelectedIndex = expectedIndex;
                _updating = false;
            }
        }

        if (_downgradeBlocked)
        {
            TimingNotice.Text = "当前服务端不支持分时网站规则，已保留当前分时草稿且禁止降格或分时写入。";
            TimingNotice.IsVisible = true;
        }
        else if (HasIncompleteTimingSelection())
        {
            TimingNotice.Text = "分时域名请至少勾选「上课」「课间」「放学后」之一，或改回「全天」。";
            TimingNotice.IsVisible = true;
        }
        else if (hasScope && !timingSupported && !inherit)
        {
            TimingNotice.Text = "当前服务端仅支持全天网站规则。";
            TimingNotice.IsVisible = !_compact;
        }
        else
        {
            TimingNotice.IsVisible = false;
        }
    }
}
