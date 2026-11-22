namespace NPEduConsole.Core;

public sealed record OrganizationSchool(string Id, string Code, string Name);
public sealed record OrganizationTerm(string Id, string Name, string Status, int AcademicYear, int Semester)
{
    public override string ToString() => $"{Name} · {Status switch { "DRAFT" => "草稿", "ARCHIVED" => "已归档", _ => "当前学期" }}";
}
public sealed record OrganizationGrade(string Code, string Name, int SortOrder);
public sealed record OrganizationGradeOption(string Id, string Code, string Name, int SortOrder)
{
    public override string ToString() => $"{Name}（{Code}）";
}
public sealed record OrganizationSubject(string Code, string Name, string Category = "OTHER", int SortOrder = 0)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }
}
public sealed record OrganizationClassOption(string Id, string Code, string Name, string? GradeId);
public sealed record OrganizationImportCatalog(OrganizationSchool School, OrganizationTerm[] Terms, string? TermId,
    OrganizationGradeOption[] Grades, OrganizationSubject[] Subjects, OrganizationClassOption[] AdministrativeClasses);
public sealed record OrganizationSubjectRule(string SubjectCode, string DeliveryMode, bool IsCompulsory);
public sealed record OrganizationClass(string Code, string Name, bool? IsStudentSelectable, bool ReplaceSubjectRules,
    OrganizationSubjectRule[] SubjectRules);
public sealed record OrganizationGroup(string Code, string Name, string SubjectCode, bool? IsStudentSelectable,
    bool ReplaceSources, string[] SourceClassCodes);
public sealed record OrganizationDeclaration(string? SchoolId = null, string? SchoolCode = null, string? SchoolName = null,
    string? TermId = null, int? AcademicYear = null, int? Semester = null, string? TermName = null,
    string? GradeCode = null, string? GradeName = null);
public sealed record OrganizationImportDocument(OrganizationGrade Grade, OrganizationSubject[] Subjects,
    OrganizationClass[] AdministrativeClasses, OrganizationGroup[] CourseGroups, OrganizationDeclaration? Declaration);
public sealed record OrganizationImportDraft(string TermId, string TermStatus, OrganizationGrade Grade,
    OrganizationSubject[] Subjects, OrganizationClass[] AdministrativeClasses, OrganizationGroup[] CourseGroups,
    OrganizationDeclaration? Declaration = null, bool ArchivedCorrection = false, string? TargetGradeId = null);
public sealed record OrganizationEvidence(string Key, string Version);
public sealed record OrganizationDifference(string Key, string Path, string Action, string Object, string? Current,
    string? After, string? Message, bool NeedsReview = false)
{
    public string ActionLabel => NeedsReview ? "需复核" : Action switch
    {
        "ADD" => "新增", "CHANGE" => "修改", "REMOVE" => "移除", "BLOCK" => "阻断", _ => "一致"
    };
    public string BeforeText => Current ?? "—";
    public string AfterText => After ?? "—";
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public string NarrowDetails => Path.StartsWith("declaration.", StringComparison.Ordinal) ?
        $"所选目标：{BeforeText}　文件声明：{AfterText}" : $"现在：{BeforeText}　→　导入后：{AfterText}";
}
public sealed record OrganizationPreview(string TermId, string TermStatus, string GradeCode, bool CanApply,
    string Version, OrganizationEvidence[] Evidence, OrganizationDifference[] Changes);
public sealed record OrganizationSavedRow(string Code, string Id);
public sealed record OrganizationImportReceipt(Guid RequestId, string TermId, string TermStatus, string GradeId,
    OrganizationSavedRow[] Subjects, OrganizationSavedRow[] AdministrativeClasses, OrganizationSavedRow[] CourseGroups);
public sealed record OrganizationImportResult(string State, OrganizationImportReceipt? Receipt = null,
    OrganizationPreview? Preview = null, string[]? ChangedKeys = null);
