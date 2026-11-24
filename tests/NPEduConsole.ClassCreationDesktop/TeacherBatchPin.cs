using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using NPEduConsole.App;

internal static partial class Program
{
    private static T BatchPinElement<T>(Control page, string id) where T : Control =>
        page.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == "TeacherBatchPin" + id);

    private static bool BatchPinReady(Control page, string id) =>
        page.GetVisualDescendants().OfType<Button>().Any(control => AutomationProperties.GetAutomationId(control) == "TeacherBatchPin" + id &&
            control.IsEffectivelyVisible && control.IsEffectivelyEnabled);

    private static async Task<Button> ReviewBatchPinAsync(Control page, int expected)
    {
        await Until(() => BatchPinReady(page, "Open"), "fresh PIN results offer a batch review");
        Click(BatchPinElement<Button>(page, "Open"));
        await Until(() => BatchPinReady(page, "Save"), "the batch review is ready to save");
        var rows = BatchPinElement<ListBox>(page, "Rows").Items.OfType<TeacherPinBatchDisplay>().ToArray();
        Check(rows.Count(row => row.State == "可交付") == expected &&
            (BatchPinElement<TextBlock>(page, "Status").Text?.StartsWith($"{expected} 位将写入", StringComparison.Ordinal) ?? false),
            "the advertised CSV count agrees with the displayed eligibility");
        return BatchPinElement<Button>(page, "Save");
    }
}
