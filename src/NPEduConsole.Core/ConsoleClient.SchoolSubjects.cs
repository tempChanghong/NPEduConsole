namespace NPEduConsole.Core;

public sealed record SchoolSubjectFields(string Code, string Name, string Category, int SortOrder);

public sealed record SchoolSubject(string Id, string SchoolId, string Code, string Name, string Category, int SortOrder, string UpdatedAt)
{
    public SchoolSubjectFields Fields => new(Code, Name, Category, SortOrder);
}

public sealed class SchoolSubjectCatalog
{
    internal SchoolSubjectCatalog(Guid sessionId, string schoolId, SchoolSubject[] subjects)
    {
        SessionId = sessionId;
        SchoolId = schoolId;
        Subjects = Array.AsReadOnly(subjects);
    }

    internal Guid SessionId { get; }
    public string SchoolId { get; }
    public IReadOnlyList<SchoolSubject> Subjects { get; }
}

public sealed partial class ConsoleClient
{
    private sealed record SubjectDirectory(string SchoolId, SchoolSubject[] Subjects);
    private sealed record SubjectCreationReceipt(Guid RequestId, SchoolSubject Subject);

    private const string SubjectsSuffix = "/subjects";
    private static string SchoolSubjectsPath(string schoolId) => $"api/v2/admin/schools/{Segment(schoolId)}{SubjectsSuffix}";

    private static bool IsSchoolSubjectPath(string path) => path.StartsWith("api/v2/admin/schools/", StringComparison.Ordinal) &&
        (path.EndsWith(SubjectsSuffix, StringComparison.Ordinal) || path.Contains(SubjectsSuffix + "/", StringComparison.Ordinal));

    private static ConsoleApiException SchoolSubjectError(int status, string? code) => new(status, code switch
    {
        "SUBJECT_CODE_EXISTS" => "该代码已被本校另一门学科使用，请更换代码。",
        "ORGANIZATION_VERSION_CONFLICT" => "另一位管理员已修改学科，请核对学校当前值。",
        "SUBJECT_CREATION_REQUEST_CONFLICT" => "该创建请求已用于其他操作，请核对目录和原始草稿。",
        "SUBJECT_CREATION_NOT_FOUND" => "暂未找到本次创建的回执。",
        "SUBJECT_CREATION_TARGET_CHANGED" => "本次创建已确认，请在目录中核对原学科的当前状态。",
        "SUBJECT_NOT_FOUND" => "这门学科已不在本校目录中，请刷新。",
        "SUBJECT_INVALID" => "KV 未接受这份学科配置，请核对名称、代码、类别和排序。",
        _ when status is 404 or 405 or 426 => "当前 KV 暂不支持 Console 学科目录，请升级 KV 或通过旧网页办理。",
        _ => HttpError(status, false).Message
    }, code);

    public async Task<SchoolSubjectCatalog> GetSchoolSubjectsAsync(string schoolId, CancellationToken ct)
    {
        var session = CurrentSession;
        var directory = await RequestAsync<SubjectDirectory>(HttpMethod.Get, SchoolSubjectsPath(schoolId), session.Token, ct);
        CheckCreationSession(session, ct);
        if (directory.SchoolId != schoolId || directory.Subjects is null) throw InvalidResponse();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subject in directory.Subjects)
        {
            CheckSchoolSubject(subject, schoolId);
            if (!ids.Add(subject.Id)) throw InvalidResponse();
        }
        return new(session.Id, schoolId, directory.Subjects);
    }

    public async Task<SchoolSubject> CreateSchoolSubjectAsync(SchoolSubjectCatalog catalog, SchoolSubjectFields fields,
        Guid requestId, CancellationToken ct)
    {
        var session = CheckSubjectSession(catalog);
        var input = ValidateSubjectFields(fields);
        var subject = await RequestAsync<SchoolSubject>(HttpMethod.Post, SchoolSubjectsPath(catalog.SchoolId), session.Token, ct,
            body: new { input.Code, input.Name, input.Category, input.SortOrder, requestId });
        CheckCreationSession(session, ct);
        CheckSchoolSubject(subject, catalog.SchoolId);
        // 创建回包必须就是本次提交的学科：同码被他人占用、或回执被错误重放时，
        // 这里会拒绝把别人的学科当成自己刚建的那门（它会被自动选进开科目录）。
        if (subject.Fields != input) throw InvalidResponse();
        return subject;
    }

    public async Task<SchoolSubject?> GetSchoolSubjectCreationAsync(SchoolSubjectCatalog catalog, Guid requestId, CancellationToken ct)
    {
        var session = CheckSubjectSession(catalog);
        try
        {
            var receipt = await RequestAsync<SubjectCreationReceipt>(HttpMethod.Get,
                $"{SchoolSubjectsPath(catalog.SchoolId)}/creation-receipts/{requestId:D}", session.Token, ct);
            CheckCreationSession(session, ct);
            if (receipt.RequestId != requestId) throw InvalidResponse();
            CheckSchoolSubject(receipt.Subject, catalog.SchoolId);
            return receipt.Subject;
        }
        catch (ConsoleApiException error) when (error is { StatusCode: 404, Code: "SUBJECT_CREATION_NOT_FOUND" })
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    public async Task<SchoolSubject> UpdateSchoolSubjectAsync(SchoolSubjectCatalog catalog, string subjectId,
        SchoolSubjectFields fields, CancellationToken ct)
    {
        var session = CheckSubjectSession(catalog);
        var baseline = catalog.Subjects.SingleOrDefault(subject => subject.Id == subjectId)
            ?? throw SchoolSubjectError(404, "SUBJECT_NOT_FOUND");
        var input = ValidateSubjectFields(fields);
        var subject = await RequestAsync<SchoolSubject>(HttpMethod.Patch, $"{SchoolSubjectsPath(catalog.SchoolId)}/{Segment(subjectId)}",
            session.Token, ct, body: new { input.Code, input.Name, input.Category, input.SortOrder, expectedUpdatedAt = baseline.UpdatedAt });
        CheckCreationSession(session, ct);
        CheckSchoolSubject(subject, catalog.SchoolId);
        if (subject.Id != subjectId || subject.Fields != input) throw InvalidResponse();
        return subject;
    }

    private Session CheckSubjectSession(SchoolSubjectCatalog catalog)
    {
        var session = CurrentSession;
        if (catalog.SessionId != session.Id)
            throw new ConsoleApiException(409, "登录账号已变化，请重新打开学科目录。", "SNAPSHOT_CHANGED");
        return session;
    }

    private static SchoolSubjectFields ValidateSubjectFields(SchoolSubjectFields fields)
    {
        var input = fields with { Code = fields.Code.Trim().ToUpperInvariant(), Name = fields.Name.Trim() };
        if (!ValidCreationCode(input.Code) || input.Name.Length is < 1 or > 191 || !KnownSubjectCategory(input.Category) ||
            input.SortOrder is < -10000 or > 10000) throw SchoolSubjectError(422, "SUBJECT_INVALID");
        return input;
    }

    private static bool KnownSubjectCategory(string category) => category is "CORE" or "ELECTIVE" or "OTHER";

    private static void CheckSchoolSubject(SchoolSubject subject, string schoolId)
    {
        if (subject is null || subject.SchoolId != schoolId || string.IsNullOrWhiteSpace(subject.Id) ||
            string.IsNullOrWhiteSpace(subject.Code) ||
            string.IsNullOrWhiteSpace(subject.Name) || !KnownSubjectCategory(subject.Category) || ValidateUpdatedAt(subject.UpdatedAt) is null)
            throw InvalidResponse();
        _ = Segment(subject.Id);
    }
}
