using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TeacherAllocationView
{
    private Window? _retryPinDialog;

    private static int[] RequiredRetryPins(PendingRequest request)
    {
        if (request.Draft.Mode != "LOCAL_PIN" || request.Draft.ArchivedCorrection) return [];
        var newAccounts = request.Preview.Changes.Where(row => row.Action == "ADD" && row.WorkspaceId is null)
            .Select(row => row.Path).ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(0, request.Draft.Teachers.Length).Where(index => newAccounts.Contains($"teachers[{index}]")).ToArray();
    }

    private async Task RetryOriginalAsync(PendingRequest request)
    {
        if (_retryPinDialog is not null) return;
        var pinRows = RequiredRetryPins(request);
        if (pinRows.Length == 0)
        {
            await SubmitAsync(request, request.Draft);
            return;
        }
        // A late original response may have become authoritative since the previous check.
        await RunAsync(() => VerifyAsync(request));
        if (_closed || !_retryMissingReceipt || Pending?.Id != request.Id) return;

        var draft = await ReadRetryPinsAsync(request.Draft, pinRows);
        if (!_closed && draft is not null && Pending?.Id == request.Id) await SubmitAsync(request, draft);
        else if (!_closed && Pending?.Id == request.Id) FocusLater(AllocationSave);
    }

    private async Task<TeacherAllocationDraft?> ReadRetryPinsAsync(TeacherAllocationDraft original, int[] pinRows)
    {
        var fields = new StackPanel { Spacing = 8 };
        var inputs = new List<(int Index, TextBox Input)>();
        foreach (var index in pinRows)
        {
            var teacher = original.Teachers[index];
            var name = $"{teacher.Name}（{teacher.Username}）";
            var input = new TextBox { PasswordChar = '●', MaxLength = 8, PlaceholderText = "4 至 8 位数字" };
            AutomationProperties.SetName(input, $"重新输入 {name} 的初始 PIN");
            fields.Children.Add(new TextBlock { Text = name, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            fields.Children.Add(input);
            inputs.Add((index, input));
        }

        var notice = Description("请重新输入原先为这些新账号设置的初始 PIN。若原请求已经写入，服务器会返回原回执，原 PIN 保持有效。");
        var issue = new TextBlock { IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(issue, AutomationLiveSetting.Polite);
        var cancel = new Button { Content = "取消", Classes = { "action" } };
        var retry = new Button { Content = "按原编号重试", Classes = { "action", "accent" } };
        var dialog = new Window
        {
            Title = "确认原请求的初始 PIN", Width = 490, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 8,
                Children =
                {
                    notice, new ScrollViewer { MaxHeight = 240, Content = fields }, issue,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 6, Children = { cancel, retry }
                    }
                }
            }
        };
        cancel.Click += (_, _) => dialog.Close(false);
        retry.Click += (_, _) =>
        {
            foreach (var (_, input) in inputs)
            {
                if (input.Text is { Length: >= 4 and <= 8 } pin && pin.All(char.IsAsciiDigit)) continue;
                issue.Text = "请为每个新账号填写 4 至 8 位数字的初始 PIN。";
                issue.IsVisible = true;
                input.Focus();
                return;
            }
            dialog.Close(true);
        };
        dialog.Opened += (_, _) => inputs[0].Input.Focus();
        dialog.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            dialog.Close(false);
        };
        _retryPinDialog = dialog;
        try
        {
            if (!await dialog.ShowDialog<bool>(HostWindow) || _closed) return null;
            var teachers = original.Teachers.ToArray();
            foreach (var (index, input) in inputs) teachers[index] = teachers[index] with { Pin = input.Text };
            return original with { Teachers = teachers };
        }
        finally
        {
            foreach (var (_, input) in inputs) input.Clear();
            _retryPinDialog = null;
        }
    }
}
