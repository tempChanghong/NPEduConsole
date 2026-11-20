using NPEduConsole.Core;

internal static class StudyEvidenceChecks
{
    internal static void Verify()
    {
        var version = new string('a', 64);
        var interval = new StudyReportWindow(new(2026, 11, 20, 19, 0, 0), new(2026, 11, 20, 20, 0, 0));
        var received = new StudyEvidenceReceipt(new(version, new("device"), new("school","term","class")),
            new(version, "Active", "APPLIED", "Daily", interval), true, true, 10, DateTimeOffset.UtcNow,
            new("Active", "Schedule", "WINDOW_ACTIVE", true, interval), true, version, 10, true,
            new("Visible"), true, version, 5, 4995, true);
        void Expect(StudyEvidenceReceipt sample, string part, bool present = true)
        {
            if (sample.Describe().Contains(part, StringComparison.Ordinal) != present)
                throw new InvalidDataException("Incorrect classroom evidence: " + part);
        }
        Expect(received, "定时采集已收到信号");
        Expect(received, "大屏已显示自习页");
        Expect(received with { ObservationAgeMs = 15000 }, "回执已过期");
        Expect(received with { Observation = received.Observation! with { Version = "old" } }, "旧配置回执");
        Expect(received with { ExecutionAgeMs = 15000 }, "定时采集已收到信号", false);
        Expect(received with { Execution = received.Execution! with { Owner = "Manual" } }, "定时采集已收到信号", false);
        Expect(received with { Observation = received.Observation! with { Phase = "Outside" } }, "定时采集已收到信号", false);
        Expect(received with { Execution = received.Execution! with { Window = interval with { End = interval.End.AddMinutes(5) } } }, "定时采集已收到信号", false);
        Expect(received with { CaptureVerified = false }, "定时采集已收到信号", false);
        Expect(received with { PresentationRemainingMs = 0 }, "大屏已显示自习页", false);
        Expect(received with { PresentationVersion = "old" }, "大屏已显示自习页", false);
        Console.WriteLine("PASS: receipt freshness, policy identity, capture owner and independent visibility.");
    }
}
