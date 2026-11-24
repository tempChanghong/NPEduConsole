namespace NPEduConsole.Core;

public sealed class TeacherPinBatchScope
{
    internal TeacherPinBatchScope(Guid sessionId, string schoolId, string schoolCode, string? termId,
        string? termStatus, string? gradeId, Guid requestId)
    {
        SessionId = sessionId;
        SchoolId = schoolId;
        SchoolCode = schoolCode;
        TermId = termId;
        TermStatus = termStatus;
        GradeId = gradeId;
        RequestId = requestId;
    }

    internal Guid SessionId { get; }
    public string SchoolId { get; }
    public string SchoolCode { get; }
    public string? TermId { get; }
    public string? TermStatus { get; }
    public string? GradeId { get; }
    public Guid RequestId { get; }
}

public sealed record TeacherPinBatchProof(string AccountId, string Username, int Version);

public sealed partial class ConsoleClient
{
    private static ConsoleApiException PinBatchScopeChanged() =>
        new(409, "办理这批教师账号的学校、学期或登录状态已变化，请重新核对。", "TEACHER_PIN_BATCH_SCOPE_CHANGED");

    public TeacherPinBatchScope CaptureTeacherPinBatchScope(string schoolId, string schoolCode, string? termId,
        string? termStatus, string? gradeId, Guid requestId)
    {
        if (string.IsNullOrWhiteSpace(schoolId) || string.IsNullOrWhiteSpace(schoolCode) || requestId == Guid.Empty ||
            (termId is null ? termStatus is not null || gradeId is not null :
                string.IsNullOrWhiteSpace(termId) || termStatus is not ("ACTIVE" or "DRAFT")))
            throw PinBatchScopeChanged();
        return new(CurrentSession.Id, schoolId, schoolCode, termId, termStatus, gradeId, requestId);
    }

    public async Task<IReadOnlyList<string>> VerifyTeacherPinBatchAsync(TeacherPinBatchScope scope,
        IReadOnlyList<TeacherPinBatchProof> proofs, CancellationToken ct)
    {
        var session = CurrentSession;
        if (session.Id != scope.SessionId) throw PinBatchScopeChanged();
        var expected = proofs.ToArray();
        if (expected.Length == 0 || expected.Any(row => row is null || row.Version < 0 ||
                string.IsNullOrWhiteSpace(row.AccountId) || string.IsNullOrWhiteSpace(row.Username)) ||
            expected.Select(row => row.AccountId).Distinct().Count() != expected.Length ||
            expected.Select(row => row.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() != expected.Length)
            throw InvalidResponse();

        var catalog = await GetTeacherAllocationAsync(scope.SchoolId, scope.TermId, scope.GradeId, scope.TermId is null, ct);
        CheckCreationSession(session, ct);
        if (catalog.SchoolCode != scope.SchoolCode || catalog.Mode != "LOCAL_PIN" || catalog.TermId != scope.TermId ||
            scope.TermId is not null && !catalog.Terms.Any(term => term.Id == scope.TermId && term.Status == scope.TermStatus))
            throw PinBatchScopeChanged();

        var accounts = new Dictionary<string, AllocationAccount>(StringComparer.Ordinal);
        var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in catalog.Accounts)
        {
            if (account is null || string.IsNullOrWhiteSpace(account.Id) || !accounts.TryAdd(account.Id, account) ||
                account.Username is { Length: > 0 } username && !usernames.Add(username))
                throw InvalidResponse();
        }
        return expected.Where(row => !accounts.TryGetValue(row.AccountId, out var account) || account.Disabled ||
            !account.Manageable || account.CredentialVersion != row.Version ||
            !string.Equals(account.Username, row.Username, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.AccountId).ToArray();
    }
}

public sealed class TeacherPinCsvRow(string name, string username, string pin, string workspaces)
{
    internal string Name { get; } = name;
    internal string Username { get; } = username;
    internal string Pin { get; } = pin;
    internal string Workspaces { get; } = workspaces;
}

public static class TeacherPinCsv
{
    public static async Task WriteAsync(TextWriter writer, string schoolName, string schoolCode,
        IReadOnlyList<TeacherPinCsvRow> rows, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(schoolCode) || rows.Count == 0 || rows.Any(row =>
                row is null || string.IsNullOrWhiteSpace(row.Username) || row.Pin is not { Length: >= 4 and <= 8 } ||
                !row.Pin.All(char.IsAsciiDigit)) ||
            rows.Select(row => row.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count)
            throw new ArgumentException("请重新核对本批可交付的教师账号。");
        await writer.WriteAsync("学校,学校代码,姓名,短账号,初始PIN,本次教学空间\r\n".AsMemory(), ct);
        foreach (var row in rows)
        {
            string line = string.Join(",", SpreadsheetCsv.Cell(schoolName), SpreadsheetCsv.Cell(schoolCode),
                SpreadsheetCsv.Cell(row.Name), SpreadsheetCsv.Cell(row.Username), SpreadsheetCsv.Cell(row.Pin),
                SpreadsheetCsv.Cell(row.Workspaces)) + "\r\n";
            await writer.WriteAsync(line.AsMemory(), ct);
        }
    }

}

public static class SpreadsheetCsv
{
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var leading = value.AsSpan().TrimStart();
        if (leading.Length > 0 && leading[0] is '=' or '+' or '-' or '@' || value[0] is '\t' or '\r' or '\n')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
