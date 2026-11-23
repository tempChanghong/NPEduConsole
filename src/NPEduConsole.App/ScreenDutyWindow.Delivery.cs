using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class ScreenDutyWindow
{
    internal Func<FilePickerSaveOptions, Task<IStorageFile?>>? SelectPinFile { private get; set; }

    private static bool CurrentPinMatches(ScreenDutyDetail? screen, InitialPinDelivery<int>? delivery) =>
        delivery is not null && screen is { Active: true, HasPin: true, Room.Active: true } &&
        screen.Id == delivery.Id && screen.CredentialVersion == delivery.Version &&
        string.Equals(screen.LoginCode, delivery.Username, StringComparison.OrdinalIgnoreCase);

    private async void DeliverPinClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _closed || _delivery is not { } delivery || !CurrentPinMatches(_detail, delivery) ||
            _catalog?.SchoolCode is not { Length: > 0 } schoolCode) return;
        await RunAsync(async () =>
        {
            async Task<bool> Verify()
            {
                await ReadAsync();
                if (_closed || _delivery != delivery || !CurrentPinMatches(_detail, delivery) || _catalog?.SchoolCode != schoolCode)
                {
                    _delivery = null;
                    return false;
                }
                return true;
            }
            bool save = sender == DutySavePin;
            if (!await delivery.DeliverAsync(this, save, _school.Name, schoolCode, "大屏网页短账号", Verify, _lifetime.Token, SelectPinFile)) return;
            _delivery = null;
            if (!_closed) Notice(save ? "已另存本屏网页账号与 PIN。" : "已将本屏网页账号与 PIN 复制到剪贴板。");
        });
    }
}
