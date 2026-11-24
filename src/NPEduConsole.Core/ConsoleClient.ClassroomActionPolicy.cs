using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NPEduConsole.Core;

public sealed partial class ConsoleClient
{
    private const string ActionPolicySegment = "/classroom-action-policy";
    private static readonly Regex ActionPolicyProof = new(@"\A[0-9a-f]{64}\z", RegexOptions.NonBacktracking);

    private static string ActionPolicyPath(string schoolId) => "api/v2/npep/schools/" + Segment(schoolId) + ActionPolicySegment;

    public async Task<ClassroomActionCatalog> GetClassroomActionCatalogAsync(string schoolId, CancellationToken ct)
    {
        var catalog = await ActionPolicyRequestAsync<ClassroomActionCatalog>(HttpMethod.Get, ActionPolicyPath(schoolId), ct);
        if (catalog.SchoolId != schoolId || RequireNonNullRows(catalog.Grades).Any(grade => string.IsNullOrWhiteSpace(grade.Id)) ||
            RequireNonNullRows(catalog.Rooms).Any(room => string.IsNullOrWhiteSpace(room.Id) || room.TermId != catalog.Term?.Id))
            throw InvalidResponse();
        return catalog;
    }

    public async Task<ClassroomActionScope> GetClassroomActionScopeAsync(string schoolId, ClassroomActionTarget target, CancellationToken ct)
    {
        var path = ActionPolicyPath(schoolId) + $"/scope/{Segment(target.TermId)}/{Segment(target.ScopeKind)}/{Segment(target.ScopeId)}";
        var scope = await ActionPolicyRequestAsync<ClassroomActionScope>(HttpMethod.Get, path, ct);
        return ValidateActionScope(scope, schoolId, target);
    }

    public async Task<ClassroomHistoricalUsbRegistry> GetClassroomHistoricalUsbKeysAsync(string schoolId, CancellationToken ct)
    {
        var registry = await ActionPolicyRequestAsync<ClassroomHistoricalUsbRegistry>(HttpMethod.Get, ActionPolicyPath(schoolId) + "/history-usb", ct);
        var keys = RequireNonNullRows(registry.Keys);
        if (registry.SchoolId != schoolId || keys.Any(key => !Guid.TryParseExact(key.KeyId, "D", out _) ||
                string.IsNullOrWhiteSpace(key.Label) || string.IsNullOrWhiteSpace(key.ScopeId) || string.IsNullOrWhiteSpace(key.ScopeName) ||
                key.ScopeKind is not ("GRADE" or "CLASS") || !ValidHistoricalScope(key.Term, key.ScopeState)) ||
            keys.Select(key => key.KeyId).Distinct(StringComparer.Ordinal).Count() != keys.Length) throw InvalidResponse();
        return registry;
    }

    public async Task<ClassroomActionPreview> PreviewClassroomActionAsync(string schoolId, ClassroomActionChange change, CancellationToken ct)
    {
        var requestId = Guid.NewGuid();
        var preview = await ActionPolicyRequestAsync<ClassroomActionPreview>(HttpMethod.Post, ActionPolicyPath(schoolId) + "/preview", ct,
            new {requestId, change = ActionChangeBody(change)}, requestId);
        if (preview.HistoricalTerm is { } term && (change.Operation != "REVOKE_USB" || term.Id != change.TermId || !ValidHistoricalScope(term, preview.HistoricalScopeState)) ||
            preview.HistoricalTerm is null && preview.HistoricalScopeState is not null)
            throw InvalidResponse();
        ValidateActionScope(preview, schoolId, change.Target, allowMissingClass: preview.HistoricalTerm is not null);
        if (!ActionPolicyProof.IsMatch(preview.Version) || RequireNonNullRows(preview.Issues).Any(issue =>
                issue.Action is not ("stopScheduledMonitoring" or "exitBackground" or "switchClassroomMode") ||
                !preview.Rooms.Any(room => room.Id == issue.ClassId)) ||
            preview.CanApply != (preview.Issues.Length == 0 || change.Operation != "SET_POLICY" || change.RestoresInheritedPolicy)) throw InvalidResponse();
        return preview;
    }

    public async Task<ClassroomActionReceipt> ApplyClassroomActionAsync(string schoolId, ClassroomActionChange change, string expectedVersion,
        Guid requestId, CancellationToken ct)
    {
        var receipt = await ActionPolicyRequestAsync<ClassroomActionReceipt>(HttpMethod.Post, ActionPolicyPath(schoolId), ct,
            new {requestId, change = ActionChangeBody(change), expectedVersion}, requestId);
        return ValidateActionReceipt(receipt, schoolId, change, requestId);
    }

    public async Task<ClassroomActionReceipt> ReadClassroomActionReceiptAsync(string schoolId, ClassroomActionChange change, Guid requestId, CancellationToken ct)
    {
        var receipt = await ActionPolicyRequestAsync<ClassroomActionReceipt>(HttpMethod.Get, ActionPolicyPath(schoolId) + $"/receipts/{requestId:D}", ct);
        return ValidateActionReceipt(receipt, schoolId, change, requestId);
    }

    private static object ActionChangeBody(ClassroomActionChange change)
    {
        var body = new Dictionary<string, object?>
        {
            ["termId"] = change.TermId, ["scopeKind"] = change.ScopeKind, ["scopeId"] = change.ScopeId,
            ["operation"] = change.Operation,
            ["actions"] = change.Actions is { } rules && !change.IncludeClassroomModeSwitch && rules.SwitchClassroomMode is null ?
                new {rules.StopScheduledMonitoring, rules.ExitBackground} : change.Actions,
            ["usbKey"] = change.UsbKey, ["keyId"] = change.KeyId
        };
        if (change.ClassActionOverrides is { } overrides) body["classActionOverrides"] = overrides;
        return body;
    }

    private async Task<T> ActionPolicyRequestAsync<T>(HttpMethod method, string path, CancellationToken ct, object? body = null, Guid? id = null)
    {
        var session = CurrentSession;
        var result = ReadData<T>(await SendAsync(method, path, session.Token, ct, version: "0.16", body: body, operationId: id,
            classroomActionPolicy: true));
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(session, _session)) throw new ConsoleApiException(401, "登录会话已改变，请重新登录。");
        return result;
    }

    private static ClassroomActionScope ValidateActionScope(ClassroomActionScope scope, string schoolId, ClassroomActionTarget target, bool allowMissingClass = false)
    {
        if (scope.SchoolId != schoolId || scope.TermId != target.TermId || scope.ScopeKind != target.ScopeKind || scope.ScopeId != target.ScopeId ||
            !ValidActionRules(scope.SavedActions) || !ValidActionRules(scope.InheritedGradeActions) ||
            scope.InheritedGradeActions is not null && target.ScopeKind != "CLASS" ||
            scope.SavedClassActionOverrides is { } overrides && (scope.SavedActions is not null || target.ScopeKind != "CLASS" ||
                !overrides.HasOverrides || !ValidOverride(overrides.StopScheduledMonitoring) || !ValidOverride(overrides.ExitBackground) ||
                !ValidOverride(overrides.SwitchClassroomMode, allowLegacy: true))) throw InvalidResponse();
        var rooms = RequireNonNullRows(scope.Rooms);
        if (rooms.Any(room => string.IsNullOrWhiteSpace(room.Id) || !ValidEffective(room.Before) || !ValidEffective(room.After)) ||
            rooms.Select(room => room.Id).Distinct(StringComparer.Ordinal).Count() != rooms.Length ||
            target.ScopeKind == "CLASS" && (rooms.Length > 1 || rooms.Length == 0 && !allowMissingClass || rooms.Length == 1 && rooms[0].Id != target.ScopeId))
            throw InvalidResponse();
        var screens = RequireNonNullRows(scope.Screens);
        if (screens.Any(screen => screen.BindingRevision < 0 || string.IsNullOrWhiteSpace(screen.BindingId) ||
            !rooms.Any(room => room.Id == screen.ClassId) || !ValidDeviceReceipt(screen.Observed) || !ValidDeviceReceipt(screen.LastApplied)) ||
            screens.Select(screen => screen.BindingId).Distinct(StringComparer.Ordinal).Count() != screens.Length) throw InvalidResponse();
        var keys = RequireNonNullRows(scope.UsbKeys);
        if (keys.Any(key => !Guid.TryParseExact(key.KeyId, "D", out _) || string.IsNullOrWhiteSpace(key.Label)) ||
            keys.Select(key => key.KeyId).Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw InvalidResponse();
        return scope;
    }

    private static bool ValidHistoricalScope(ClassroomActionHistoricalTerm term, string? scopeState) => !string.IsNullOrWhiteSpace(term.Id) &&
        !string.IsNullOrWhiteSpace(term.Name) && scopeState is "PRESENT" or "INACTIVE" or "MISSING" &&
        (term.Status is "DRAFT" or "ARCHIVED" or "MISSING" || term.Status == "ACTIVE" && scopeState is not "PRESENT");

    private static bool ValidDeviceReceipt(ClassroomActionDeviceReceipt? receipt) => receipt is null || receipt.SchemaVersion is 1 or 2 or 3 &&
        (receipt.Revision is null ? receipt.PolicyId is null : receipt.Revision >= 0 && receipt.PolicyId is { } id && ActionPolicyProof.IsMatch(id));
    private static bool ValidEffective(ClassroomEffectiveAction policy)
    {
        if (!ActionPolicyProof.IsMatch(policy.PolicyId) || policy.UsbCount is < 0 or > 64 || policy.RequiredRevision < 0)
            return false;
        bool validWhole = policy.Source == "LEGACY" ? policy.Actions is null && policy.UsbCount == 0 :
            policy.Source is "GRADE" or "CLASS" && policy.Actions is not null && ValidActionRules(policy.Actions);
        if (policy.ActionOrigins is not { } origins) return policy.MinimumReceiptSchema is null && validWhole;
        if (policy.Source == "LEGACY")
            return validWhole && policy.MinimumReceiptSchema == 1 && origins == new ClassroomActionOrigins("LEGACY", "LEGACY", "LEGACY");
        if (policy.Source is not ("GRADE" or "CLASS") || policy.Actions is not { } actions) return false;
        int schema = actions.StopScheduledMonitoring is null || actions.ExitBackground is null ? 3 : actions.SwitchClassroomMode is not null ? 2 : 1;
        return policy.MinimumReceiptSchema == schema && ValidOrigin(origins.StopScheduledMonitoring, actions.StopScheduledMonitoring) &&
            ValidOrigin(origins.ExitBackground, actions.ExitBackground) && ValidOrigin(origins.SwitchClassroomMode, actions.SwitchClassroomMode, allowLegacy: true);
    }
    private static bool ValidOrigin(string origin, ClassroomActionRule? rule, bool allowLegacy = false) => rule is null ?
        origin == "LEGACY" || allowLegacy && origin == "CLASS" : origin is "CLASS" or "GRADE" && ValidActionRule(rule);
    private static bool ValidOverride(ClassroomActionOverride? value, bool allowLegacy = false) => value is null ||
        (value.Rule is null ? allowLegacy : ValidActionRule(value.Rule));
    private static bool ValidActionRules(ClassroomActionRules? actions) => actions is null ||
        ValidActionRule(actions.StopScheduledMonitoring) && ValidActionRule(actions.ExitBackground) &&
        (actions.SwitchClassroomMode is null || ValidActionRule(actions.SwitchClassroomMode));
    private static bool ValidActionRule(ClassroomActionRule? rule) => rule?.Mode switch
    {
        "VERIFY" => rule.Factor is "CLASS_PASSWORD" or "USB" or "ANY" or "ALL",
        "ALLOW" or "DENY" => rule.Factor is null,
        _ => false
    };

    private static ClassroomActionReceipt ValidateActionReceipt(ClassroomActionReceipt receipt, string schoolId, ClassroomActionChange change, Guid requestId)
    {
        if (receipt.RequestId != requestId || receipt.SchoolId != schoolId || receipt.TermId != change.TermId || receipt.ScopeKind != change.ScopeKind ||
            receipt.ScopeId != change.ScopeId || receipt.Operation != change.Operation || receipt.Revision <= 0 || receipt.ChangedClasses < 0 ||
            receipt.KeyId != (change.UsbKey?.KeyId ?? change.KeyId)) throw InvalidResponse();
        return receipt;
    }

    private static async Task<ConsoleApiException> ClassroomActionErrorAsync(HttpResponseMessage response, string requestId, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (status is 401 or 403) return HttpError(status, false);
        Envelope? envelope = null;
        try { envelope = await ReadEnvelopeAsync(response.Content, ct); }
        catch (JsonException) { }
        var code = envelope?.RequestId == requestId && envelope.ProtocolVersion == "0.16" ? envelope.Error?.Code : null;
        return new ConsoleApiException(status, code switch
        {
            "ACTION_POLICY_UPGRADE_REQUIRED" or "PROTOCOL_UNSUPPORTED" => "此 KV 尚未提供大屏安全策略，请升级服务并完成数据库迁移。",
            "ACTION_POLICY_CLIENT_UPGRADE_REQUIRED" => "此范围包含新版安全策略，请更新管理端后重新核对。",
            "ACTION_POLICY_TERM_CHANGED" or "ACTION_POLICY_SCOPE_NOT_FOUND" => "原学期、年级或班级已变化，请重新核对办理范围。",
            "ACTION_POLICY_STALE" or "REVISION_CONFLICT" => "策略、班级口令或大屏范围已变化，请重新预览。",
            "ACTION_POLICY_NO_CHANGE" => "当前已是此设置，可刷新核对。",
            "ACTION_POLICY_USB_REGISTERED" => "此 U 盘在本学期已登记，或盘上的原钥匙编号已使用过。请先核对登记状态。",
            "ACTION_POLICY_USB_LIMIT" => "此范围已有 32 支已登记 U 盘。",
            "ACTION_POLICY_USB_CHANGED" => "此 U 盘登记已变化，请刷新原范围。",
            "ACTION_POLICY_USB_REISSUE_UNAVAILABLE" => "无法核对这支 U 盘是本校已撤销的原钥匙，原文件保留。",
            "ACTION_POLICY_RECEIPT_NOT_FOUND" => "暂未查到本人的这笔回执。",
            "IDEMPOTENCY_CONFLICT" => "此请求编号已有其他办理记录，请核对原编号。",
            null when response.StatusCode == HttpStatusCode.NotFound => "此 KV 未提供大屏安全策略入口。",
            _ => HttpError(status, false).Message
        }, code);
    }
}
