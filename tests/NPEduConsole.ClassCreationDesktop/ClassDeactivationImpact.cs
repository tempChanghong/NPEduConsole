using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    /// <summary>
    /// 停用影响摘要的局部回归：不连 KV、不写库，只检查 user control 的四种状态与重点计数。
    /// 必须在 UI 线程调用；若 Avalonia 尚未启动，会自己起一个桌面应用跑完再退出。
    /// </summary>
    private static void VerifyClassDeactivationImpactView()
    {
        if (Application.Current is not null)
        {
            VerifyClassDeactivationImpactStates();
            return;
        }

        AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(_ => Dispatcher.UIThread.Post(() =>
            {
                int exitCode = 0;
                try { VerifyClassDeactivationImpactStates(); }
                catch (Exception error) { Console.WriteLine("FAIL: " + error); exitCode = 1; }
                finally
                {
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                        desktop.Shutdown(exitCode);
                }
            }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static void VerifyClassDeactivationImpactStates()
    {
        var view = new ClassDeactivationImpactView();
        var host = new Window { Width = 420, Height = 640, Title = "停用影响摘要核对", Content = view };
        host.Show();
        try
        {
            T Named<T>(string name) where T : Control =>
                view.FindControl<T>(name) ??
                view.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name) ??
                throw new InvalidDataException("缺少控件 " + name);

            var status = Named<TextBlock>("ImpactStatus");
            var message = Named<TextBlock>("ImpactMessage");
            var keyPanel = Named<Border>("ImpactKeyPanel");
            var keySummary = Named<TextBlock>("ImpactKeySummary");
            var counts = Named<StackPanel>("ImpactCounts");
            var note = Named<TextBlock>("ImpactNote");

            // 按标签定位计数行：行序或分组调整时不会误报，缺行时直接失败。
            string RowText(string label)
            {
                foreach (var child in counts.Children)
                {
                    string[] texts = child is TextBlock single
                        ? new[] { single.Text ?? "" }
                        : child is Panel panel
                            ? panel.Children.OfType<TextBlock>().Select(text => text.Text ?? "").ToArray()
                            : Array.Empty<string>();
                    if (texts.Length > 0 && texts[0] == label)
                        return string.Join(" ", texts.Where(text => text.Length > 0));
                }
                throw new InvalidDataException("缺少计数行 " + label);
            }

            // 1. null 表示仍在读取。
            view.SetImpact(null);
            Check(status.Text == "正在读取停用影响…" && !message.IsVisible && !keyPanel.IsVisible && !note.IsVisible &&
                counts.Children.Count == 0, "null 只显示读取中，不出现计数、重点或说明");

            // 2. error 表示读取失败；此时忽略 counts。
            view.SetImpact(null, "学校服务暂时不可用。");
            Check(status.Text == "无法读取停用影响" && message.IsVisible && message.Text!.Contains("学校服务暂时不可用") &&
                status.Classes.Contains("impact-error") && counts.Children.Count == 0 && !keyPanel.IsVisible && !note.IsVisible,
                "读取失败只显示错误，不显示或残留任何计数");

            // 3. 全部为 0 是成功状态，不是失败也不是加载。
            view.SetImpact(new ClassDeactivationCounts(0, 0, 0, 0, 0, 0, 0, 0, 0));
            Check(status.Text == "没有关联记录。" && !status.Classes.Contains("impact-error") &&
                message.IsVisible && message.Text!.Contains("均为 0") && counts.Children.Count == 0 &&
                !keyPanel.IsVisible && note.IsVisible, "全零显示为无关联成功态，并保留说明");

            // 4. 重点项：学生、大屏、任课、生效作业先汇总，再列全部计数。
            var active = new ClassDeactivationCounts(WorkspaceMembers: 3, TeachingAssignments: 2, PendingInvitations: 1,
                PublicationHistory: 7, ActivePublications: 1, ScreenBindings: 2, Students: 34, Leaderships: 2, SourceLinks: 1);
            view.SetImpact(active);
            Check(status.Text!.Contains("仍在使用中") && keyPanel.IsVisible && !message.IsVisible &&
                keySummary.Text!.Contains("学生 34 人") && keySummary.Text.Contains("大屏绑定 2 台") &&
                keySummary.Text.Contains("任课安排 2 条") && keySummary.Text.Contains("生效中的作业或通知 1 条") &&
                counts.Children.Count == 10 && note.IsVisible, "重点关联先汇总为一行，其余按类别逐条列出");

            Check(RowText("学生").Contains("34 人") && RowText("大屏绑定").Contains("2 台") &&
                RowText("任课安排").Contains("2 条") && RowText("生效中的作业或通知").Contains("1 条") &&
                RowText("班级成员").Contains("3 个") &&
                counts.Children.OfType<TextBlock>().Any(text => text.Text == "其他关联"),
                "计数行按标签可定位，重点项与单位保持稳定");

            // 5. 只有历史记录时不宣称仍在使用中，也不把历史数据说成设备撤权依据。
            view.SetImpact(new ClassDeactivationCounts(0, 0, 0, 5, 0, 0, 0, 0, 0));
            Check(status.Text == "该班没有学生、大屏、任课或生效作业，但仍有其他关联记录。" && !keyPanel.IsVisible &&
                counts.Children.Count == 10 && RowText("作业与通知发布记录").Contains("5 条"),
                "只有历史记录时给出单独的说明，不冒充仍在使用中");

            // 6. 之后的错误必须清掉先前展示的计数，避免把旧数据当成新状态。
            view.SetImpact(active, "账号权限已变化。");
            string errorText = message.Text ?? "";
            Check(status.Text == "无法读取停用影响" && counts.Children.Count == 0 && !keyPanel.IsVisible &&
                !note.IsVisible && message.IsVisible && message.TextWrapping == TextWrapping.Wrap &&
                errorText.Contains("账号权限已变化") && errorText.Contains('\n'),
                "读取失败会清空上一次的计数，错误文本显式换行且可折行");

            // 7. 可以回到加载态并再次成功渲染。
            view.SetImpact(null);
            Check(status.Text == "正在读取停用影响…" && counts.Children.Count == 0 &&
                !status.Classes.Contains("impact-error"), "重新加载会清空错误样式与计数");
            Check(AutomationName(view) == "停用影响摘要", "控件带有稳定的可访问名称");

            Console.WriteLine("PASS: 停用影响摘要覆盖加载、读取失败、全零无关联、重点计数、仅历史记录、" +
                "错误清空与重新加载；计数行按标签定位，错误文本换行；不连 KV、不做任何写入。");
        }
        finally
        {
            host.Close();
        }
    }

    private static string? AutomationName(Control control) =>
        Avalonia.Automation.AutomationProperties.GetName(control);
}
