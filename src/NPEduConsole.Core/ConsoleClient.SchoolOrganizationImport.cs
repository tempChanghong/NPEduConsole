namespace NPEduConsole.Core;

public sealed partial class ConsoleClient
{
    private static string OrganizationImportPath(string school) => $"api/v2/admin/schools/{Segment(school)}/organization-import";

    private static ConsoleApiException SchoolImportError(int status, string? code) => new(status, code switch
    {
        "ORG_IMPORT_TERM_CHANGED" or "TEACHER_ALLOCATION_TERM_CHANGED" =>
            "目标学期状态已变化。请返回学期目录核对。",
        "ORG_IMPORT_GRADE_CHANGED" or "TEACHER_ALLOCATION_GRADE_CHANGED" =>
            "原年级与当前目标已不一致。请重新选择年级并预览。",
        "ORG_IMPORT_TARGET_INVALID" => "请选择有效的导入目标学期。",
        "ORG_IMPORT_PREVIEW_REQUIRED" or "TEACHER_ALLOCATION_PREVIEW_REQUIRED" => "请重新预览并核对逐项差异。",
        "ORG_IMPORT_REQUEST_CONFLICT" or "TEACHER_ALLOCATION_REQUEST_CONFLICT" => "原编号已用于不同的办理，请核对对应回执。",
        "ORG_IMPORT_RECEIPT_NOT_FOUND" or "TEACHER_ALLOCATION_RECEIPT_NOT_FOUND" => "暂未找到本人原请求的回执。",
        "ORG_IMPORT_CONCURRENT_CODE" => "代码刚被其他操作占用，本次零写入。请重新预览。",
        "TEACHER_ALLOCATION_MODE_CHANGED" => "学校登录方式已变化。请重新读取教师目录。",
        "TEACHER_ALLOCATION_IDENTITY_CHANGED" or "TEACHER_ALLOCATION_INVITATION_CHANGED" =>
            "账号或访问已有变化。本次零写入，请重新预览。",
        _ => HttpError(status, false).Message
    }, code);

    public async Task<OrganizationImportCatalog> GetOrganizationImportAsync(string school, string? term, CancellationToken ct, bool archivedCorrection = false)
    {
        if (archivedCorrection && string.IsNullOrWhiteSpace(term)) throw new ArgumentException("请选择要更正的归档学期。");
        var session = CurrentSession;
        var path = OrganizationImportPath(school) + (term is null ? "" : $"?termId={Segment(term)}");
        if (archivedCorrection) path += "&archivedCorrection=true";
        var data = await RequestAsync<OrganizationImportCatalog>(HttpMethod.Get, path, session.Token, ct);
        CheckCreationSession(session, ct);
        if (data.School.Id != school || term is not null && data.TermId != term ||
            data.Terms.Any(row => row.Status is not ("ACTIVE" or "DRAFT") && !(archivedCorrection && row.Status == "ARCHIVED")) ||
            archivedCorrection && !data.Terms.Any(row => row.Id == term && row.Status == "ARCHIVED") ||
            data.TermId is not null && !data.Terms.Any(row => row.Id == data.TermId))
            throw InvalidResponse();
        return data;
    }

    public async Task<OrganizationPreview> PreviewOrganizationImportAsync(string school, OrganizationImportDraft draft, CancellationToken ct)
    {
        var session = CurrentSession;
        var data = await RequestAsync<OrganizationPreview>(HttpMethod.Post, OrganizationImportPath(school) + "/preview", session.Token, ct, body: draft);
        CheckCreationSession(session, ct);
        VerifyOrganizationPreview(data, draft);
        return data;
    }

    public async Task<OrganizationImportResult> SaveOrganizationImportAsync(string school, OrganizationImportDraft draft,
        OrganizationPreview preview, Guid requestId, CancellationToken ct)
    {
        VerifyOrganizationPreview(preview, draft);
        var session = CurrentSession;
        var data = await RequestAsync<OrganizationImportResult>(HttpMethod.Post, OrganizationImportPath(school), session.Token, ct, body: new
        {
            draft.TermId, draft.TermStatus, draft.Grade, draft.Subjects, draft.AdministrativeClasses, draft.CourseGroups, draft.Declaration,
            draft.ArchivedCorrection, draft.TargetGradeId,
            RequestId = requestId, ExpectedVersion = preview.Version, ExpectedEvidence = preview.Evidence
        });
        CheckCreationSession(session, ct);
        if (data.State == "APPLIED" && data.Receipt is { } receipt) VerifyOrganizationReceipt(receipt, draft, requestId);
        else if (data.State is "STALE" or "BLOCKED" && data.Preview is { } observed)
        {
            VerifyOrganizationPreview(observed, draft);
            if (data.State == "STALE" && data.ChangedKeys is null) throw InvalidResponse();
        }
        else throw InvalidResponse();
        return data;
    }

    public async Task<OrganizationImportReceipt?> GetOrganizationImportReceiptAsync(string school, OrganizationImportDraft draft,
        Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var data = await RequestAsync<OrganizationImportReceipt>(HttpMethod.Get, $"{OrganizationImportPath(school)}/receipts/{requestId:D}", session.Token, ct);
            CheckCreationSession(session, ct);
            VerifyOrganizationReceipt(data, draft, requestId);
            return data;
        }
        catch (ConsoleApiException error) when (error is {StatusCode: 404, Code: "ORG_IMPORT_RECEIPT_NOT_FOUND"})
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private static void VerifyOrganizationPreview(OrganizationPreview data, OrganizationImportDraft draft)
    {
        static bool Digest(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
        if (data.TermId != draft.TermId || data.TermStatus != draft.TermStatus ||
            data.GradeCode != draft.Grade.Code.Trim().ToUpperInvariant() || !Digest(data.Version) ||
            data.Evidence.Any(row => string.IsNullOrEmpty(row.Key) || !Digest(row.Version)) ||
            data.Evidence.Select(row => row.Key).Distinct().Count() != data.Evidence.Length ||
            data.Changes.Any(row => row.Action is not ("ADD" or "CHANGE" or "REMOVE" or "KEEP" or "BLOCK")) ||
            data.CanApply == data.Changes.Any(row => row.Action == "BLOCK")) throw InvalidResponse();
    }

    private static void VerifyOrganizationReceipt(OrganizationImportReceipt receipt, OrganizationImportDraft draft, Guid requestId)
    {
        static bool Covers(string[] codes, OrganizationSavedRow[] rows) =>
            rows.All(row => !string.IsNullOrWhiteSpace(row.Id)) && rows.Select(row => row.Id).Distinct().Count() == rows.Length &&
            codes.Select(code => code.Trim().ToUpperInvariant()).Distinct().Order().SequenceEqual(rows.Select(row => row.Code).Order());
        var subjects = draft.Subjects.Select(row => row.Code).Concat(draft.CourseGroups.Select(row => row.SubjectCode))
            .Concat(draft.AdministrativeClasses.SelectMany(row => row.SubjectRules.Select(rule => rule.SubjectCode))).ToArray();
        if (receipt.RequestId != requestId || receipt.TermId != draft.TermId || receipt.TermStatus != draft.TermStatus ||
            string.IsNullOrWhiteSpace(receipt.GradeId) || draft.TargetGradeId is not null && receipt.GradeId != draft.TargetGradeId ||
            !Covers(subjects, receipt.Subjects) ||
            !Covers(draft.AdministrativeClasses.Select(row => row.Code).ToArray(), receipt.AdministrativeClasses) ||
            !Covers(draft.CourseGroups.Select(row => row.Code).ToArray(), receipt.CourseGroups)) throw InvalidResponse();
    }
}
