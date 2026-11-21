namespace NPEduConsole.Core;

public sealed record RosterStudent(string? Id, string Name, string? StudentNumber);

public static class RosterEditor
{
    public const int MaxStudents = 120;

    public static IReadOnlyList<RosterStudent> Validate(IReadOnlyList<RosterStudent> students)
    {
        if (students.Count > MaxStudents) throw new ArgumentException("一个班最多保存 120 名学生。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<RosterStudent>(students.Count);
        foreach (var student in students)
        {
            var name = student.Name.Trim();
            var number = student.StudentNumber?.Trim();
            if (name.Length is 0 or > 64 || number?.Length > 64)
                throw new ArgumentException($"第 {rows.Count + 1} 行需要姓名，姓名和学号均限 64 个字符。");
            if (!string.IsNullOrEmpty(number) && !numbers.Add(number))
                throw new ArgumentException($"学号 {number} 重复，请核对。");
            if (student.Id is { } id && (string.IsNullOrWhiteSpace(id) || !ids.Add(id)))
                throw new ArgumentException($"第 {rows.Count + 1} 行的学生身份无效或重复。");
            rows.Add(student with { Name = name, StudentNumber = string.IsNullOrEmpty(number) ? null : number });
        }
        return rows.AsReadOnly();
    }

    public static IReadOnlyList<RosterStudent> Import(string text, IReadOnlyList<RosterStudent> current, bool replace)
    {
        var parsed = new List<RosterStudent>();
        var unnumbered = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(text);
        int lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (parsed.Count == MaxStudents) throw new ArgumentException("一次最多导入 120 名学生。");
            string name, number;
            if (line.Contains('\t'))
            {
                var cells = line.Split('\t');
                if (cells.Length != 2) throw new ArgumentException($"第 {lineNumber} 行请粘贴学号和姓名两列。");
                number = cells[0].Trim();
                name = cells[1].Trim();
            }
            else
            {
                var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                number = parts.Length == 2 ? parts[0] : "";
                name = parts[^1].Trim();
            }
            if (name.Length == 0) throw new ArgumentException($"第 {lineNumber} 行缺少姓名。");
            if (number.Length == 0 && !unnumbered.Add(name))
                throw new ArgumentException($"第 {lineNumber} 行有同名且未填学号的学生，请补充不同学号或逐行编辑。");

            var row = new RosterStudent(null, name, number.Length == 0 ? null : number);
            var matches = current.Where(student => MatchesImportKey(student, row)).Take(2).ToArray();
            if (matches.Length > 1) throw new ArgumentException($"第 {lineNumber} 行对应多条现有记录，请逐行编辑。");
            parsed.Add(row with { Id = matches.FirstOrDefault()?.Id });
        }
        if (parsed.Count == 0) throw new ArgumentException("请先粘贴学生名单。");
        var incoming = Validate(parsed);
        if (replace) return incoming;
        var merged = current.ToList();
        foreach (var row in incoming)
        {
            var index = merged.FindIndex(student => MatchesImportKey(student, row));
            if (index >= 0) merged[index] = row;
            else merged.Add(row);
        }
        return Validate(merged);
    }

    private static bool MatchesImportKey(RosterStudent candidate, RosterStudent row) => row.StudentNumber is { } number
        ? candidate.StudentNumber?.Trim() == number
        : string.IsNullOrWhiteSpace(candidate.StudentNumber) && candidate.Name.Trim() == row.Name;

    public static IReadOnlyList<string> Changes(IReadOnlyList<RosterStudent> before, IReadOnlyList<RosterStudent> after)
    {
        var original = before.Select((row, index) => (row, index))
            .Where(item => item.row.Id is not null).ToDictionary(item => item.row.Id!, StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var changes = new List<string>();
        for (int index = 0; index < after.Count; index++)
        {
            var row = after[index];
            if (row.Id is null || !original.TryGetValue(row.Id, out var old))
            {
                changes.Add($"新增第 {index + 1} 行　{Label(row)}");
                continue;
            }
            kept.Add(row.Id);
            if (old.row.Name != row.Name || (old.row.StudentNumber ?? "") != (row.StudentNumber ?? ""))
                changes.Add($"修改第 {old.index + 1} 行　{Label(old.row)} → {Label(row)}");
            if (old.index != index)
                changes.Add($"调整顺序　{Label(row)}（第 {old.index + 1} 行 → 第 {index + 1} 行）");
        }
        for (int index = 0; index < before.Count; index++)
        {
            var row = before[index];
            if (row.Id is not null && !kept.Contains(row.Id))
                changes.Add($"移出第 {index + 1} 行　{Label(row)}（保留历史考勤）");
        }
        return changes;
    }

    private static string Label(RosterStudent row) =>
        string.IsNullOrEmpty(row.StudentNumber) ? row.Name : $"{row.Name}（{row.StudentNumber}）";

    public static bool MatchesSaved(ClassRoster baseline, IReadOnlyList<RosterStudent> submitted, ClassRoster current)
    {
        if (current.SessionId != baseline.SessionId || current.SchoolId != baseline.SchoolId ||
            current.ClassId != baseline.ClassId || current.Students.Count != submitted.Count) return false;
        var oldIds = baseline.Students.Select(student => student.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < submitted.Count; i++)
        {
            var expected = submitted[i];
            var saved = current.Students[i];
            if (saved.Name != expected.Name.Trim() || (saved.StudentNumber ?? "") != (expected.StudentNumber?.Trim() ?? "") ||
                (expected.Id is null ? oldIds.Contains(saved.Id) : expected.Id != saved.Id)) return false;
        }
        return true;
    }
}
