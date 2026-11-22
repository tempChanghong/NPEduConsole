namespace NPEduConsole.Core;

public static class ExamReason
{
    public static string Describe(string? reason) => reason switch
    {
        null => "",
        "EXAM_MODE_REQUIRED" => "请先完成进入考试模式，等这台设备报告环境已就绪。",
        "CONTROL_DISABLED" => "设备尚未开放本项学校控制，或学校连接已暂停。请到教室核对配对授权。",
        "DEVICE_OFFLINE" => "设备观测已过时或尚未连接，请等这台大屏重新上报。",
        "CLIENT_UNSUPPORTED" => "当前设备版本未报告支持，请检查 NPEduTools 版本和学校连接。",
        "PLAYER_BUSY" or "EXAMAWARE_PRESENTING" => "ExamAware 已有放映。请在教室结束放映后核对。",
        "PLAYER_UNKNOWN" => "播放器状态待核对。请在教室检查 ExamAware 和桥接权限。",
        "BRIDGE_DISCONNECTED" => "ExamAware 桥接未连接。请在教室打开 ExamAware 并检查插件连接。",
        "BRIDGE_UPGRADE_REQUIRED" or "PLAYER_PERMISSION_DENIED" => "请在教室安装支持放映的 ExamAware 桥接版本并核对放映权限。",
        "RECORDING_BUSY" => "教室正在录制或保存录制。请等录制任务结束后再投递方案。",
        "RECORDING_SAVE_FAILED" or "RECORDING_SAVE_TIMEOUT" => "录制尚未成功保存。请到教室确认录制状态后再切换。",
        "DESKTOP_UNAVAILABLE" => "教室桌面当前不可交互。请在现场登录或解除锁屏。",
        "NOTICE_OPEN" => "教室正在处理通知。请等通知关闭后核对。",
        "OPERATION_BUSY" => "这台设备已有操作，请核对当前任务。",
        "INVALID_PLAN" => "设备未通过校验。请在 ExamAware 编辑器核对方案文件。",
        "PLAN_EXPIRED" or "EXPIRED" => "本次方案或授权已过期，请重新核对后办理。",
        "UNKNOWN_RESULT" => "执行结果待核对，请查看教室现场。系统不会自动重新开播。",
        "STATE_CHANGED" or "POLICY_CHANGED" or "POLICY_STALE" or "SESSION_SUPERSEDED" => "设备状态或授权已变化，请重新核对。",
        "START_ALREADY_AUTHORIZED" => "设备已取得开始许可。请到教室核对，必要时在 ExamAware 结束。",
        "IDEMPOTENCY_CONFLICT" => "原请求与当前内容不一致，或其他管理员已先操作。请核对当前任务。",
        "EXAMAWARE_EXIT_FAILED" => "ExamAware 未能正常退出。请在教室保存并关闭未保存的编辑内容，结束放映后重试。",
        "EXAMAWARE_NOT_READY" or "EXAMAWARE_CONFIGURATION_REQUIRED" => "请在教室核对 ExamAware 程序位置、运行状态和桥接连接。",
        "CLASSISLAND_TASK_REQUIRED" or "CLASSISLAND_CONFIGURATION_REQUIRED" or "CLASSISLAND_NOT_READY" or "CLASSISLAND_EXIT_UNAVAILABLE" =>
            "请在教室核对 ClassIsland 程序、登录任务和课表接口。",
        "LOCAL_REQUEST_REJECTED" or "RECOVERY_REQUIRED" => "请到教室查看 NPEduTools 的实际状态。若编辑器有未保存内容，请先保存并关闭。",
        "BRIDGE_COMMAND_LIMIT" => "桥接已达到本次命令上限，请在 ExamAware 重新启用插件后核对。",
        "INITIATOR_NO_LONGER_AUTHORIZED" or "SCHOOL_ADMIN_REQUIRED" => "学校管理员授权已变化，请重新核对当前权限。",
        "AUTH_REVOKED" or "CREDENTIAL_EXPIRED" => "这台设备的原授权已失效。请返回设备目录，重新核对当前绑定。",
        "AUTH_INVALID" => "登录或原设备授权已变化，请重新核对。",
        "NOT_FOUND" => "未找到对应的设备、操作或请求回执，请核对当前绑定。",
        "PROTOCOL_UNSUPPORTED" => "KV 尚不支持此项考试办理，请使用现有网页入口。",
        "RATE_LIMITED" => "学校服务请求较多，请稍后按原请求核对。",
        "TEMPORARILY_UNAVAILABLE" => "学校互联暂不可用，请稍后重新核对。",
        _ => "设备或学校服务未能完成本次操作，请核对现场详情。"
    };

    public static ClassroomPlayerSession? PlayerObservation(ClassroomExamState? state, ExamPlanOperation? plan)
    {
        if (state?.PlanFresh != true || state.Plans.Status?.Player is not { Known: true } player || plan?.SessionId is not { Length: > 0 } id)
            return null;
        return player.Sessions.FirstOrDefault(session => session.Id == id) ?? (player.LastSession?.Id == id ? player.LastSession : null);
    }
}
