using System.Text.Json;
using NPEduConsole.Core;

/// <summary>
/// 诊断导出（DeviceDiagnostics）的离线检查：只覆盖 Core 的纯逻辑，不需要服务器或数据库。
/// 入口由 Program.cs 调用 <see cref="Verify"/>；失败抛 InvalidDataException。
/// </summary>
internal static class DeviceDiagnosticsChecks
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 6, 0, 0, TimeSpan.Zero);
    private static readonly string School = Guid.Parse("11111111-1111-4111-8111-111111111111").ToString("D");

    internal static void Verify()
    {
        Counts();
        Dedupe();
        Scope();
        Shapes();
        Times();
        Versions();
        Origin();
        Secrets();
        Console.WriteLine("PASS: diagnostics export caps 200 rows, keeps counts/time honest, and drops unknown or sensitive values.");
    }

    // 201 条加载 → 只导 200 条，计数与「还有下一页」按最后一条真实记录算。
    private static void Counts()
    {
        var rows = Enumerable.Range(0, 201).Select(_ => Row(Guid.NewGuid().ToString("D"))).ToArray();
        var export = Create([Page(Now.AddMinutes(-4), 201, rows)]);
        Same(export.MatchedTotal, 201, "匹配总数");
        Same(export.LoadedCount, 201, "加载总数");
        Same(export.ExportedCount, DeviceDiagnostics.Limit, "导出上限");
        Same(export.OmittedLoadedCount, 1, "被省略条数");
        Same(export.Items.Length, 200, "导出条目数");
        Same(export.HasMore, false, "没有游标时不该声称还有下一页");
        Same(export.SchemaVersion, 1, "导出结构版本");
        Same(export.Kind, "npep-school-overview-diagnostics", "导出种类");
        Same(export.Filter, "ALL", "筛选口径");

        var withCursor = Create([Page(Now, 401, rows, rows[^1].DeviceId)]);
        Same(withCursor.HasMore, true, "有游标就要标还有下一页");
        Same(withCursor.MatchedTotal, 401, "匹配总数取最后一页");
    }

    // 跨页重复的同一台设备只算一条；大小写不同也视为同一台。
    private static void Dedupe()
    {
        var first = Enumerable.Range(0, 200).Select(_ => Row(Guid.NewGuid().ToString("D"))).ToArray();
        var repeated = first[0].DeviceId.ToUpperInvariant();
        var pages = new[]
        {
            Page(Now.AddMinutes(-2), 250, first),
            Page(Now.AddMinutes(-1), 250, [Row(repeated)])
        };
        var export = Create(pages);
        Same(export.LoadedCount, 200, "重复设备去重后条数");
        Same(export.ExportedCount, 200, "去重后导出条数");
        Same(export.OmittedLoadedCount, 0, "去重后没有被省略的条目");
        Same(export.Items.Select(item => item.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count(), 200, "导出里设备不重复");
    }

    // 跨校、空页、缺时间都不该被静默导出。
    private static void Scope()
    {
        Throws("跨校设备", "当前学校", () => Create([Page(Now, 1, [Row(Guid.NewGuid().ToString("D"), school: Guid.NewGuid().ToString("D"))])]));
        Throws("空页", "请先读取设备目录", () => Create([]));
        Throws("缺少读取时间", "请先读取设备目录", () => Create([new DeviceDiagnosticPage(default, 1, [Row(Guid.NewGuid().ToString("D"))], null)]));
        Throws("负的匹配总数", "请先读取设备目录", () => Create([new DeviceDiagnosticPage(Now, -1, [], null)]));
    }

    // 注入的值只可能变成 UNKNOWN / null，不会被原样写进导出。
    private static void Shapes()
    {
        var status = new DeviceDiagnosticStatus("1.2.3<script>", "DAILY; drop table", "IDLE\nx", -4,
            "ENABLED'", "RECORDING; DROP", new DeviceDiagnosticBridge("READY\nx", "9.9.9 <b>"), null);
        var row = Row(Guid.NewGuid().ToString("D"), state: "ACTIVE' OR 1=1", connectivity: "ONLINE\nInjected",
            issues: "OFFLINE", status: status, revision: -1, seen: Now.AddHours(8), name: "教室 A · PIN 998877",
            screen: "主屏 · 日志：token=abc");
        row = row with { Issues = ["OFFLINE", "OFFLINE", "PIN_998877", "OFFLINE"], BindingChanged = true };
        var export = Create([Page(Now, 1, [row])]);
        var item = export.Items.Single();
        Same(item.State, "UNKNOWN", "非法状态");
        Same(item.Connectivity, "UNKNOWN", "非法连接状态");
        Sequence(item.Issues, ["OFFLINE", "UNKNOWN"], "未知问题码只留一个 UNKNOWN、重复被合并");
        Same(item.BindingRevision, null, "负的绑定版本");
        Same(item.BindingChanged, true, "绑定变化标记保留");
        Same(item.LastSeenAt, Now.AddHours(8).ToUniversalTime(), "观测时间统一转 UTC");
        var statusItem = item.Status ?? throw new InvalidDataException("状态字段被丢弃了。");
        Same(statusItem.AppVersion, null, "带标签的版本号");
        Same(statusItem.Mode, "UNKNOWN", "非法模式");
        Same(statusItem.ModePhase, "UNKNOWN", "非法阶段");
        Same(statusItem.ModeRevision, null, "负的模式版本");
        Same(statusItem.AutomaticRecording, "UNKNOWN", "非法自动录制");
        Same(statusItem.Recording, "UNKNOWN", "非法录制状态");
        Same(statusItem.ClassIsland?.Connection, "UNKNOWN", "非法桥接连接");
        Same(statusItem.ClassIsland?.BridgeVersion, null, "非法桥接版本");
        Same(statusItem.ExamAware?.Connection, "UNKNOWN", "缺失的桥接按未核实处理");

        var valid = Row(Guid.NewGuid().ToString("D"), revision: 9007199254740991, status: new DeviceDiagnosticStatus(
            "1.2.3", "EXAM", "RUNNING", 12, "DISABLED", "FINALIZING",
            new DeviceDiagnosticBridge("READY", "1.2.3"), new DeviceDiagnosticBridge("DISCONNECTED", null)));
        var kept = Create([Page(Now, 1, [valid])]).Items.Single();
        Same(kept.BindingRevision, 9007199254740991L, "整数上限保留");
        Same(kept.Status?.Mode, "EXAM", "合法模式保留");
        Same(kept.Status?.Recording, "FINALIZING", "合法录制状态保留");
        var tooBig = Create([Page(Now, 1, [valid with { BindingRevision = 9007199254740992L }])]).Items.Single();
        Same(tooBig.BindingRevision, null, "超过安全整数上限按缺失处理");
    }

    // 生成时间、观测区间与陈旧判断都按 UTC 计算。
    private static void Times()
    {
        var early = Now.AddSeconds(-50);
        var late = Now.AddSeconds(-10);
        var fresh = Create([Page(early, 1, [Row(Guid.NewGuid().ToString("D"))]), Page(late, 1, [Row(Guid.NewGuid().ToString("D"))])]);
        Same(fresh.GeneratedAt, Now, "生成时间取传入的 now");
        Same(fresh.OldestObservationAt, early, "最早观测时间");
        Same(fresh.LatestObservationAt, late, "最晚观测时间");
        Same(fresh.Stale, false, "两页都在一分钟内且不晚于现在");

        var offset = new DateTimeOffset(2026, 11, 23, 14, 0, 0, TimeSpan.FromHours(8));
        var converted = Create([Page(offset, 1, [Row(Guid.NewGuid().ToString("D"))])]);
        Same(converted.OldestObservationAt, Now, "带时区偏移的读取时间换算成 UTC");
        Same(converted.Stale, false, "换算后正好是现在，不算陈旧");

        var ahead = Create([Page(Now.AddMinutes(3), 1, [Row(Guid.NewGuid().ToString("D"))])]);
        Same(ahead.Stale, true, "读取时间晚于现在要标陈旧");

        var old = Create([Page(Now.AddMinutes(-5), 1, [Row(Guid.NewGuid().ToString("D"))])]);
        Same(old.Stale, true, "超过一分钟未刷新要标陈旧");
    }

    // 版本号只接受明确的几种写法，绝不放任意字符串进导出。
    private static void Versions()
    {
        void Version(string input, string? expected)
        {
            var row = Row(Guid.NewGuid().ToString("D"), status: new DeviceDiagnosticStatus(input, "DAILY", "RUNNING", 1,
                "ENABLED", "RECORDING", null, null));
            Same(Create([Page(Now, 1, [row])]).Items.Single().Status?.AppVersion, expected, "版本号 " + input);
        }
        Version("1.2.3", "1.2.3");
        Version("1.2.3-beta.1+abcdef1", "1.2.3-beta.1+abcdef1");
        Version("InDev 20261123", "InDev 20261123");
        Version("v1.2.3 · EVA-02（二号机）", "1.2.3");
        Version("v1.2.3 · EVA-02（二号机）+abcdef1", "1.2.3+abcdef1");
        Version("<script>alert(1)</script>", null);
        Version(new string('9', 161), null);
    }

    // 后端地址只保留「协议 + 主机端口」，带账号密码或非 http(s) 一律不写。
    private static void Origin()
    {
        var plain = Create([Page(Now, 1, [Row(Guid.NewGuid().ToString("D"))])], server: new Uri("https://kv.example.com:8443/api/v2?x=1"));
        Same(plain.BackendOrigin, "https://kv.example.com:8443", "去掉路径与查询串");
        var credentials = Create([Page(Now, 1, [Row(Guid.NewGuid().ToString("D"))])], server: new Uri("https://admin:secret@kv.example.com/api"));
        Same(credentials.BackendOrigin, null, "带账号密码的地址不写");
        var insecure = Create([Page(Now, 1, [Row(Guid.NewGuid().ToString("D"))])], server: new Uri("ftp://kv.example.com/api"));
        Same(insecure.BackendOrigin, null, "非 http(s) 地址不写");
    }

    // 名称、搜索词、PIN、日志一类内容不该出现在导出里；字段集合严格固定。
    private static void Secrets()
    {
        var row = Row(Guid.NewGuid().ToString("D"), name: "张老师 · PIN 998877", screen: "南楼 302 主屏 · 日志 token=abc123",
            status: new DeviceDiagnosticStatus("1.2.3", "DAILY", "RUNNING", 3, "ENABLED", "RECORDING",
                new DeviceDiagnosticBridge("READY", "1.2.3"), null));
        var export = Create([Page(Now, 1, [row])], hasSearch: true);
        Same(export.HasSearch, true, "有搜索词时只写标记");
        var json = DeviceDiagnostics.Serialize(export);
        foreach (var secret in new[] { "PIN 998877", "abc123", "南楼 302", "张老师", "13800138000" })
            Same(json.Contains(secret, StringComparison.Ordinal), false, "导出里不该出现 " + secret);

        using var document = JsonDocument.Parse(json);
        Keys(document.RootElement, ["schemaVersion", "kind", "backendOrigin", "appVersion", "schoolId", "generatedAt",
            "oldestObservationAt", "latestObservationAt", "stale", "filter", "hasSearch", "matchedTotal", "loadedCount",
            "exportedCount", "omittedLoadedCount", "hasMore", "items"], "导出顶层字段");
        var item = document.RootElement.GetProperty("items")[0];
        Keys(item, ["deviceId", "schoolId", "administrativeClassId", "screenBindingId", "bindingRevision", "bindingChanged",
            "state", "connectivity", "lastSeenAt", "issues", "status"], "单条设备字段");
        Keys(item.GetProperty("status"), ["appVersion", "mode", "modePhase", "modeRevision", "automaticRecording",
            "recording", "classIsland", "examAware"], "状态字段");
        Keys(item.GetProperty("status").GetProperty("classIsland"), ["connection", "bridgeVersion"], "桥接字段");
        Same(document.RootElement.GetProperty("schoolId").GetString(), School, "学校编号写入导出");
    }

    private static DeviceDiagnosticPage Page(DateTimeOffset asOf, int matched, DeviceDiagnosticRow[] rows, string? next = null) =>
        new(asOf, matched, rows, next);

    private static DeviceDiagnosticRow Row(string deviceId, string state = "ACTIVE", string connectivity = "ONLINE",
        string? issues = null, DeviceDiagnosticStatus? status = null, string? school = null, long? revision = 7,
        bool? changed = false, DateTimeOffset? seen = null, string? name = null, string? screen = null) =>
        new(deviceId, school ?? School, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), revision, changed,
            state, connectivity, seen ?? Now.AddMinutes(-1), issues is null ? null : [issues], status, name, screen);

    private static DeviceDiagnosticsExport Create(DeviceDiagnosticPage[] pages, string? appVersion = "1.2.3",
        string filter = "ALL", bool hasSearch = false, Uri? server = null, string? school = null) =>
        DeviceDiagnostics.Create(server ?? new Uri("https://kv.example.com/api/v2"), appVersion, school ?? School,
            filter, hasSearch, pages, Now);

    private static void Same<T>(T actual, T expected, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new InvalidDataException($"{what}：期望 {expected}，实际 {actual}。");
    }

    private static void Sequence(string[] actual, string[] expected, string what)
    {
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException($"{what}：期望 [{string.Join(", ", expected)}]，实际 [{string.Join(", ", actual)}]。");
    }

    private static void Keys(JsonElement element, string[] expected, string what)
    {
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException($"{what}：期望 [{string.Join(", ", expected)}]，实际 [{string.Join(", ", actual)}]。");
    }

    private static void Throws(string what, string message, Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException error) when (error.Message.Contains(message, StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidDataException($"{what}：没有按预期拒绝。");
    }
}
