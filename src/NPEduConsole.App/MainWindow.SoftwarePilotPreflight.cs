using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private const string LocalPilotPreflightNote = "现场安装与恢复：待核实。请在本机核对 NPDownloader 安装、两包下载验签及实际回退。";
    private DispatcherTimer? _softwarePreflightClock;

    private void EnsureSoftwarePreflightClock()
    {
        if (_softwarePreflightClock is not null) return;
        _softwarePreflightClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _softwarePreflightClock.Tick += RefreshSoftwarePreflightClock;
    }

    private void StopSoftwarePreflightClock() => _softwarePreflightClock?.Stop();

    private void RefreshSoftwarePreflightClock(object? sender, EventArgs args)
    {
        if (!SoftwareMaintenanceCard.IsVisible || _softwareError is not null || _software is null)
        {
            StopSoftwarePreflightClock();
            return;
        }
        RenderSoftwarePilotPreflight(_software);
    }

    private void RenderSoftwarePilotPreflight(SoftwareUpdate? snapshot)
    {
        if (!SoftwareMaintenanceCard.IsVisible)
        {
            StopSoftwarePreflightClock();
            SoftwarePilotPreflightSection.IsVisible = false;
            return;
        }

        SoftwarePilotPreflightSection.IsVisible = true;
        SoftwarePilotPreflightLocal.Text = LocalPilotPreflightNote;

        if (_softwareError is not null || snapshot is null)
        {
            StopSoftwarePreflightClock();
            var history = ((_softwareError is null && snapshot is null) || (_softwareError is not null && _softwareErrorAllowsHistory)) &&
                          EquipmentKey is { } key
                ? GetSoftwareHistory(_client, key)
                : null;
            SoftwarePilotPreflightSummary.Text = FormatOfflinePreflightSummary(_softwareRead is not null, _softwareError is not null,
                _softwareErrorAllowsHistory, history is not null);
            SoftwarePilotPreflightEnrollment.Text = "纳管：待核实（无实时状态）";
            SoftwarePilotPreflightVersion.Text = history?.ObservedVersion is { } version
                ? $"版本：{version}（上次上报 · {FormatTime(history.LastSeenAt)}）"
                : "版本：待核实（无当期证据）";
            SoftwarePilotPreflightRecovery.Text = "原版恢复包：待核实";
            SoftwarePilotPreflightTargets.Text = "候选目标：待核实";
            return;
        }

        var preflight = snapshot.GetPilotPreflight(DateTimeOffset.UtcNow);
        SoftwarePilotPreflightSummary.Text = FormatLivePreflightSummary(preflight);
        SoftwarePilotPreflightEnrollment.Text = FormatPreflightEnrollment(preflight.Enrollment);
        SoftwarePilotPreflightVersion.Text = FormatPreflightVersion(preflight);
        SoftwarePilotPreflightRecovery.Text = FormatPreflightRecovery(preflight);
        SoftwarePilotPreflightTargets.Text = FormatPreflightTargets(preflight);

        if (preflight.Observation == "CURRENT")
        {
            EnsureSoftwarePreflightClock();
            _softwarePreflightClock!.Start();
        }
        else
        {
            StopSoftwarePreflightClock();
        }
    }

    private static string FormatOfflinePreflightSummary(bool reading, bool hasError, bool allowsHistory, bool hasHistory)
    {
        if (reading)
            return hasHistory
                ? "正在读取学校状态。下方显示上次上报的版本，当前纳管与目录项待核实。"
                : "正在读取学校实时状态，当前试点条件待核实，请稍候。";
        if (hasError && allowsHistory)
            return "暂无法读取学校状态。纳管、目录与恢复包待核实，请恢复后刷新。";
        if (hasError)
            return "试点条件暂不可核实。请按上方提示处理后刷新。";
        return "尚无学校实时状态，请点击「刷新维护状态」后再核对试点条件。";
    }

    private static string FormatLivePreflightSummary(SoftwarePilotPreflight preflight)
    {
        if (preflight.HasActiveTask)
            return "当前已有进行中的维护任务，请先观察或处理现有任务，完成后再评估下一轮试点。";
        if (preflight.Enrollment == "INVITED")
            return "待现场同意：请到教室大屏打开 NPDownloader，核对校班屏并同意纳管。";
        if (preflight.Enrollment == "UNMANAGED")
            return "设备尚未纳管：请先点击「邀请设备纳管」，再到教室端确认。";
        if (preflight.Enrollment != "MANAGED")
            return "纳管状态待核实，请点击「刷新维护状态」后再评估是否进入试点。";
        if (preflight.CatalogStatus == "NOT_CONFIGURED")
            return "学校更新目录尚未配置，请先完成服务端目录配置后再进入试点。";
        if (preflight.CatalogStatus == "UNAVAILABLE")
            return "学校更新目录暂不可用，请核对服务端目录可读性后刷新。";
        if (preflight.CatalogStatus != "AVAILABLE")
            return "学校更新目录状态未知，请刷新核对后再评估试点。";
        if (preflight.Observation == "HISTORICAL")
            return "仅有历史版本记录。请确认教室 NPEduTools 运行并连回学校后刷新。";
        if (preflight.Observation != "CURRENT")
            return "尚未收到设备当期版本上报，请确认教室 NPEduTools 已运行并连回学校后刷新。";
        if (preflight.RecoveryPackage == "MISSING")
            return preflight.ObservedVersion is { } version
                ? $"学校更新目录缺少原版恢复包 {version}，请先补齐该版本恢复包后再批准试点。"
                : "学校更新目录缺少当前版本的原版恢复包，请先补齐后再批准试点。";
        if (preflight.RecoveryPackage != "IN_CATALOG")
            return "原版恢复包状态待核实，请核对学校更新目录后刷新。";
        if (preflight.TargetVersions.Count == 0)
            return "学校更新目录暂无更高的候选版本，请先上架目标版本包。";
        return "已核对学校侧条件。请先核实现场条件，再到下方选择目标版本。";
    }

    private static string FormatPreflightEnrollment(string enrollment) => enrollment switch
    {
        "MANAGED" => "纳管：学校记录已纳管",
        "INVITED" => "纳管：待现场同意",
        "UNMANAGED" => "纳管：未纳管",
        _ => "纳管：待核实"
    };

    private static string FormatPreflightVersion(SoftwarePilotPreflight preflight) => preflight.Observation switch
    {
        "CURRENT" when preflight.ObservedVersion is { } version => $"运行版本：{version}（近期上报 · {FormatTime(preflight.ObservedAt)}）",
        "HISTORICAL" when preflight.ObservedVersion is { } version => $"版本：{version}（历史 · {FormatTime(preflight.ObservedAt)}）",
        "HISTORICAL" => "版本：仅历史记录，暂无有效版本号",
        _ => "版本：待核实（尚无当期上报）"
    };

    private static string FormatPreflightRecovery(SoftwarePilotPreflight preflight) => preflight.CatalogStatus switch
    {
        "NOT_CONFIGURED" => "原版恢复包：目录未配置",
        "UNAVAILABLE" => "原版恢复包：目录暂不可用",
        "AVAILABLE" when preflight.Observation != "CURRENT" => "原版恢复包：待核实（无当期版本证据）",
        "AVAILABLE" when preflight.RecoveryPackage == "IN_CATALOG" => preflight.ObservedVersion is { } version
            ? $"原版恢复包：学校目录已收录 {version}"
            : "原版恢复包：学校目录已收录",
        "AVAILABLE" when preflight.RecoveryPackage == "MISSING" => preflight.ObservedVersion is { } version
            ? $"原版恢复包：学校目录缺少 {version}"
            : "原版恢复包：学校目录缺失",
        _ => "原版恢复包：待核实"
    };

    private static string FormatPreflightTargets(SoftwarePilotPreflight preflight)
    {
        if (preflight.CatalogStatus == "NOT_CONFIGURED") return "候选目标：目录未配置";
        if (preflight.CatalogStatus == "UNAVAILABLE") return "候选目标：目录暂不可用";
        if (preflight.CatalogStatus != "AVAILABLE") return "候选目标：待核实";
        if (preflight.Observation != "CURRENT") return "候选目标：待核实（需当期版本）";
        if (preflight.TargetVersions.Count == 0) return "候选目标：目录暂无更高版本";
        string list = string.Join("、", preflight.TargetVersions.Take(3));
        if (preflight.TargetVersions.Count > 3) list += $" 等 {preflight.TargetVersions.Count} 个";
        return $"候选目标：学校目录已收录 {list}";
    }
}
