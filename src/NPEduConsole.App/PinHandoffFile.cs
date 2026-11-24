using System.Text;
using Avalonia.Platform.Storage;

namespace NPEduConsole.App;

internal static class PinHandoffFile
{
    internal static async Task<bool> WriteAsync(IStorageFile file, Encoding encoding, Func<TextWriter, Task> write,
        CancellationToken token, Func<bool>? stillCurrent = null)
    {
        string path = file.TryGetLocalPath() ?? throw new IOException();
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".np-handoff-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                await using var writer = new StreamWriter(stream, encoding);
                await write(writer);
            }
            token.ThrowIfCancellationRequested();
            if (stillCurrent?.Invoke() == false) return false;
            File.Move(temporary, path, overwrite: true);
            created = false;
            return true;
        }
        finally { if (created) File.Delete(temporary); }
    }
}
