using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NPEduConsole.Core;

namespace NPEduConsole.App;

/// <summary>整校迁入的只读预览：哪份包、会带什么、哪些东西留在源服、哪些屏要重新接。</summary>
public sealed partial class SchoolTransferPreviewView : UserControl
{
    private static readonly (string Key, string Label)[] CountLabels =
    [
        ("schools", "学校"),
        ("accounts", "账号"),
        ("administrators", "管理员"),
        ("terms", "学期"),
        ("grades", "年级"),
        ("subjects", "学科"),
        ("workspaces", "班级与走班"),
        ("teachers", "任课"),
        ("students", "学生"),
        ("screens", "大屏"),
        ("attendanceDays", "考勤日"),
        ("publications", "发布"),
        ("revisions", "发布修订"),
        ("auditLogs", "审计记录"),
        ("managementAuthority", "管理账号版本"),
        ("studySettings", "自习策略"),
        ("legacyNoiseSchedules", "旧噪音安排"),
        ("noiseDisplaySettings", "噪音显示设置"),
        ("portableClassCredentials", "班级管理口令"),
        ("seatVersions", "座位图版本"),
        ("seatImages", "座位图图片"),
    ];

    public SchoolTransferPreviewView()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, "迁入预览");
    }

    /// <summary>null 表示还没有读到包（例如刚进入向导）；同一份包重复设置时保留滚动位置。</summary>
    public void Present(SchoolTransferPreview? snapshot, string server)
    {
        var server_ = string.IsNullOrWhiteSpace(server) ? "当前服务器" : server.Trim();
        TransferServer.Text = $"目标服务器：{server_}";

        if (snapshot is null)
        {
            TransferEmpty.IsVisible = true;
            TransferSummary.IsVisible = false;
            TransferWarningSection.IsVisible = false;
            TransferScreens.Children.Clear();
            TransferRoutes.Children.Clear();
            TransferWarnings.Children.Clear();
            TransferPackage.Text = "包信息：尚未读取";
            TransferStatus.Text = "请选择一份迁移包后再核对。";
            return;
        }

        TransferEmpty.IsVisible = false;
        TransferSummary.IsVisible = true;

        var manifest = snapshot.Manifest;
        // Core 只在「还能写入」时给出非空 PreviewToken；带快照却没有 token，说明这是迁入完成后的只读回执。
        var receipt = snapshot.PreviewToken is not { Length: > 0 };
        TransferHead.Text = receipt
            ? $"迁入已写入 · {Safe(manifest.School.Name)}"
            : $"迁入预览 · {Safe(manifest.School.Name)}";
        TransferPackage.Text =
            $"{(receipt ? "回执" : "包信息")}：格式 v{manifest.FormatVersion} · 导出于 {Local(manifest.ExportedAt)} · 迁移编号 {Short(manifest.MigrationId.ToString())}";
        TransferSource.Text = $"源学校：{Safe(manifest.School.Name)}（{Safe(manifest.School.Code)}）";
        TransferTarget.Text = $"目标服务器：{server_}";
        TransferCountHead.Text = receipt ? "这次迁入写入了什么" : "这份包会带来什么";

        var counts = snapshot.Counts ?? new Dictionary<string, int>();
        var parts = CountLabels
            .Where(pair => counts.TryGetValue(pair.Key, out var value) && value > 0)
            .Select(pair => $"{pair.Label} {counts[pair.Key]}")
            .ToList();
        var unknown = counts.Count - CountLabels.Count(pair => counts.ContainsKey(pair.Key));
        if (unknown > 0) parts.Add($"其它 {unknown} 项");
        TransferCounts.Text = parts.Count == 0
            ? (receipt ? "回执没有列出写入条数。" : "包内没有可枚举的数据。")
            : $"{(receipt ? "已经写入" : "会带来")}：{string.Join(" · ", parts)}";

        var retained = snapshot.Retained;
        TransferRetained.Text = retained is null
            ? "旧包未记录留源数量。自习照片和退出记录留在源服。"
            : $"留在源服：自习照片 {retained.Photos} 张 · 退出记录 {retained.ExitEvents} 条（不会复制到新服务器）";

        FillScreens(snapshot.ScreenActions);
        FillRoutes(snapshot.OwnerRoutes);
        FillWarnings(snapshot.Warnings);

        var active = snapshot.ScreenActions?.Count(row => row.IsActive) ?? 0;
        TransferStatus.Text = receipt
            ? $"这些大屏待重新接入：{active} 台在用。"
            : $"已核对：{active} 台在用大屏需要重新接入。确认后会立即写入新实例。";
        TransferFootNote.Text = receipt
            ? "本页是只读回执：迁入已经完成，写入结果以服务器为准。"
            : "本页只做核对：真正的迁入在最后一步确认后才会写入，写入前可以返回重新选择文件。";
    }

    private void FillScreens(TransferScreen[]? screens)
    {
        TransferScreens.Children.Clear();
        if (screens is null || screens.Length == 0)
        {
            TransferScreens.Children.Add(Note("包内没有大屏。"));
            return;
        }

        foreach (var screen in screens)
        {
            var chips = new List<string> { screen.IsActive ? "在用" : "当前不可接入" };
            if (screen.WebLogin) chips.Add("网页账号保留");
            else if (screen.IsActive) chips.Add("网页账号尚未配置");
            if (screen.NativeNeedsPairing) chips.Add("原生待现场重新配对");
            if (screen.ClassSecretNeedsReset) chips.Add("班级口令需重设");

            var row = new Border { Classes = { "transfer-row" } };
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new TextBlock
            {
                Text = $"{Safe(screen.Name)} · {Safe(screen.ClassName)}",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            });
            stack.Children.Add(Note($"绑定编号 {screen.Id}"));
            stack.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", chips),
                FontSize = 12,
                Foreground = Brush("MutedBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            row.Child = stack;
            AutomationProperties.SetName(row, $"{screen.Name}，{screen.ClassName}，{string.Join("，", chips)}");
            TransferScreens.Children.Add(row);
        }
    }

    private void FillRoutes(TransferOwnerRoute[]? routes)
    {
        TransferRoutes.Children.Clear();
        if (routes is null || routes.Length == 0)
        {
            TransferRoutes.Children.Add(Note("包内没有声明管理员接管路径，迁入后请用原有凭据登录核对。"));
            return;
        }

        foreach (var route in routes)
        {
            var text = $"{Safe(route.Name)} · {MethodLabel(route.Method)} · {(route.Available ? "可用" : "需要额外准备")}";
            var row = new Border { Classes = { "transfer-row" } };
            row.Child = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(row, text);
            TransferRoutes.Children.Add(row);
        }
    }

    private void FillWarnings(string[]? warnings)
    {
        TransferWarnings.Children.Clear();
        var rows = (warnings ?? []).Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
        TransferWarningSection.IsVisible = rows.Count > 0;
        foreach (var text in rows)
        {
            TransferWarnings.Children.Add(new TextBlock
            {
                Text = $"— {text.Trim()}",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("MutedBrush"),
            });
        }
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("MutedBrush"),
    };

    private static string MethodLabel(string method) => method switch
    {
        "PIN" or "LOCAL_PIN" => "本地 PIN",
        "PASSWORD" or "SHARED_PASSWORD" => "通用口令",
        "OAUTH" => "OAuth",
        _ => string.IsNullOrWhiteSpace(method) ? "未知方式" : method,
    };

    private static string Safe(string? text) => string.IsNullOrWhiteSpace(text) ? "（未命名）" : text.Trim();

    private static string Local(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];

    private static IBrush? Brush(string key) =>
        Avalonia.Application.Current?.TryFindResource(key, out var value) == true ? value as IBrush : null;
}
