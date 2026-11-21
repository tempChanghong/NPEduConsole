using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed record RosterCurrentRow(int Position, RosterStudent Student)
{
    public string Label => $"{Position}. {Student.Name} · 学号 {Student.StudentNumber ?? "无"}";
    public string AutomationId => $"RosterCurrentStudent-{Student.Id}";
}

internal sealed record RosterReviewRow(RosterChange Change)
{
    public string Kind => Change.Kind switch
    {
        RosterChangeKind.Add => "新增",
        RosterChangeKind.Edit => "修改",
        RosterChangeKind.Move => "调整顺序",
        _ => "移出"
    };
    public bool IsAdd => Change.Kind == RosterChangeKind.Add;
    public bool IsRemove => Change.Kind == RosterChangeKind.Remove;
    public string Before => Label(Change.Before, Change.BeforePosition, "—");
    public string After => Label(Change.After, Change.AfterPosition, "移出名单");
    public string AutomationId => $"RosterChange-{Change.Before?.Id ?? Change.After?.Id ?? $"new-{Change.AfterPosition}"}-{Change.Kind}";

    private static string Label(RosterStudent? student, int? position, string empty) => student is null ? empty
        : $"{student.Name} · 学号 {student.StudentNumber ?? "无"} · 第 {position} 行";

    public override string ToString() => $"{Kind}　{Before} → {After}";
}
