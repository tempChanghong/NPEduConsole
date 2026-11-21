using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NPEduConsole.Core;

namespace NPEduConsole.App;

/// <summary>
/// 停用行政班前的影响摘要。只读展示：不解绑大屏、不移除成员、不删除历史，也不发起任何请求。
/// </summary>
public sealed partial class ClassDeactivationImpactView : UserControl
{
    private ClassDeactivationCounts? _counts;
    private string? _error;

    public ClassDeactivationImpactView()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, "停用影响摘要");
    }

    /// <summary>
    /// counts 为 null 且 error 为 null 表示仍在读取；error 非空表示读取失败，此时忽略 counts。
    /// 必须在 UI 线程调用。
    /// </summary>
    public void SetImpact(ClassDeactivationCounts? counts, string? error = null)
    {
        _counts = counts;
        _error = string.IsNullOrWhiteSpace(error) ? null : error.Trim();
        Render();
    }

    private void Render()
    {
        bool failed = _error is not null;
        bool loading = !failed && _counts is null;

        ImpactCounts.Children.Clear();
        ImpactKeyPanel.IsVisible = false;
        ImpactNote.IsVisible = false;
        ImpactMessage.Text = null;
        ImpactMessage.IsVisible = false;
        ImpactStatus.Classes.Set("impact-error", failed);

        if (failed)
        {
            ImpactStatus.Text = "无法读取停用影响";
            ImpactMessage.Text = _error + "\n请稍后重试；停用前必须先确认这份摘要。";
            ImpactMessage.IsVisible = true;
            return;
        }

        if (loading)
        {
            ImpactStatus.Text = "正在读取停用影响…";
            return;
        }

        var counts = _counts!;
        // 只判断有没有，不做求和：服务器计数是 int，求和可能溢出后显示成“没有关联记录”。
        bool hasKey = counts.Students > 0 || counts.ScreenBindings > 0 || counts.TeachingAssignments > 0 ||
            counts.ActivePublications > 0;
        bool any = hasKey || counts.WorkspaceMembers > 0 || counts.PendingInvitations > 0 ||
            counts.PublicationHistory > 0 || counts.Leaderships > 0 || counts.SourceLinks > 0;

        if (!any)
        {
            ImpactStatus.Text = "没有关联记录。";
            ImpactMessage.Text = "学生、成员、任课、作业与大屏关联均为 0；暂无需要先处理的关联。";
            ImpactMessage.IsVisible = true;
            ImpactNote.IsVisible = true;
            return;
        }

        ImpactStatus.Text = hasKey
            ? "该班仍在使用中。停用前请先确认下列关联。"
            : "该班没有学生、大屏、任课或生效作业，但仍有其他关联记录。";

        if (hasKey)
        {
            ImpactKeyPanel.IsVisible = true;
            ImpactKeySummary.Text = $"学生 {counts.Students} 人 · 大屏绑定 {counts.ScreenBindings} 台 · " +
                $"任课安排 {counts.TeachingAssignments} 条 · 生效中的作业或通知 {counts.ActivePublications} 条";
        }

        AddRow("学生", "人", counts.Students, emphasis: true);
        AddRow("大屏绑定", "台", counts.ScreenBindings, emphasis: true);
        AddRow("任课安排", "条", counts.TeachingAssignments, emphasis: true);
        AddRow("生效中的作业或通知", "条", counts.ActivePublications, emphasis: true);
        AddGroup("其他关联");
        AddRow("班级成员", "个", counts.WorkspaceMembers, emphasis: false);
        AddRow("待接受邀请", "个", counts.PendingInvitations, emphasis: false);
        AddRow("作业与通知发布记录", "条", counts.PublicationHistory, emphasis: false);
        AddRow("负责关系", "项", counts.Leaderships, emphasis: false);
        AddRow("来源关联", "项", counts.SourceLinks, emphasis: false);
        ImpactNote.IsVisible = true;
    }

    private void AddGroup(string label)
    {
        var header = new TextBlock
        {
            Text = label,
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
            Classes = { "muted" }
        };
        ImpactCounts.Children.Add(header);
    }

    private void AddRow(string label, string unit, int value, bool emphasis)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var name = new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!emphasis) name.Classes.Add("muted");
        grid.Children.Add(name);

        var count = new TextBlock
        {
            Text = value == 0 ? "无" : $"{value} {unit}",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = emphasis && value > 0 ? FontWeight.SemiBold : FontWeight.Normal
        };
        if (value == 0) count.Classes.Add("muted");
        Grid.SetColumn(count, 1);
        grid.Children.Add(count);

        ImpactCounts.Children.Add(grid);
    }
}
