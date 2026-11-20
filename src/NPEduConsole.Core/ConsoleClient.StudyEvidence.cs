using System.Diagnostics;

namespace NPEduConsole.Core;

public sealed partial class ConsoleClient
{
    public async Task<StudyEvidenceReceipt> ReadStudyReceiptAsync(StudyEvidenceScope scope, string deviceId, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var path = $"api/v2/npep/schools/{Segment(scope.SchoolId)}/devices/{Segment(deviceId)}/study-settings";
        var token = CurrentSession.Token;
        StudyEvidenceReceipt result;
        try { result = await RequestAsync<StudyEvidenceReceipt>(HttpMethod.Get, path, token, ct, version: "0.10"); }
        catch (ConsoleApiException error) when (error.StatusCode == 401)
        {
            // KV also returns 401 for an expired device while the school account is still valid.
            await RequestAsync<OverviewPage>(HttpMethod.Get, $"api/v2/npep/schools/{Segment(scope.SchoolId)}/device-overview?limit=1",
                token, ct, version: "0.1");
            throw new ConsoleApiException(409, "大屏绑定已失效，请刷新班级列表。");
        }
        if (result.Current.Scope != scope || result.Current.Identity.DeviceId != deviceId ||
            result.Current.Version.Length != 64 || !result.Current.Version.All(char.IsAsciiHexDigitLower))
            throw InvalidResponse();
        return result with { RequestStarted = started };
    }

    public async Task<StudyReportPage> ReadStudyReportsAsync(StudyEvidenceScope scope, string? cursor, CancellationToken ct)
    {
        var path = StudyReportsPath(scope);
        if (cursor is not null) path += "&cursor=" + Uri.EscapeDataString(cursor);
        var page = await RequestAsync<StudyReportPage>(HttpMethod.Get, path, CurrentSession.Token, ct, version: "0.17");
        if (page.Items.Length > 20 || page.Items.Any(row => !ValidStudyGroup(row)) ||
            page.Cursor is not null && (page.Cursor.Length == 0 || page.Cursor == cursor)) throw InvalidResponse();
        return page;
    }

    public async Task<StudyReportGroup> ReadStudyReportAsync(StudyEvidenceScope scope, StudyReportGroup selected, CancellationToken ct)
    {
        var path = StudyReportsPath(scope) + "&groupId=" + Uri.EscapeDataString(selected.GroupId);
        var page = await RequestAsync<StudyReportPage>(HttpMethod.Get, path, CurrentSession.Token, ct, version: "0.17");
        if (page.Items is not [var result] || !ValidStudyGroup(result) || result.GroupId != selected.GroupId ||
            result.DeviceId != selected.DeviceId || result.Mode != selected.Mode || result.Window != selected.Window ||
            result.Fragments is not { } parts || parts.Length != result.FragmentCount ||
            parts.Any(p => p is null || p.Ownership != scope || p.Study.Window != result.Window || p.Study.Mode != result.Mode ||
                p.Summary.Coverage is < 0 or > 1 || p.EndedAt < p.StartedAt)) throw InvalidResponse();
        return result;
    }

    private static bool ValidStudyGroup(StudyReportGroup row) =>
        row is not null && row.GroupId.Length > 0 && row.DeviceId.Length > 0 && row.FragmentCount > 0 && row.Window.Start < row.Window.End &&
        row.Window.Start.Kind == DateTimeKind.Unspecified && row.Window.End.Kind == DateTimeKind.Unspecified &&
        (row.Mode, row.ClockSource) is ("Daily", "ClassIsland") or ("Exam", "Windows");

    private static string StudyReportsPath(StudyEvidenceScope scope) =>
        $"api/v2/npep/schools/{Segment(scope.SchoolId)}/study-sessions?termId={Segment(scope.TermId)}&classId={Segment(scope.ClassId)}";
}
