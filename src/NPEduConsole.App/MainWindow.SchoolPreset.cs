using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private async Task ShowSchoolPresetAsync(Control anchor)
    {
        if (_client is not { } client || _school is not { } school ||
            _page != ShellPage.Devices || _academicPages.Count != 0) return;

        var view = new SchoolPresetExportView();
        using var lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        SchoolAccessPresetFile? preset = null;
        bool busy = false;
        bool IsCurrent() => !token.IsCancellationRequested && ReferenceEquals(client, _client) &&
            ReferenceEquals(school, _school) && ShowsAcademicPage(view);

        void ShowApiError(ConsoleApiException error)
        {
            if (!IsCurrent() || ReturnToLoginIfExpired(error)) return;
            preset = null;
            view.ShowError(error.StatusCode == 403 ? "当前账号无法读取本校资料，请核对学校和权限。" :
                "暂时无法读取本校资料。请重试。");
        }

        async Task LoadAsync()
        {
            if (busy || !IsCurrent()) return;
            busy = true;
            preset = null;
            view.SetLoading();
            try
            {
                var current = await client.ReadSchoolAccessPresetAsync(school.Id, token);
                if (!IsCurrent()) return;
                preset = current;
                view.ShowPreset(current.SchoolName, current.SchoolCode ?? "未提供", current.BackendUrl);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (ConsoleApiException error) { ShowApiError(error); }
            catch (IOException error) { if (IsCurrent()) view.ShowError(error.Message); }
            finally { busy = false; }
        }

        async Task SaveAsync()
        {
            if (busy || preset is not { } shown || !IsCurrent()) return;
            busy = true;
            view.SetSaving(true);
            view.ShowFeedback(null);
            try
            {
                using var picked = await StorageProvider.SaveFilePickerAsync(new()
                {
                    Title = "导出教室接入配置",
                    SuggestedFileName = shown.SuggestedFileName,
                    DefaultExtension = "np-school.json",
                    FileTypeChoices = [new("学校接入配置") { Patterns = ["*.np-school.json"] }],
                    ShowOverwritePrompt = false
                });
                if (picked is null || !IsCurrent()) return;
                string path = picked.TryGetLocalPath() ?? throw new IOException("请选择本机或 U 盘上的文件位置。");
                var current = await client.ReadSchoolAccessPresetAsync(school.Id, token);
                if (!IsCurrent()) return;
                if (current != shown)
                {
                    preset = current;
                    view.ShowPreset(current.SchoolName, current.SchoolCode ?? "未提供", current.BackendUrl);
                    view.ShowFeedback("学校资料已更新。请核对后重新导出。");
                    return;
                }
                await current.ExportAsync(path, token);
                if (IsCurrent()) view.ShowFeedback("配置已导出。请在教室端的 NPDownloader 中导入。");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (ConsoleApiException error) { ShowApiError(error); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
                System.Runtime.InteropServices.ExternalException)
            {
                if (IsCurrent()) view.ShowFeedback(error is IOException ? error.Message : "无法保存配置，请核对文件位置后重试。");
            }
            finally
            {
                busy = false;
                if (IsCurrent()) view.SetSaving(false);
            }
        }

        view.CloseRequested += () => ReturnFromAcademicPage(view);
        view.SaveRequested += async () => await SaveAsync();
        view.ReloadRequested += async () => await LoadAsync();
        var exit = PushAcademicPage(view, () => true, lifetime.Cancel);
        await LoadAsync();
        if (await exit) RestoreDevicePolicyEntry(client, school, anchor);
    }
}
