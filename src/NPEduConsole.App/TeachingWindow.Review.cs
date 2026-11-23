using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed record TeachingPending(TeachingDraft Draft, TeachingPreview Preview, string? EntryAccountId = null);

internal enum TeachingObservation { Applied, Stale, Blocked, Readback }

internal sealed record TeachingPreviewRow(TeachingRow Row, string Before, string After, string Status,
    string? Reason, bool NeedsAttention)
{
    public bool CanLocatePeer { get; init; }
    public string ClassName => Row.ClassName;
    public string AutomationId => "TeachingPreviewClass-" + Row.ClassId;
    public string LocateAutomationId => "LocateTeachingPeer-" + Row.ClassId;
    public bool HasReason => !string.IsNullOrWhiteSpace(Reason);

    internal static TeachingPreviewRow Planned(TeachingRow row, string? teacher, TeachingDraft draft)
    {
        bool blocked = IsBlocked(row);
        string before = row.State == "INVALID" ? "待核对" : Display(row.Assignments);
        string after = row.State switch
        {
            "ADD" => Display(row.Assignments.Select(item => item.ToString()).Append(ProposedTeacher(teacher, draft.Position))),
            "CHANGE" => Display(row.Assignments.Select(item => item.Id == draft.AssignmentId
                ? ProposedTeacher(teacher, draft.Position) : item.ToString())),
            "REMOVE" => Display(row.Assignments.Where(item => item.Id != draft.AssignmentId)),
            _ when blocked => "本批暂不能提交",
            _ => before
        };
        return new(row, before, after, StateLabel(row.State), blocked ? row.Message : null, blocked);
    }

    internal static TeachingPreviewRow[] Compare(TeachingRow[] current, TeachingPending original, TeachingObservation observation)
    {
        var before = original.Preview.Rows.ToDictionary(row => row.ClassId, StringComparer.Ordinal);
        return current.Select(row => Observed(row, before.GetValueOrDefault(row.ClassId), observation switch
        {
            TeachingObservation.Stale => true,
            TeachingObservation.Blocked => IsBlocked(row),
            TeachingObservation.Readback => !Matches(original.Draft, row),
            _ => false
        }, observation == TeachingObservation.Applied)).ToArray();
    }

    internal static bool Matches(TeachingDraft draft, TeachingRow row) => draft.Action switch
    {
        "REMOVE" => row.State == "ALREADY_REMOVED",
        "CHANGE" => row.Assignments.Any(item => item.AccountId == draft.AccountId && item.Position == draft.Position) &&
            (row.State == "UNCHANGED" || row.Assignments.All(item => item.Id != draft.AssignmentId)),
        _ => row.Assignments.Any(item => item.AccountId == draft.AccountId && item.Position == draft.Position)
    };

    private static bool IsBlocked(TeachingRow row) => row.State is "OTHER_PRIMARY" or "EXISTING_ROLE" or "INVALID";

    internal static TeachingPreviewRow Observed(TeachingRow row, TeachingRow? earlier, bool changed, bool applied)
    {
        string? reason = IsBlocked(row) ? row.Message : null;
        string status = applied ? row.State switch
        {
            "ADD" => "已新增",
            "CHANGE" => "已修改",
            "REMOVE" => "已移除",
            _ => "保持原安排"
        } : changed ? "请重新核对" : "已读取";
        return new(row, earlier is null ? "—" : Display(earlier.Assignments),
            row.State == "INVALID" ? "待核对" : Display(row.Assignments), status, reason, changed || row.State == "INVALID");
    }

    private static string StateLabel(string state) => state switch
    {
        "ADD" => "将新增",
        "CHANGE" => "将修改",
        "REMOVE" => "将移除",
        "OTHER_PRIMARY" => "已有其他主讲",
        "EXISTING_ROLE" => "已有不同任课身份",
        "INVALID" => "待核对",
        _ => "保持原安排"
    };

    private static string ProposedTeacher(string? teacher, string? position) =>
        $"{teacher} · {(position == "PRIMARY" ? "主讲" : "协同")}";

    private static string Display(IEnumerable<TeachingAssignment> rows) => Display(rows.Select(item => item.ToString()));

    private static string Display(IEnumerable<string> rows) => string.Join(Environment.NewLine, rows) is { Length: > 0 } value
        ? value : "暂无任课";
}
