using System.Runtime.CompilerServices;
using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed class SubjectSetupFlow
{
    private static readonly ConditionalWeakTable<ConsoleClient,
        Dictionary<(string School, string Term, string Classroom), UnconfirmedSubjectSetup>> UnconfirmedBySession = new();

    private Window? _active;

    internal async Task OpenAsync(Window owner, ConsoleClient client, School school, string termId, string classId,
        string[] initiallySelected, Action<ConsoleApiException> expired)
    {
        if (_active is not null || !owner.IsVisible) return;
        var unconfirmed = UnconfirmedBySession.GetOrCreateValue(client);
        var scope = (school.Id, termId, classId);
        var setup = new SubjectSetupWindow(client, school, termId, classId, initiallySelected, unconfirmed.GetValueOrDefault(scope));
        setup.SessionExpired += expired;
        Window current = _active = setup;
        setup.Closed += SetupClosed;
        owner.Closed += OwnerClosed;
        try
        {
            await setup.ShowDialog(owner);
            if (_active != setup || !owner.IsVisible || setup.NextTeaching is not { } next) return;
            var teaching = new TeachingWindow(client, school, termId, next.ClassId, next.SubjectId);
            teaching.SessionExpired += expired;
            current = _active = teaching;
            await teaching.ShowDialog(owner);
        }
        finally
        {
            setup.Closed -= SetupClosed;
            owner.Closed -= OwnerClosed;
            if (_active == current) _active = null;
        }

        void SetupClosed(object? sender, EventArgs args)
        {
            if (_active != setup) return;
            if (setup.Pending is { } pending) unconfirmed[scope] = pending;
            else unconfirmed.Remove(scope);
            if (setup.NextTeaching is null) _active = null;
        }

        void OwnerClosed(object? sender, EventArgs args) => Close();
    }

    internal void Close()
    {
        var active = _active;
        _active = null;
        if (active is SubjectSetupWindow setup) setup.CloseForScopeChange();
        if (active is TeachingWindow teaching) teaching.CloseForScopeChange();
    }
}
