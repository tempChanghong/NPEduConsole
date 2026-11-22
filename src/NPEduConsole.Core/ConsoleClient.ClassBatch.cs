using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPEduConsole.Core;

public sealed record BatchClassRow(string Code, string Name, bool IsStudentSelectable);
public sealed record BatchClassIssue(int Row, string Field, string Code, string Message);
public sealed record BatchCreatedClass(string Id, string Code, string Name, bool IsStudentSelectable);
public sealed record BatchClassReceipt(string TermId, string GradeId, Guid RequestId, IReadOnlyList<BatchCreatedClass> Classes);

public sealed partial class ConsoleClient
{
    private const string ClassBatchSuffix = CurrentClassesSuffix + "/batch";
    private sealed record BatchValidation(string SchoolId, string TermId, string GradeId, BatchClassIssue[] Rows);
    private sealed record BatchReceiptData(string SchoolId, string TermId, string GradeId, Guid RequestId,
        [property: JsonRequired] BatchCreatedRow[]? Classes);
    private sealed record BatchCreatedRow(string Id, string Code, string Name, bool? IsStudentSelectable);

    public async Task<IReadOnlyList<BatchClassIssue>> ValidateClassBatchAsync(ClassCreationContext context, string gradeId,
        IReadOnlyList<BatchClassRow> rows, CancellationToken ct)
    {
        var session = RequireBatchContext(context, gradeId);
        var classes = CopyBatchRows(rows);
        try
        {
            var result = await RequestAsync<BatchValidation>(HttpMethod.Post, ClassBatchPath(context) + "/validate", session.Token,
                ct, body: context.IsSpecifiedTerm ? new { expectedScope = context.ScopeVersion, gradeId, classes }
                    : new { context.TermId, gradeId, classes });
            CheckCreationSession(session, ct);
            if (result.SchoolId != context.SchoolId || result.TermId != context.TermId || result.GradeId != gradeId ||
                result.Rows is not { Length: 0 }) throw InvalidResponse();
            return [];
        }
        catch (ConsoleApiException error) when (error.BatchIssues.Count > 0)
        {
            CheckCreationSession(session, ct);
            if (error.BatchIssues.Any(issue => issue.Row > classes.Length)) throw InvalidResponse();
            return error.BatchIssues;
        }
    }

    public async Task<BatchClassReceipt> CreateClassBatchAsync(ClassCreationContext context, string gradeId,
        IReadOnlyList<BatchClassRow> rows, Guid requestId, CancellationToken ct)
    {
        var session = RequireBatchContext(context, gradeId, requestId);
        var classes = CopyBatchRows(rows);
        var result = await RequestAsync<BatchReceiptData>(HttpMethod.Post, ClassBatchPath(context), session.Token, ct,
            body: context.IsSpecifiedTerm ? new { expectedScope = context.ScopeVersion, gradeId, requestId, classes }
                : new { context.TermId, gradeId, requestId, classes });
        CheckCreationSession(session, ct);
        var receipt = CheckBatchReceipt(context, gradeId, requestId, result) ?? throw InvalidResponse();
        if (receipt.Classes.Count != classes.Length || receipt.Classes.Where((item, index) =>
                item.Code != classes[index].Code || item.Name != classes[index].Name ||
                item.IsStudentSelectable != classes[index].IsStudentSelectable).Any())
            throw InvalidResponse();
        return receipt;
    }

    public async Task<BatchClassReceipt?> GetClassBatchResultAsync(ClassCreationContext context, string gradeId, Guid requestId,
        CancellationToken ct)
    {
        var session = RequireBatchContext(context, gradeId, requestId);
        string path = $"{ClassBatchPath(context)}/{requestId:D}?termId={Segment(context.TermId!)}&gradeId={Segment(gradeId)}";
        var result = await RequestAsync<BatchReceiptData>(HttpMethod.Get, path, session.Token, ct);
        CheckCreationSession(session, ct);
        return CheckBatchReceipt(context, gradeId, requestId, result);
    }

    private static BatchClassRow[] CopyBatchRows(IReadOnlyList<BatchClassRow> rows)
    {
        if (rows.Count is < 1 or > 100)
            throw new ConsoleApiException(422, "批量行政班数量必须为 1 至 100 个。", "ADMIN_CLASS_BATCH_INVALID");
        return rows.Select(row => row with { Code = NormalizeBatchCode(row.Code), Name = row.Name.Trim() }).ToArray();
    }

    private static string NormalizeBatchCode(string value)
    {
        string code = value.Trim();
        return code.All(char.IsAscii) ? code.ToUpperInvariant() : code;
    }

    private Session RequireBatchContext(ClassCreationContext context, string gradeId, Guid? requestId = null)
    {
        var session = CurrentSession;
        if (session.Id != context.SessionId)
            throw new ConsoleApiException(409, "登录会话已改变，请重新选择学校与年级。", "SNAPSHOT_CHANGED");
        if (context.TermId is null || !context.Grades.Any(grade => grade.Id == gradeId))
            throw new ConsoleApiException(422, "请选择当前启用学期的已有年级。", "GRADE_INVALID");
        if (requestId == Guid.Empty)
            throw new ConsoleApiException(422, "缺少批次编号。", "ADMIN_CLASS_BATCH_INVALID");
        return session;
    }

    private static BatchClassReceipt? CheckBatchReceipt(ClassCreationContext context, string gradeId, Guid requestId,
        BatchReceiptData result)
    {
        if (result.SchoolId != context.SchoolId || result.TermId != context.TermId || result.GradeId != gradeId ||
            result.RequestId != requestId) throw InvalidResponse();
        if (result.Classes is null) return null;
        if (result.Classes.Length is < 1 or > 100) throw InvalidResponse();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var classes = new List<BatchCreatedClass>(result.Classes.Length);
        foreach (var row in result.Classes)
        {
            if (row is null || row.IsStudentSelectable is not { } selectable ||
                !ids.Add(Segment(row.Id)) || !ValidCreationCode(row.Code) || !codes.Add(row.Code) ||
                string.IsNullOrWhiteSpace(row.Name) || row.Name.Length > 191) throw InvalidResponse();
            classes.Add(new(row.Id, row.Code, row.Name, selectable));
        }
        return new(result.TermId, result.GradeId, result.RequestId, classes.AsReadOnly());
    }

    private static string ClassBatchPath(ClassCreationContext context) =>
        CreationClassesPath(context) + "/batch";

    private static bool IsClassBatchPath(string path) =>
        path.EndsWith(ClassBatchSuffix, StringComparison.Ordinal) ||
        path.Contains(ClassBatchSuffix + "/", StringComparison.Ordinal) ||
        path.Contains(TermStructureSegment + "/classes/batch", StringComparison.Ordinal);

    private static async Task<ConsoleApiException> ClassBatchErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        int status = (int)response.StatusCode;
        string? code = null;
        BatchClassIssue[] issues = [];
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("code", out var errorCode) && errorCode.ValueKind == JsonValueKind.String)
                    code = errorCode.GetString();
                if (root.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array &&
                    rows.GetArrayLength() <= 400)
                    issues = rows.Deserialize<BatchClassIssue[]>(Json) ?? [];
            }
        }
        catch (JsonException) { }
        if ((status is 404 or 405 or 426) && code != "GRADE_INVALID")
            return new(status, "当前 KV 缺少批量开班接口，请升级 KV 后重试。", "CLASS_BATCH_UNSUPPORTED");
        if (status is not (409 or 422) || code is not ("WORKSPACE_CODE_EXISTS" or "ADMIN_CLASS_BATCH_INVALID") ||
            issues.Any(issue => issue is null || issue.Row is < 1 or > 100 || issue.Field is not ("code" or "name" or "isStudentSelectable") ||
                string.IsNullOrWhiteSpace(issue.Code) || string.IsNullOrWhiteSpace(issue.Message) || issue.Message.Length > 512))
            issues = [];
        return new(status, code switch
        {
            "ADMIN_CLASS_BATCH_INVALID" => "请核对批次内容，修正标记的班级。",
            "WORKSPACE_CODE_EXISTS" => "本学期已有教学空间使用标记的代码。该批未写入。",
            "CLASS_BATCH_REQUEST_CONFLICT" => "该批次编号已用于其他操作，请核对原提交。",
            "CLASS_BATCH_TERM_CHANGED" => "启用学期已改变，请刷新后重新选择年级。",
            "TERM_STRUCTURE_SCOPE_CHANGED" => "所选学期或学校启用学期已变化。请重新读取学期和年级后核对草稿。",
            "GRADE_INVALID" => "年级已不属于当前启用学期，请刷新年级。",
            _ => HttpError(status, false).Message
        }, code) { BatchIssues = Array.AsReadOnly(issues) };
    }
}
