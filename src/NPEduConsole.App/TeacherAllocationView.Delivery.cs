using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TeacherAllocationView
{
    internal Func<FilePickerSaveOptions, Task<IStorageFile?>>? SelectPinFile { private get; set; }

    internal static bool TeacherPinMatches(TeacherAllocationCatalog? catalog, InitialPinDelivery<int> delivery) =>
        catalog is {Mode: "LOCAL_PIN", SchoolCode.Length: > 0} &&
        catalog.Accounts.Any(row => row.Id == delivery.Id && row.Manageable && !row.Disabled &&
            row.CredentialVersion == delivery.Version && string.Equals(row.Username, delivery.Username, StringComparison.OrdinalIgnoreCase));

    private void RenderPinDelivery(AllocationSavedTeacher? teacher)
    {
        var delivery = teacher?.AccountId is { } id ? _deliveries.GetValueOrDefault(id) : null;
        bool canDeliver = delivery is not null && TeacherPinMatches(_catalog, delivery);
        AllocationCopyPin.IsVisible = AllocationSavePin.IsVisible = canDeliver;
        AllocationCopyPin.IsEnabled = AllocationSavePin.IsEnabled = !_busy;
        AllocationPinHint.IsVisible = teacher is not null && !_archivedCorrection;
        AllocationPinHint.Text = teacher is null ? "" : Mode switch
        {
            "SHARED_PASSWORD" => "凭学校代码、短账号和学校发放的通用口令登录。",
            "OAUTH_EMAIL" => "请使用分配时登记的可信邮箱，从学校网页登录。",
            _ when !teacher.Created => "已有账号沿用原凭据。",
            _ when canDeliver => $"可交付 {teacher.Username} 的本次初始 PIN，离开结果页后清除。",
            _ => "账号已创建。本页没有可再次交付的 PIN。如需新 PIN，可在教师账号页重置。"
        };
    }

    private async void DeliverPinClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _stage != Stage.Receipt || _catalog is not {SchoolCode: { } schoolCode} catalog ||
            AllocationReceiptRows.SelectedItem is not ReceiptRow {Teacher.AccountId: { } id} ||
            !_deliveries.TryGetValue(id, out var delivery) || !TeacherPinMatches(catalog, delivery)) return;
        await RunAsync(async () =>
        {
            async Task<bool> Verify()
            {
                var current = await _client.GetTeacherAllocationAsync(_school.Id, catalog.TermId, catalog.GradeId,
                    catalog.TermId is null, _lifetime.Token);
                if (_closed || _stage != Stage.Receipt || _deliveries.GetValueOrDefault(id) != delivery) return false;
                if (!TeacherPinMatches(current, delivery) || current.SchoolCode != schoolCode)
                {
                    _deliveries.Remove(id);
                    return false;
                }
                return AllocationReceiptRows.SelectedItem is ReceiptRow row && row.Teacher.AccountId == id;
            }
            bool save = sender == AllocationSavePin;
            if (!await delivery.DeliverAsync(HostWindow, save, _school.Name, schoolCode, "教师短账号", Verify, _lifetime.Token, SelectPinFile)) return;
            _deliveries.Remove(id);
            if (!_closed) Status(save ? "已另存所选教师的账号与 PIN。" : "已将所选教师的账号与 PIN 复制到剪贴板。");
        });
    }
}
