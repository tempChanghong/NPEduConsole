using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private readonly string? _lastServerFile;

    private void ChangeLoginServerClicked(object? sender, RoutedEventArgs args)
    {
        KnownServerRow.IsVisible = false;
        ServerField.IsVisible = true;
        Password.Clear();
        Dispatcher.UIThread.Post(() =>
        {
            if (!LoginView.IsEffectivelyVisible || !ServerField.IsVisible || !ServerAddress.IsEnabled) return;
            ServerAddress.Focus();
            ServerAddress.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void ShowKnownLoginServer(Uri address)
    {
        string display = address.AbsoluteUri.TrimEnd('/');
        KnownServerAddress.Text = display;
        ServerField.IsVisible = false;
        KnownServerRow.IsVisible = true;
    }

    private void RestoreLoginServer()
    {
        if (_lastServerFile is null) return;
        try
        {
            var file = new FileInfo(_lastServerFile);
            if (file.Exists && file.Length is > 0 and <= 8192)
            {
                var address = ConsoleClient.NormalizeKvAddress(File.ReadAllText(file.FullName));
                ServerAddress.Text = address.AbsoluteUri.TrimEnd('/');
                ShowKnownLoginServer(address);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ConsoleApiException)
        {
            // An unreadable or obsolete preference leaves the first-use address editor available.
        }
    }

    private void RememberLoginServer(Uri address)
    {
        if (_lastServerFile is null) return;
        string temporary = _lastServerFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_lastServerFile)!);
            File.WriteAllText(temporary, address.AbsoluteUri);
            File.Move(temporary, _lastServerFile, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not remember the Console server address ({0:X8}).", error.HResult);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
