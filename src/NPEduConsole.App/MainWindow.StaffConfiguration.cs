using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private StaffConfigurationRequests? _staffConfigurationRequests;

    private async Task<bool> ShowStaffConfigurationAsync(ConsoleClient client, School school, string? termId, StaffConfigurationDocument? document = null)
    {
        if (_client != client || _school != school) return false;
        StaffConfigurationView view = null!;
        view = CreateStaffConfigurationView(client, school, termId, document, () => ShowsAcademicPage(view));
        view.ReturnRequested = () => ReturnFromAcademicPage(view);
        var exit = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        return await exit && view.Applied;
    }

    private StaffConfigurationView CreateStaffConfigurationView(ConsoleClient client, School school, string? termId,
        StaffConfigurationDocument? document, Func<bool> visible, Window? owner = null)
    {
        var directory = _lastServerFile is null ? null : Path.Combine(Path.GetDirectoryName(_lastServerFile)!, "staff-configuration-requests");
        var view = new StaffConfigurationView(client, school, termId, _staffConfigurationRequests ??= new(directory), document)
        {
            AccessLost = error => AcademicScopeUnavailable(error, client, school), ResolveIssue = ResolveAsync
        };
        return view;

        async Task ResolveAsync(StaffConfigurationIssue issue, StaffConfigurationTeacher? teacher, string term, CancellationToken ct)
        {
            if (!Current() || issue.TargetId is not { } target) return;
            var current = await client.GetStaffConfigurationAsync(school.Id, ct);
            if (!Current()) return;
            if (current.Term is not { } active || active.Id != term)
                throw new ConsoleApiException(409, "启用学期已变化，请从开学配置重新进入。");
            if (issue.Kind == "TEACHING" && issue.SubjectId is { } subject)
            {
                var catalog = await client.GetTeacherAllocationAsync(school.Id, term, null, false, ct);
                if (!Current()) return;
                var room = catalog.Workspaces.SingleOrDefault(row => row.Id == target);
                if (room is null) throw new ConsoleApiException(409, "当前学期已找不到这间教学空间，请重新核对方案。");
                if (owner is null)
                    await ShowTeachingPageAsync(client, school, term, target, subject, teacher?.AccountId,
                        courseGroup: room.Type == "COURSE_GROUP", termName: active.Name);
                else
                    await ShowTeachingAsync(client, school, term, target, subject, teacher?.AccountId, room.Type == "COURSE_GROUP", owner);
            }
            else if (issue.Kind is "GRADE_DUTY" or "CLASS_DUTY")
                await ShowStaffResponsibilitiesAsync(client, school, term, issue.Kind == "GRADE_DUTY" ? "grades" : "classes", target,
                    teacher?.AccountId, owner, currentTermName: active.Name);
        }

        bool Current() => _client == client && _school == school && visible();
    }
}
