using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace NPEduConsole.App;

// Only the originating form owns this value. It is never part of a receipt or a navigation result.
internal sealed class InitialPinDelivery<TVersion>(string id, string username, string pin, TVersion version)
{
    public string Id { get; } = id;
    public string Username { get; } = username;
    public TVersion Version { get; } = version;

    internal async Task<bool> DeliverAsync(Window owner, bool save, string schoolName, string schoolCode,
        string kind, Func<Task<bool>> stillAuthorized, CancellationToken token,
        Func<FilePickerSaveOptions, Task<IStorageFile?>>? pickFile)
    {
        try
        {
            var select = pickFile ?? owner.StorageProvider.SaveFilePickerAsync;
            using var file = save ? await select(new FilePickerSaveOptions
            {
                Title = "另存本次账号与 PIN", SuggestedFileName = "账号交付.txt", DefaultExtension = "txt",
                FileTypeChoices = [new("文本文件") { Patterns = ["*.txt"] }], ShowOverwritePrompt = true
            }) : null;
            if (save && file is null) return false;
            token.ThrowIfCancellationRequested();
            bool current = await stillAuthorized();
            token.ThrowIfCancellationRequested();
            if (!current) throw new ArgumentException("账号、凭据或办理范围已经变化，请重新核对。");
            string content = $"{schoolName}\r\n学校代码 {schoolCode}\r\n{kind} {Username}\r\nPIN {pin}";
            if (file is null)
            {
                var clipboard = owner.Clipboard ?? throw new IOException();
                await clipboard.SetTextAsync(content);
            }
            else
            {
                string path = file.TryGetLocalPath() ?? throw new IOException();
                string temp = Path.Combine(Path.GetDirectoryName(path)!, $".np-handoff-{Guid.NewGuid():N}.tmp");
                bool created = false;
                try
                {
                    await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        created = true;
                        await using var writer = new StreamWriter(stream);
                        await writer.WriteAsync(content.AsMemory(), token);
                    }
                    token.ThrowIfCancellationRequested();
                    File.Move(temp, path, overwrite: true);
                }
                finally { if (created) File.Delete(temp); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            token.ThrowIfCancellationRequested();
            throw new ArgumentException("交付未完成，请核对文件位置或剪贴板后重试。");
        }
        return true;
    }
}
