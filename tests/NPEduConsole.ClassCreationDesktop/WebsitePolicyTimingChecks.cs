using NPEduConsole.App;
using NPEduConsole.Core;

internal static class WebsitePolicyTimingChecks
{
    public static void RunAll()
    {
        CheckCompositeEncodeDecodeReversibility();
        CheckEmptySelectionBlocksDraftAndAllowsAlways();
        CheckLegacyDowngradeProtection();
        CheckEffectiveBeforeAfterFormatting();
        CheckScreenSchedulePhaseAndTimingFormatting();
        CheckPendingWritePreservesCapabilityAndData();
    }

    private static void CheckCompositeEncodeDecodeReversibility()
    {
        string[] validPeriods =
        [
            "ALWAYS",
            "LESSON",
            "BREAK",
            "AFTER_SCHOOL",
            "LESSON|BREAK",
            "LESSON|AFTER_SCHOOL",
            "BREAK|AFTER_SCHOOL",
            "LESSON|BREAK|AFTER_SCHOOL"
        ];
        foreach (var expected in validPeriods)
        {
            var decoded = WebsitePolicyTiming.DecodeSelection(expected);
            var encoded = WebsitePolicyTiming.EncodeSelection(decoded.IsAllDay, decoded.IsLesson, decoded.IsBreak, decoded.IsAfterSchool);
            Require(encoded == expected, $"Round-trip mismatch for {expected}: got {encoded}.");
        }
        Require(WebsitePolicyTiming.TryNormalizePeriod("AFTER_SCHOOL|LESSON") == "LESSON|AFTER_SCHOOL",
            "Canonical order must be LESSON|BREAK|AFTER_SCHOOL.");
        Require(WebsitePolicyTiming.TryNormalizePeriod("ALWAYS|LESSON") is null,
            "ALWAYS must never mix with timed periods.");
        Require(WebsitePolicyTiming.TryNormalizePeriod("") is null,
            "Empty period must be rejected.");
    }

    private static void CheckEmptySelectionBlocksDraftAndAllowsAlways()
    {
        var periods = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Require(WebsitePolicyTiming.TryBuildDraft(false, ["douyin.com", "bilibili.com"], periods, WebsitePolicyTiming.BehaviorAllow, out var allDay),
            "New hosts default to ALWAYS.");
        Require(allDay is not null && allDay.Rules.All(rule => rule.Period == "ALWAYS") && allDay.UnavailableBehavior == "BLOCK",
            "All-day draft defaults to ALWAYS and BLOCK.");

        periods["douyin.com"] = null;
        Require(!WebsitePolicyTiming.TryBuildDraft(false, ["douyin.com", "bilibili.com"], periods, WebsitePolicyTiming.BehaviorBlock, out _),
            "Empty period selection must block preview/build.");

        periods["douyin.com"] = "LESSON|AFTER_SCHOOL";
        Require(WebsitePolicyTiming.TryBuildDraft(false, ["douyin.com", "bilibili.com"], periods, WebsitePolicyTiming.BehaviorBlock, out var comboBlock) &&
            comboBlock is not null, "LESSON|AFTER_SCHOOL draft should build.");
        Require(comboBlock!.Rules[0] == new WebsitePolicyRule("douyin.com", "LESSON|AFTER_SCHOOL"), "Composite period preserved.");
        Require(comboBlock.Rules[1] == new WebsitePolicyRule("bilibili.com", "ALWAYS"), "Untouched host stays ALWAYS.");
        Require(comboBlock.UnavailableBehavior == "BLOCK", "Default unavailable behavior is BLOCK.");

        Require(WebsitePolicyTiming.TryBuildDraft(false, ["douyin.com", "bilibili.com"], periods, WebsitePolicyTiming.BehaviorAllow, out var comboAllow) &&
            comboAllow!.UnavailableBehavior == "ALLOW" && !WebsitePolicyTiming.DraftEquals(comboBlock, comboAllow),
            "ALLOW unavailable behavior is distinct and preserved.");

        Require(WebsitePolicyTiming.TryBuildDraft(true, ["douyin.com"], periods, WebsitePolicyTiming.BehaviorAllow, out var inherit) &&
            inherit is null, "Inherit draft must be null.");
    }

    private static void CheckLegacyDowngradeProtection()
    {
        var effective = new WebsitePolicyEffective(1, "s1", "t1", "c1", "GRADE", ["douyin.com"], "p1");
        var room = new WebsitePolicyRoom("c1", "高二（1）班", "G2-1", effective, effective);

        var legacyAllDay = new WebsitePolicySnapshot("s1", "t1", "GRADE", "g2", "高二",
            ["douyin.com"], ["douyin.com"], [room], [], CanApply: true, Version: new string('a', 64),
            TimingSupported: false, BeforeDraft: null, AfterDraft: null);
        var allDayDraft = new WebsitePolicyDraft([new("douyin.com", "ALWAYS")], "BLOCK");
        Require(!WebsitePolicyTiming.ShouldBlockDowngrade(legacyAllDay, allDayDraft), "Legacy all-day scope stays editable.");
        Require(WebsitePolicyTiming.TryResolvePublishDraft(legacyAllDay, allDayDraft, out var legacyResolved) && legacyResolved is null,
            "Legacy all-day preview resolves without timed draft.");

        var comboDraft = new WebsitePolicyDraft([new("douyin.com", "LESSON|AFTER_SCHOOL")], "ALLOW");
        Require(WebsitePolicyTiming.ShouldBlockDowngrade(legacyAllDay, comboDraft),
            "Timed composite draft on TimingSupported=false must block downgrade/write.");
        Require(!WebsitePolicyTiming.TryResolvePublishDraft(legacyAllDay, comboDraft, out _),
            "Legacy preview must refuse publishing a timed composite draft.");

        var legacyWithLoadedTimed = legacyAllDay with { BeforeDraft = comboDraft };
        Require(WebsitePolicyTiming.ShouldBlockDowngrade(legacyWithLoadedTimed, WebsitePolicyTiming.SavedDraftOf(legacyWithLoadedTimed)),
            "Loaded timed composite draft on TimingSupported=false must never be downgraded.");

        var timedPreviewMissingAfterDraft = legacyAllDay with { TimingSupported = true, AfterDraft = null };
        Require(!WebsitePolicyTiming.TryResolvePublishDraft(timedPreviewMissingAfterDraft, comboDraft, out _),
            "Timed preview without AfterDraft must refuse silent downgrade.");

        var timedPreviewValid = legacyAllDay with { TimingSupported = true, AfterDraft = comboDraft };
        Require(WebsitePolicyTiming.TryResolvePublishDraft(timedPreviewValid, comboDraft, out var resolved) &&
            WebsitePolicyTiming.DraftEquals(resolved, comboDraft),
            "Timed preview with AfterDraft resolves exact composite draft.");
    }

    private static void CheckEffectiveBeforeAfterFormatting()
    {
        var allDayLegacy = new WebsitePolicyEffective(1, "s1", "t1", "c1", "GRADE", ["douyin.com"], "p1");
        Require(WebsitePolicyTiming.FormatEffective(allDayLegacy) == "年级默认 · douyin.com", "Legacy all-day caption unchanged.");

        var comboBlock = new WebsitePolicyEffective(1, "s1", "t1", "c1", "GRADE", ["douyin.com", "bilibili.com"], "p2",
            [new("douyin.com", "LESSON|AFTER_SCHOOL"), new("bilibili.com", "ALWAYS")], "BLOCK");
        Require(WebsitePolicyTiming.FormatEffective(comboBlock) == "年级默认 · douyin.com（上课+放学后）、bilibili.com（全天） · 课表不可核对时限制",
            "Composite BLOCK caption shows 上课+放学后 and fallback.");

        var comboAllow = new WebsitePolicyEffective(1, "s1", "t1", "c1", "CLASS", ["douyin.com"], "p3",
            [new("douyin.com", "LESSON|BREAK|AFTER_SCHOOL")], "ALLOW");
        Require(WebsitePolicyTiming.FormatEffective(comboAllow) == "本班自定 · douyin.com（上课+课间+放学后） · 课表不可核对时暂不限制",
            "Three-period ALLOW caption shows full combination and ALLOW fallback.");

        var emptyOverride = new WebsitePolicyEffective(1, "s1", "t1", "c1", "CLASS", [], "p4", [], "BLOCK");
        Require(WebsitePolicyTiming.FormatEffective(emptyOverride) == "本班自定 · 无下发域名", "Empty class override caption unchanged.");
    }

    private static void CheckScreenSchedulePhaseAndTimingFormatting()
    {
        var legacyScreen = new WebsitePolicyScreen("c1", "b1", "前屏", 1, "d1", "教室前屏", "RECEIVED_NOT_APPLIED", null);
        Require(WebsitePolicyTiming.FormatScreenStatus(legacyScreen) == "设备已收到 · Edge 生效未确认",
            "Legacy screen without timing fields keeps exact status.");

        var timedScreen = new WebsitePolicyScreen("c1", "b1", "前屏", 1, "d1", "教室前屏", "WRITTEN_PENDING", null,
            SchedulePhase: "LESSON", TimingSupported: true);
        Require(WebsitePolicyTiming.FormatScreenStatus(timedScreen) == "已写入 Windows · 等待 Edge 前台确认 · 课表：上课",
            "Timed screen shows read-only schedule phase.");
        Require(WebsitePolicyTiming.FormatScreenStatus(timedScreen with { PhaseHeld = true }).Contains("课表：上课（短时沿用）"),
            "A short disconnect does not present the last phase as freshly confirmed.");
        Require(WebsitePolicyTiming.FormatScreenStatus(timedScreen with { SchedulePhase = "NO_ACTIVE_PERIOD" }).Contains("当前无活动时段"),
            "An actual schedule gap remains distinct from an unavailable schedule.");

        var oldHostScreen = new WebsitePolicyScreen("c1", "b1", "后屏", 1, "d2", "旧版后屏", "TIMING_UPDATE_REQUIRED", null,
            SchedulePhase: null, TimingSupported: false);
        Require(WebsitePolicyTiming.FormatScreenStatus(oldHostScreen) == "教室端需更新 · 暂不支持分时规则",
            "TIMING_UPDATE_REQUIRED status is rendered clearly.");
    }

    private static void CheckPendingWritePreservesCapabilityAndData()
    {
        var reqId = Guid.NewGuid();
        var target = new ClassroomActionTarget("t1", "GRADE", "g2");
        var draft = new WebsitePolicyDraft([new("douyin.com", "LESSON|AFTER_SCHOOL")], "ALLOW");
        var timedWrite = new WebsitePolicyView.Write(reqId, target, ["douyin.com"], "v1", UseTimedApi: true, Draft: draft);
        var pending = new WebsitePolicyView.Pending { Value = timedWrite };
        Require(pending.Value!.RequestId == reqId && pending.Value.UseTimedApi &&
            WebsitePolicyTiming.DraftEquals(pending.Value.Draft, draft),
            "Pending.Write retains RequestId, timed capability, and composite LESSON|AFTER_SCHOOL draft.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
