using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace NPEduConsole.Core;

public sealed record TeacherAllocationDocumentTarget(string? SchoolId, string? TermId, string? GradeId);
public sealed record TeacherAllocationDocument(TeacherAllocationDocumentTarget Target, AllocationTeacher[] Teachers);

public static class TeacherAllocationDocumentReader
{
    public static TeacherAllocationDocument ReadLegacy(string text, string mode)
    {
        CheckSize(text);
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            var isArray = root.ValueKind == JsonValueKind.Array;
            var target = isArray ? new TeacherAllocationDocumentTarget(null, null, null) :
                new(TargetId(root, "schoolId"), TargetId(root, "termId"), TargetId(root, "gradeId"));
            var assignments = isArray ? root : root.GetProperty("assignments");
            var rows = assignments.EnumerateArray().Select(row =>
            {
                var key = Value(row, mode == "OAUTH_EMAIL" ? "email" : "username");
                var role = Value(row, "role") ?? "TEACHER";
                var boards = row.TryGetProperty("workspaceCodes", out var codes) || row.TryGetProperty("workspaces", out codes)
                    ? codes.EnumerateArray().Select(value => value.GetString() ?? "").ToArray() : [];
                return Row(mode, key, Value(row, "name"), mode == "LOCAL_PIN" ? Value(row, "pin") : null, role, boards);
            }).ToArray();
            return new(target, Merge(rows, mode));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ArgumentException("无法读取旧教师分配 JSON，请核对 assignments、姓名、账号和班级列。");
        }
    }

    public static AllocationTeacher[] ReadColumns(string text, string mode)
    {
        CheckSize(text);
        using var parser = new TextFieldParser(new StringReader(text));
        parser.SetDelimiters("\t");
        var pinned = mode == "LOCAL_PIN";
        var rows = new List<AllocationTeacher>();
        while (!parser.EndOfData)
        {
            var line = parser.LineNumber;
            string[] cells;
            try { cells = parser.ReadFields() ?? []; }
            catch (MalformedLineException) { throw new ArgumentException($"第 {line} 行：请核对引号或制表符格式。"); }
            if (cells.Length == 0 || cells.All(string.IsNullOrWhiteSpace)) continue;
            if (rows.Count == 0 && cells[0] is "短账号" or "邮箱") continue;
            if (cells.Length > (pinned ? 5 : 4) || string.IsNullOrWhiteSpace(cells[0]))
                throw new ArgumentException($"第 {line} 行：列数或教师账号无效。");
            var boards = (cells.ElementAtOrDefault(pinned ? 3 : 2) ?? "").Split([',', '，', '、', ';', '；'],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            rows.Add(Row(mode, cells[0], cells.ElementAtOrDefault(1), pinned ? cells.ElementAtOrDefault(2) : null,
                cells.ElementAtOrDefault(pinned ? 4 : 3) ?? "TEACHER", boards));
        }
        return Merge(rows, mode);
    }

    private static AllocationTeacher Row(string mode, string? key, string? name, string? pin, string role, IEnumerable<string> boards)
    {
        var identity = (key ?? "").Trim();
        var normalizedRole = role.Trim().ToUpperInvariant() switch
        { "教师" or "" => "TEACHER", "助教" => "ASSISTANT", "只读" => "VIEWER", var value => value };
        return new(mode == "OAUTH_EMAIL" ? null : identity, mode == "OAUTH_EMAIL" ? identity : null, null,
            NonEmpty(name), NonEmpty(pin), false, boards.Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => new AllocationAccess(null, code.Trim().ToUpperInvariant(), "SET", normalizedRole)).ToArray());
    }

    private static AllocationTeacher[] Merge(IEnumerable<AllocationTeacher> rows, string mode)
    {
        var people = new Dictionary<string, AllocationTeacher>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = (mode == "OAUTH_EMAIL" ? row.Email : row.Username) ?? "";
            if (key.Length == 0) throw new ArgumentException("每位教师都需要短账号或邮箱。");
            if (!people.TryGetValue(key, out var old)) people[key] = row;
            else
            {
                if (old.Name is not null && row.Name is not null && old.Name != row.Name)
                    throw new ArgumentException("同一教师在文件中填写了不同姓名，请核对后再导入。");
                if (old.Pin is not null && row.Pin is not null && old.Pin != row.Pin)
                    throw new ArgumentException("同一短账号在表中有多份初始 PIN，请核对输入。");
                var access = old.Access.Concat(row.Access).GroupBy(item => item.WorkspaceCode).Select(items =>
                {
                    if (items.Select(item => item.Role).Distinct().Count() > 1)
                        throw new ArgumentException("同一教师对同一教学空间填写了不同角色，请核对后再导入。");
                    return items.First();
                }).ToArray();
                people[key] = old with { Name = old.Name ?? row.Name, Pin = old.Pin ?? row.Pin, Access = access };
            }
        }
        if (people.Count == 0) throw new ArgumentException("表格中没有教师。");
        return people.Values.ToArray();
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? TargetId(JsonElement root, string property)
    {
        var id = NonEmpty(Value(root, property));
        if (id is not null && (id.Length > 191 || id.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_' and not '.')))
            throw new ArgumentException($"旧教师分配 JSON 的 {property} 不是有效的目标编号。");
        return id;
    }

    private static string? Value(JsonElement row, string name) => row.TryGetProperty(name, out var value) &&
        value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static void CheckSize(string text)
    {
        if (text.Length > OrganizationImportDocumentReader.MaximumCharacters)
            throw new ArgumentException("导入表过长，请按年级拆分。");
    }
}
