using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TermPreparationWindow
{
    private TermStatusWindow? _termStatusWindow;
    private TermClassManagementWindow? _termClassesWindow;
    private GradeTeachingPlanWindow? _termTeachingWindow;
    private ManagedAcademicTerm[] CatalogTerms => _catalog?.Terms ??
        (_catalog is { } catalog ? (catalog.ActiveTerm is { } active ? [active, ..catalog.Drafts] : catalog.Drafts) : []);

    private void TermFilterChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_client is null || PreparedTerms is null || _catalog is null) return;
        ApplyTermFilter();
        Render();
    }

    private void ApplyTermFilter()
    {
        string? status = TermFilter.SelectedIndex switch {1 => "ACTIVE", 2 => "DRAFT", 3 => "ARCHIVED", _ => null};
        var filtered = CatalogTerms.Where(term => status is null || status == term.Status).ToArray();
        var rows = filtered.Select((term, index) => term with {StartsYear = index == 0 || filtered[index - 1].AcademicYear != term.AcademicYear}).ToArray();
        _binding = true;
        PreparedTerms.ItemsSource = rows;
        PreparedTerms.SelectedItem = rows.FirstOrDefault(term => term.Id == _selectedTermId) ??
            rows.FirstOrDefault(term => term.Id == _activeTerm?.Id) ?? rows.FirstOrDefault();
        _selectedTermId ??= SelectedTerm?.Id;
        _binding = false;
        DraftListEmpty.Text = rows.Length == 0 ? "此筛选下没有学期。" : null;
        DraftListEmpty.IsVisible = rows.Length == 0;
        PreparedTerms.IsVisible = rows.Length > 0;
    }

    private void RenderTermDirectory(bool editable)
    {
        TermFilter.IsEnabled = editable && _catalog?.Terms is not null;
        var term = SelectedTerm;
        bool supportsStatus = _catalog?.Terms is not null;
        bool pendingStatus = TermStatusWindow.HasPending(_client, _school.Id);
        ViewedTermName.Text = term?.Name ?? "选择本校学期查看";
        ViewedTermDetails.Text = term is null ? null : $"{term.Period} · {term.StatusText}\n{term.Dates}";
        TermManageClasses.IsVisible = term is not null;
        TermViewDiagnostics.IsVisible = term is not null && OpenTermDiagnostics is not null;
        TermViewDiagnostics.IsEnabled = editable;
        TermPrepareClasses.IsVisible = _page == Page.Check && _check?.Term.Status == "DRAFT";
        TermManageClasses.IsEnabled = TermPrepareClasses.IsEnabled = editable;
        TermManageTeaching.IsVisible = TermManageClasses.IsVisible;
        TermPrepareTeaching.IsVisible = TermPrepareClasses.IsVisible;
        TermManageTeaching.IsEnabled = TermPrepareTeaching.IsEnabled = editable && _termTeachingWindow is null;
        TermStatusActivate.IsVisible = supportsStatus && term?.Status is "DRAFT" or "ARCHIVED";
        TermStatusActivate.Content = term?.Status == "ARCHIVED" ? "重新启用" : "预览启用";
        TermMoreActions.IsVisible = supportsStatus && term is not null;
        TermStatusDraft.IsVisible = term?.Status is "ACTIVE" or "ARCHIVED";
        TermStatusArchive.IsVisible = term?.Status is "ACTIVE" or "DRAFT";
        TermStatusActivate.IsEnabled = TermMoreActions.IsEnabled = editable && !pendingStatus;
        TermActivationOptions.IsVisible = supportsStatus && _page == Page.Check && _check is not null && PendingActivation is null;
        TermActivationOptions.IsEnabled = editable && !pendingStatus;
        TermStatusReceiptButton.IsVisible = supportsStatus && (pendingStatus || _catalog?.LatestStatusRequestId is not null);
        TermStatusReceiptButton.Content = pendingStatus ? "核对未确认操作" : "最近一次学期操作";
        TermStatusReceiptButton.IsEnabled = editable;
        var history = (_catalog?.StatusHistory ?? []).Where(row => row.TermId == term?.Id).ToArray();
        if (TermStatusHistoryPicker.ItemsSource is not IEnumerable<TermStatusHistoryEntry> previous || !previous.SequenceEqual(history))
        {
            var prior = (TermStatusHistoryPicker.SelectedItem as TermStatusHistoryEntry)?.RequestId;
            TermStatusHistoryPicker.ItemsSource = history;
            TermStatusHistoryPicker.SelectedItem = history.FirstOrDefault(row => row.RequestId == prior) ?? history.FirstOrDefault();
        }
        TermStatusHistoryPicker.IsVisible = TermStatusHistoryOpen.IsVisible = history.Length > 0;
        TermStatusHistoryOpen.IsEnabled = editable && !pendingStatus;
        if (pendingStatus && _page == Page.Check && PendingActivation is null) TermPrimary.IsEnabled = false;
    }

    private async void TermManageClassesClicked(object? sender, RoutedEventArgs args) =>
        await OpenTermClassesAsync(SelectedTerm, TermManageClasses);

    internal Func<Window, ManagedAcademicTerm, Task>? OpenTermDiagnostics { get; init; }

    private async void TermDiagnosticsClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _closed || SelectedTerm is not { } term || OpenTermDiagnostics is not { } open) return;
        await RunAsync(async () =>
        {
            await open(this, term);
            if (!_closed) await LoadCatalogAsync();
        });
        if (!_closed) Focus(TermViewDiagnostics);
    }

    private async void TermPrepareClassesClicked(object? sender, RoutedEventArgs args) =>
        await OpenTermClassesAsync(_check?.Term, TermPrepareClasses);

    private async void TermManageTeachingClicked(object? sender, RoutedEventArgs args) =>
        await OpenTermTeachingAsync(SelectedTerm, TermManageTeaching);

    private async void TermPrepareTeachingClicked(object? sender, RoutedEventArgs args) =>
        await OpenTermTeachingAsync(_check?.Term, TermPrepareTeaching);

    private async Task OpenTermTeachingAsync(ManagedAcademicTerm? term, Button source)
    {
        if (term is null || _busy || _closed || _termTeachingWindow is not null) return;
        var window = new GradeTeachingPlanWindow(_client, _school, null, null, null, term.Id) { SessionExpired = SessionExpired };
        _termTeachingWindow = window;
        TermManageTeaching.IsEnabled = TermPrepareTeaching.IsEnabled = false;
        try { await window.ShowDialog(this); }
        finally { _termTeachingWindow = null; }
        if (_closed) return;
        if (_page == Page.Check && window.WorkspaceChanged) await RunAsync(() => ReadCheckAsync(term.Id));
        Render();
        Focus(source);
    }

    private async Task OpenTermClassesAsync(ManagedAcademicTerm? term, Button source)
    {
        if (term is null || _busy || _closed || _termClassesWindow is not null) return;
        var window = new TermClassManagementWindow(_client, _school, term) { SessionExpired = SessionExpired };
        _termClassesWindow = window;
        TermManageClasses.IsEnabled = TermPrepareClasses.IsEnabled = false;
        try { await window.ShowDialog(this); }
        finally { _termClassesWindow = null; }
        if (_closed) return;
        await RunAsync(_page == Page.Check ? () => ReadCheckAsync(term.Id) : LoadCatalogAsync);
        Focus(source);
    }

    private async void StatusActivateClicked(object? sender, RoutedEventArgs args) => await OpenStatusWindowAsync(SelectedTerm, "ACTIVATE");
    private async void DraftStatusActivateClicked(object? sender, RoutedEventArgs args) => await OpenStatusWindowAsync(_check?.Term, "ACTIVATE");
    private async void StatusArchiveClicked(object? sender, RoutedEventArgs args) => await OpenStatusWindowAsync(SelectedTerm, "ARCHIVE");
    private async void StatusDraftClicked(object? sender, RoutedEventArgs args) => await OpenStatusWindowAsync(SelectedTerm, "DRAFT");
    private async void StatusReceiptClicked(object? sender, RoutedEventArgs args) => await OpenStatusWindowAsync(null, null, _catalog?.LatestStatusRequestId);
    private async void StatusHistoryClicked(object? sender, RoutedEventArgs args)
    {
        if (TermStatusHistoryPicker.SelectedItem is TermStatusHistoryEntry entry)
            await OpenStatusWindowAsync(null, null, entry.RequestId);
    }

    private async Task OpenStatusWindowAsync(ManagedAcademicTerm? term, string? action, Guid? receipt = null)
    {
        if (_closed || _busy || _termStatusWindow is not null || _catalog?.Terms is null) return;
        var view = new TermStatusWindow(_client, _school, _activeTerm, term, action, receipt)
        {
            AuthorizationLost = error =>
            {
                if (_closed) return;
                ClearSchoolScope();
                if (error.StatusCode == 401) SessionExpired?.Invoke(error);
            },
            OpenPlacement = OpenPlacement,
        };
        _termStatusWindow = view;
        try { await view.ShowDialog(this); }
        finally { if (_termStatusWindow == view) _termStatusWindow = null; }
        if (_closed) return;
        if (view.ObservedWrite) ActivationObserved = true;
        if (view.Destination is { } destination)
        {
            ActivationDestination = destination;
            _allowClose = true;
            Close();
            return;
        }
        if (view.ResultTermId is { } termId) _selectedTermId = termId;
        if (view.ObservedWrite && _page == Page.Check)
        {
            _dirty = false;
            SetPage(Page.Directory);
        }
        await RunAsync(LoadCatalogAsync);
        if (!_closed) Focus(_page == Page.Check ? TermActivationOptions : SelectedTerm is null ? TermFilter : PreparedTerms);
    }
}
