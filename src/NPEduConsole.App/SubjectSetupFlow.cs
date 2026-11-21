using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed class SubjectSetupFlow
{
    private Window? _active;

    internal async Task OpenAsync(Window owner, ConsoleClient client, School school, string termId, string classId,
        string[] initiallySelected, Action<ConsoleApiException> expired)
    {
        if (_active is not null || !owner.IsVisible) return;
        var setup = new SubjectSetupWindow(client, school, termId, classId, initiallySelected);
        setup.SessionExpired += expired;
        Window current = _active = setup;
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
            owner.Closed -= OwnerClosed;
            if (_active == current) _active = null;
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
