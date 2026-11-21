using System.Globalization;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace NPEduConsole.Core;

public static class OrganizationImportDocumentReader
{
    public const int MaximumCharacters = 1_000_000;

    public static OrganizationImportDocument ReadLegacy(string text)
    {
        if (text.Length > MaximumCharacters) throw new ArgumentException("文件超过可导入大小，请按一个年级拆分。");
        try
        {
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 24 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException();
            var school = Property(root, "school");
            var term = Property(root, "term");
            var grade = Property(root, "grade");
            return new(
                new(Value(grade, "code"), Value(grade, "name"), Number(grade, "sortOrder") ?? 0),
                Rows(root, "subjects").Select((row, index) => new OrganizationSubject(Value(row, "code"), Value(row, "name"),
                    String(row, "category") ?? "OTHER", Number(row, "sortOrder") ?? index)).ToArray(),
                Rows(root, "administrativeClasses").Select(row => new OrganizationClass(Value(row, "code"), Value(row, "name"),
                    Boolean(row, "isStudentSelectable"), false, Rules(row))).ToArray(),
                Rows(root, "courseGroups").Select(row => new OrganizationGroup(Value(row, "code"), Value(row, "name"), Value(row, "subject"),
                    Boolean(row, "isStudentSelectable"), false, Rows(row, "sourceClasses").Select(source => source.GetString() ?? "").ToArray())).ToArray(),
                new(String(school, "id"), String(school, "code"), String(school, "name"),
                    String(term, "id"), Number(term, "academicYear"), Number(term, "semester"), String(term, "name"),
                    String(grade, "code"), String(grade, "name")));
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            throw new ArgumentException("无法读取组织 JSON。请核对旧网页导出的字段、列类型和文件格式。");
        }
    }

    public static OrganizationImportDocument SevenClassExample(OrganizationGrade? selectedGrade)
    {
        using var input = typeof(OrganizationImportDocumentReader).Assembly
            .GetManifestResourceStream("NPEduConsole.Core.Resources.SevenClassOrganization.json")!;
        using var reader = new StreamReader(input);
        var document = ReadLegacy(reader.ReadToEnd());
        var grade = selectedGrade is { Code.Length: > 0, Name.Length: > 0 } ? selectedGrade : document.Grade;
        string Code(string original) => original.StartsWith("G2-", StringComparison.Ordinal) ? grade.Code + original[2..] : original;
        string Name(string original) => original.StartsWith("高二", StringComparison.Ordinal) ? grade.Name + original[2..] : original;
        return new(grade, document.Subjects, document.AdministrativeClasses
            .Select(row => row with { Code = Code(row.Code), Name = Name(row.Name) }).ToArray(), document.CourseGroups
            .Select(row => row with { Code = Code(row.Code), SourceClassCodes = row.SourceClassCodes.Select(Code).ToArray() }).ToArray(), null);
    }

    public static (OrganizationClass[] Classes, OrganizationSubject[] Subjects) ReadClassColumns(string text)
    {
        var classes = new Dictionary<string, OrganizationClass>(StringComparer.OrdinalIgnoreCase);
        var subjects = new Dictionary<string, OrganizationSubject>(StringComparer.OrdinalIgnoreCase);
        foreach (var (row, index) in ReadColumns(text, "班级代码"))
        {
            if (row.Length < 2 || row.Length > 7 || row[0].Length == 0 || row[1].Length == 0)
                throw RowError(index, "需要班级代码、班名，以及可选的学科代码、学科名称、授课方式、必修、学生自选。");
            var selectable = Flag(row.ElementAtOrDefault(6), index);
            if (!classes.TryGetValue(row[0], out var room))
                room = new(row[0], row[1], selectable, false, []);
            else if (room.Name != row[1] || room.IsStudentSelectable is { } prior && selectable is { } now && prior != now)
                throw RowError(index, "同代码班级的名称或学生自选设置不一致。");
            room = room with { IsStudentSelectable = selectable ?? room.IsStudentSelectable };
            var code = row.ElementAtOrDefault(2);
            var name = row.ElementAtOrDefault(3);
            if (!string.IsNullOrWhiteSpace(code))
            {
                var mode = (row.ElementAtOrDefault(4) ?? "").ToUpperInvariant() switch
                {
                    "随班" or "随行政班" or "ADMIN_CLASS" => "ADMIN_CLASS",
                    "走班" or "COURSE_GROUP" => "COURSE_GROUP",
                    _ => throw RowError(index, "该学科需注明随班或走班。")
                };
                if (room.SubjectRules.Any(rule => string.Equals(rule.SubjectCode, code, StringComparison.OrdinalIgnoreCase)))
                    throw RowError(index, "同一班的学科代码重复。");
                room = room with { SubjectRules = [.. room.SubjectRules, new(code, mode, Flag(row.ElementAtOrDefault(5), index) ?? mode == "ADMIN_CLASS")] };
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (subjects.TryGetValue(code, out var present) && present.Name != name)
                        throw RowError(index, "同代码学科名称不一致。");
                    subjects[code] = new(code, name);
                }
            }
            else if (row.Skip(3).Take(3).Any(value => value.Length > 0))
                throw RowError(index, "有学科字段却没有填写学科代码。");
            classes[row[0]] = room;
        }
        return (classes.Values.ToArray(), subjects.Values.ToArray());
    }

    public static OrganizationGroup[] ReadGroupColumns(string text) => ReadColumns(text, "教学班代码").Select(item =>
    {
        var (row, index) = item;
        if (row.Length is < 3 or > 5 || row.Take(3).Any(string.IsNullOrWhiteSpace))
            throw RowError(index, "需要教学班代码、名称、学科代码，以及可选的来源班代码、学生自选。");
        var sources = (row.ElementAtOrDefault(3) ?? "").Split([',', '，', '、', ';', '；'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return new OrganizationGroup(row[0], row[1], row[2], Flag(row.ElementAtOrDefault(4), index), false, sources);
    }).ToArray();

    private static JsonElement Property(JsonElement row, string field) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(field, out var value) ? value : default;
    private static string? String(JsonElement row, string field)
    {
        var value = Property(row, field);
        return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : value.GetString();
    }
    private static string Value(JsonElement row, string field) => String(row, field) ?? "";
    private static int? Number(JsonElement row, string field)
    {
        var value = Property(row, field);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ?
            int.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetInt32();
    }
    private static bool? Boolean(JsonElement row, string field)
    {
        var value = Property(row, field);
        return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : value.GetBoolean();
    }
    private static JsonElement[] Rows(JsonElement row, string field)
    {
        var value = Property(row, field);
        return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? [] : value.EnumerateArray().ToArray();
    }
    private static OrganizationSubjectRule[] Rules(JsonElement row)
    {
        var rules = Property(row, "subjectRules");
        if (rules.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return [];
        return rules.EnumerateObject().Select(item =>
        {
            var mode = item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString()! : Value(item.Value, "deliveryMode");
            var required = item.Value.ValueKind == JsonValueKind.String ?
                string.Equals(mode.Trim(), "ADMIN_CLASS", StringComparison.OrdinalIgnoreCase) : Boolean(item.Value, "isCompulsory") ?? false;
            return new OrganizationSubjectRule(item.Name, mode, required);
        }).ToArray();
    }
    private static ArgumentException RowError(long line, string message) => new($"第 {line} 行：{message}");
    private static bool? Flag(string? text, long line) => text?.ToUpperInvariant() switch
    {
        null or "" => null,
        "是" or "TRUE" or "1" or "允许" or "必修" => true,
        "否" or "FALSE" or "0" or "不允许" or "非必修" => false,
        _ => throw RowError(line, "标志列请填写「是」或「否」。")
    };

    private static IEnumerable<(string[] Cells, long Index)> ReadColumns(string text, string header)
    {
        if (text.Length > MaximumCharacters) throw new ArgumentException("表格过长，请按一个年级拆分。");
        using var parser = new TextFieldParser(new StringReader(text));
        parser.SetDelimiters("\t");
        var first = true;
        while (!parser.EndOfData)
        {
            var line = parser.LineNumber;
            string[] cells;
            try { cells = parser.ReadFields() ?? []; }
            catch (MalformedLineException) { throw RowError(line, "引号或制表符格式无效。"); }
            if (first)
            {
                first = false;
                if (cells.FirstOrDefault() == header) continue;
            }
            if (cells.Any(value => value.Length > 0)) yield return (cells, line);
        }
    }
}
