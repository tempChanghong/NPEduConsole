using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class StudyRecordsWindow : Window
{
    public StudyRecordsWindow() => InitializeComponent();

    internal StudyRecordsWindow(ConsoleClient client, StudyEvidenceScope scope, string school, string classroom,
        DateOnly? day, StudyReportGroup? report, IReadOnlyDictionary<string, string>? devices = null, DeviceFieldScope? reportDevice = null) : this()
    {
        var page = new StudyRecordsPage(client, scope, school, classroom, day, report, devices, reportDevice);
        Content = page;
        page.ReturnRequested += Close;
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            await page.StartAsync();
        };
        Closed += (_, _) => page.End();
    }
}
