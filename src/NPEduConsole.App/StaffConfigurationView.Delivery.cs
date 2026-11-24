using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class StaffConfigurationView
{
    private void ClearStaffPins()
    {
        _pins.Clear();
        StaffPlanPinBatch.Clear();
    }

    private void BindStaffPins(StaffConfigurationReceipt receipt)
    {
        if (_pins.Count == 0 || _scope is not { Mode: "LOCAL_PIN", Term: { } term } scope || term.Id != receipt.TermId) return;
        var batch = _client.CaptureTeacherPinBatchScope(_school.Id, scope.SchoolCode, receipt.TermId, "ACTIVE", null, receipt.RequestId);
        var people = receipt.Teachers.Select(teacher => new TeacherPinBatchPerson(teacher.AccountId,
            teacher.Name, teacher.Username, string.Join("、", (teacher.Changes ?? []).Where(change => change.Kind == "ACCESS")
                .Select(change => change.Label)), teacher.Created)).ToArray();
        StaffPlanPinBatch.Bind(batch, _school.Name, $"{_school.Name} · {term.Name}（启用中）", people, _pins);
    }

    private async void ReadBatchWebsite()
    {
        await RunAsync(ct => StaffPlanPinBatch.ReadWebsiteAsync(_client, ct));
        if (!_closed) StaffPlanPinBatch.FocusReviewReturn();
    }

    private async void SaveBatchPins()
    {
        if (_receipt is null || !StaffPlanPinBatch.Reviewing || TopLevel.GetTopLevel(this) is not Window owner) return;
        bool saved = false;
        await RunAsync(async ct => saved = await StaffPlanPinBatch.SaveAsync(_client, owner, SelectBatchPinFile, ct));
        if (_closed) return;
        if (saved) FocusLater(Compact && _detail ? StaffPlanBackList : StaffPlanRows);
        else if (StaffPlanPinBatch.Reviewing) StaffPlanPinBatch.FocusAfterFilePicker();
    }
}
