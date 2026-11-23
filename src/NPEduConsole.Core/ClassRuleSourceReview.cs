namespace NPEduConsole.Core;

/// <summary>Tracks source removals acknowledged in one class-rule preview.</summary>
public sealed class ClassRuleSourceReview(ClassRulePreview preview)
{
    private readonly ILookup<string, ClassRuleRemoval> _bySubject = preview.Removals.ToLookup(row => row.SubjectId);
    private readonly HashSet<(string Subject, string Group)> _confirmed = [];

    public int RemainingCount => preview.Removals.Length - _confirmed.Count;

    public IEnumerable<ClassRuleRemoval> ForSubject(string? subjectId) =>
        subjectId is null ? [] : _bySubject[subjectId];

    public int RemainingFor(string? subjectId) => ForSubject(subjectId).Count(IsPending);

    public bool IsConfirmed(string subjectId, string groupId) => _confirmed.Contains((subjectId, groupId));

    public void SetConfirmed(string subjectId, string groupId, bool value)
    {
        if (!value) _confirmed.Remove((subjectId, groupId));
        else if (ForSubject(subjectId).Any(row => row.GroupId == groupId)) _confirmed.Add((subjectId, groupId));
    }

    public ClassRuleRemoval? NextUnconfirmed(string? preferredSubjectId = null) =>
        ForSubject(preferredSubjectId).FirstOrDefault(IsPending) ?? preview.Removals.FirstOrDefault(IsPending);

    public string[] ConfirmedGroupIds() =>
        preview.Removals.Where(row => IsConfirmed(row.SubjectId, row.GroupId)).Select(row => row.GroupId).ToArray();

    private bool IsPending(ClassRuleRemoval row) => !IsConfirmed(row.SubjectId, row.GroupId);
}
