using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NPEduConsole.Core;

public sealed partial class ConsoleClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
    private readonly HttpClient _http;
    private sealed class Session(string token)
    {
        private string _token = token;
        public string Token => Volatile.Read(ref _token);
        public Guid Id { get; } = Guid.NewGuid();

        public void Renew(string previous, string replacement) =>
            Interlocked.CompareExchange(ref _token, replacement, previous);
    }
    private Session? _session;

    private sealed class AccountHttpHandler(Func<Session?> currentSession) : HttpClientHandler
    {
        private static readonly Regex AccountToken = new(@"\A[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\z", RegexOptions.NonBacktracking);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var session = currentSession();
            var previous = session?.Token;
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            var headers = response.Headers.NonValidated;
            if (session is not null && previous is not null && response.IsSuccessStatusCode &&
                request.Headers.Authorization is { Scheme: "Bearer" } bearer && bearer.Parameter == previous &&
                headers.TryGetValues("X-Token-Refreshed", out var refreshed) && refreshed.ToString() == "true" &&
                headers.TryGetValues("X-New-Access-Token", out var values) && values.Count == 1)
            {
                var replacement = values.ToString();
                // Late responses can update only their original session and only the token they used.
                if (replacement.Length <= 8192 && AccountToken.IsMatch(replacement)) session.Renew(previous, replacement);
            }
            return response;
        }
    }

    public ConsoleClient(string serverAddress)
    {
        var address = NormalizeKvAddress(serverAddress);
        _http = new HttpClient(new AccountHttpHandler(() => _session) { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = address,
            Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = 32 * 1024 * 1024
        };
    }

    internal static Uri NormalizeKvAddress(string serverAddress)
    {
        if (!Uri.TryCreate(serverAddress.Trim(), UriKind.Absolute, out var address)
            || !IsTrustedTransport(address) || address.UserInfo.Length > 0
            || address.Fragment.Length > 0 || address.Query.Length > 0)
            throw new ConsoleApiException(0, "请输入 KV 服务的 HTTPS 地址。本机测试可使用 localhost 或回环 IP 的 HTTP 地址。");
        return new Uri(address.AbsoluteUri.TrimEnd('/') + "/");
    }

    private static bool IsTrustedTransport(Uri uri) => uri.Scheme == Uri.UriSchemeHttps ||
        uri.Scheme == Uri.UriSchemeHttp && (uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    public async Task<IReadOnlyList<School>> LoginAsync(string schoolCode, string username, string password, CancellationToken ct)
    {
        if (_session is not null) throw new ConsoleApiException(0, "请先退出当前账号。");
        var credentials = new { schoolCode = schoolCode.Trim(), username = username.Trim(), password };
        var login = await RequestAsync<LoginResponse>(HttpMethod.Post, "accounts/local/login", null, ct, body: credentials);
        var token = login.AccessToken;
        try
        {
            var memberships = await MembershipsAsync(token, ct);
            var schools = memberships.Where(IsAdministrator).Select(m => new School(m.School.Id, m.School.Name)).ToArray();
            if (schools.Length == 0)
                throw new ConsoleApiException(403, "此账号没有可管理的学校。请使用已获学校管理权限的账号登录。");
            ct.ThrowIfCancellationRequested();
            _session = new Session(token);
            return schools;
        }
        catch
        {
            // Login created a KV session even when the account has no school management grant.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await EndSessionAsync(token, timeout.Token); }
            catch (Exception ex) when (ex is ConsoleApiException or OperationCanceledException or ObjectDisposedException) { }
            throw;
        }
    }

    private Task<Membership[]> MembershipsAsync(string token, CancellationToken ct) =>
        RequestAsync<Membership[]>(HttpMethod.Get, "api/v2/me/schools", token, ct);

    private static bool IsAdministrator(Membership membership) => membership.Role is "OWNER" or "ADMIN";
    private Session CurrentSession => _session ?? throw new ConsoleApiException(401, "请先登录管理账号。");

    public async Task<IReadOnlyList<Classroom>> GetClassroomsAsync(string schoolId, CancellationToken ct)
    {
        var session = CurrentSession;
        var token = session.Token;
        var path = "api/v2/admin/schools/" + Segment(schoolId);
        // This protected endpoint checks the current school grant even when there is no active term.
        var screens = await RequestAsync<ScreenRow[]>(HttpMethod.Get, path + "/classroom-screens", token, ct);
        var memberships = await MembershipsAsync(token, ct);
        var membership = memberships.FirstOrDefault(m => m.School.Id == schoolId && IsAdministrator(m));
        if (membership is null) throw new ConsoleApiException(403, "当前账号已无权查看该学校。");
        var term = membership.School.Terms.FirstOrDefault(t => t.Status == "ACTIVE");
        if (term is null) return [];

        var structureTask = RequestAsync<Structure>(HttpMethod.Get, path + "/academic-structure?termId=" + Segment(term.Id), token, ct);
        var devicesTask = ReadOptionalDevicesAsync(schoolId, token, ct);
        await Task.WhenAll(structureTask, devicesTask);
        var structure = await structureTask;
        if (structure.Term.Id != term.Id || structure.Term.Status != "ACTIVE")
            throw new ConsoleApiException(409, "学校已切换学期，请刷新班级列表。");

        // A screen account may already exist before NPEduTools pairs. Keep it visible in that state.
        var devices = await devicesTask;
        var byScreen = (devices ?? []).ToLookup(d => (d.AdministrativeClassId, d.ScreenBindingId, d.BindingRevision));
        var byClass = screens.Where(s => s.SchoolId == schoolId && s.AdministrativeClassId is not null)
            .ToLookup(s => s.AdministrativeClassId!);
        var classrooms = new List<Classroom>();
        foreach (var room in structure.AdministrativeClasses.Where(c => c.IsActive))
        {
            var roomScreens = new List<Screen>();
            var bindings = byClass[room.Id];
            foreach (var screen in bindings.Where(screen => screen.IsActive))
            {
                var matched = byScreen[(room.Id, screen.Id, screen.NpepBindingRevision)];
                var connected = matched.Select(d => new Device(d.DeviceId, d.DeviceName, d.Connectivity, d.LastSeenAt));
                roomScreens.Add(new Screen(screen.Id, screen.Name, connected.ToArray()) { DeviceOverviewUnavailable = devices is null });
            }
            classrooms.Add(new Classroom(room.Id, room.Name, room.Grade?.Name, roomScreens)
            {
                TermId = term.Id,
                GradeId = room.Grade?.Id,
                Code = room.Code,
                HasInactiveScreens = bindings.Any(screen => !screen.IsActive),
                FirstScreen = devices is not null && !bindings.Any() ? new(session.Id, schoolId, term.Id, room.Id) : null
            });
        }
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(session, _session)) throw new ConsoleApiException(401, "登录会话已改变，请重新登录。");
        return classrooms;
    }

    private async Task<List<DeviceRow>?> ReadOptionalDevicesAsync(string schoolId, string token, CancellationToken ct)
    {
        try { return await ReadDevicesAsync(schoolId, token, ct); }
        catch (ConsoleApiException ex) when (ex.StatusCode is 404 or 426 or 503) { return null; }
    }

    private async Task<List<DeviceRow>> ReadDevicesAsync(string schoolId, string token, CancellationToken ct)
    {
        var devices = new List<DeviceRow>();
        var path = "api/v2/npep/schools/" + Segment(schoolId) + "/device-overview?limit=100";
        string? cursor = null;
        do
        {
            var url = cursor is null ? path : path + "&cursor=" + Segment(cursor);
            var page = await RequestAsync<OverviewPage>(HttpMethod.Get, url, token, ct, version: "0.1");
            devices.AddRange(page.Items.Where(d => d.SchoolId == schoolId && d.State == "ACTIVE" && !d.BindingChanged));
            if (page.NextCursor is not null && (!Guid.TryParseExact(page.NextCursor, "D", out _)
                || string.CompareOrdinal(page.NextCursor, cursor) <= 0))
                throw InvalidResponse();
            cursor = page.NextCursor;
        } while (cursor is not null);
        return devices;
    }

    public async Task<Equipment> GetEquipmentAsync(string schoolId, string deviceId, CancellationToken ct)
    {
        var session = CurrentSession;
        var started = Stopwatch.GetTimestamp();
        var envelope = await EquipmentRequestAsync(HttpMethod.Get, schoolId, deviceId, session.Token, ct);
        var response = ReadData<EquipmentResponse>(envelope);
        var policy = response.Policy;
        var inventory = response.Inventory;
        var microphones = inventory?.Microphones ?? [];
        string? NameFor(string? id) => id is null ? null : microphones.FirstOrDefault(m => m.Id == id)?.Name;
        var name = policy.MicrophoneId is null ? null : NameFor(policy.MicrophoneId) ?? "已指定（上报清单中无名称）";
        return new Equipment(policy.Revision, inventory?.AppliedRevision, policy.Locked, inventory?.Unlocked, name,
            inventory?.ReceivedAt, inventory?.Error)
        {
            Source = new EquipmentSource(session.Id, schoolId, deviceId, policy, envelope.ServerTime!.Value, started),
            Microphones = Array.AsReadOnly(microphones),
            ReportedMicrophoneId = inventory?.SelectedMicrophoneId,
            ReportedMicrophoneName = inventory?.SelectedMicrophoneId is { } selected
                ? NameFor(selected) ?? "已上报选择，当前清单未列出此麦克风" : null,
            DeviceSupportsLocalMicrophoneChange = inventory?.SupportsLocalMicrophoneChange == true,
            LocalMicrophoneOverrideId = inventory?.LocalMicrophoneOverride
        };
    }

    public async Task<long> ChangeMicrophoneAsync(Equipment snapshot, string microphoneId, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        var source = EquipmentScope(snapshot, session);
        if (!snapshot.IsInventoryFresh)
            throw new ConsoleApiException(409, "设备清单已过期，请等待设备重新上报并刷新。", "EQUIPMENT_INVENTORY_STALE");
        if (!snapshot.Microphones.Any(m => m.Id == microphoneId))
            throw new ConsoleApiException(409, "所选麦克风不在设备清单中，请刷新后重新选择。", "EQUIPMENT_UNAVAILABLE");
        if (!snapshot.CanSelectSchoolMicrophone(microphoneId) || requestId == Guid.Empty)
            throw new ConsoleApiException(400, "请选择另一支麦克风并确认更改。", "INVALID_CHANGE");

        return await PublishEquipmentAsync(session, source, microphoneId, allowLocalChange: null, requestId, ct);
    }

    public async Task<long> SetLocalMicrophoneChangeAsync(Equipment snapshot, bool allow, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        var source = EquipmentScope(snapshot, session);
        if (!snapshot.ServiceSupportsLocalMicrophoneChange)
            throw new ConsoleApiException(409, "当前 KV 服务尚未支持现场换麦授权。", "LOCAL_MICROPHONE_CHANGE_UNSUPPORTED");
        if (source.Policy.AllowLocalMicrophoneChange == allow || requestId == Guid.Empty)
            throw new ConsoleApiException(400, "学校当前策略已经是此授权状态。", "INVALID_CHANGE");
        if (allow && !snapshot.CanGrantLocalMicrophoneChange)
            throw new ConsoleApiException(409, "本次授权要求已启用学校锁定策略，并收到支持现场换麦的设备新回执。请刷新后核对。",
                "LOCAL_MICROPHONE_CHANGE_UNAVAILABLE");

        return await PublishEquipmentAsync(session, source, source.Policy.MicrophoneId, allow, requestId, ct);
    }

    private static EquipmentSource EquipmentScope(Equipment snapshot, Session session) =>
        snapshot.Source is { } source && source.SessionId == session.Id
            ? source
            : throw new ConsoleApiException(409, "请在当前账号下重新读取设备。", "SNAPSHOT_CHANGED");

    private async Task<long> PublishEquipmentAsync(Session session, EquipmentSource source, string? microphoneId,
        bool? allowLocalChange, Guid requestId, CancellationToken ct)
    {
        var previous = source.Policy;
        var body = new EquipmentChange(requestId, previous.Revision, microphoneId, previous.CameraId, previous.Locked, allowLocalChange);
        var expectedAuthorization = allowLocalChange ?? previous.AllowLocalMicrophoneChange;
        try
        {
            var envelope = await EquipmentRequestAsync(HttpMethod.Post, source.SchoolId, source.DeviceId, session.Token, ct, body, requestId);
            var saved = ReadData<Policy>(envelope);
            if (saved.Revision != previous.Revision + 1 || saved.MicrophoneId != microphoneId ||
                saved.CameraId != previous.CameraId || saved.Locked != previous.Locked ||
                (expectedAuthorization is { } expected ? saved.AllowLocalMicrophoneChange != expected : saved.AllowLocalMicrophoneChange == true))
                throw InvalidResponse();
            return saved.Revision;
        }
        catch (ConsoleApiException ex) when (ex.StatusCode is 0 or 408 or >= 500)
        {
            throw new ConsoleApiException(ex.StatusCode, "发布结果尚未确认，请重新读取该设备的学校配置后再决定是否提交。", "PUBLISH_UNCONFIRMED");
        }
    }

    private async Task<Envelope> EquipmentRequestAsync(HttpMethod method, string schoolId, string deviceId,
        string token, CancellationToken ct, object? body = null, Guid? requestId = null)
    {
        var schoolPath = $"api/v2/npep/schools/{Segment(schoolId)}";
        var path = $"{schoolPath}/devices/{Segment(deviceId)}/equipment";
        try
        {
            return await SendAsync(method, path, token, ct, "0.16", body, requestId);
        }
        catch (ConsoleApiException ex) when (ex.StatusCode == 401)
        {
            // KV also uses 401 when a listed device expires or its binding changes.
            // The same school's overview authenticates the manager independently of that device.
            await RequestAsync<OverviewPage>(HttpMethod.Get, schoolPath + "/device-overview?limit=1", token, ct, version: "0.1");
            throw new ConsoleApiException(409, "这台设备的绑定已改变或已过期，请刷新班级列表。");
        }
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null) await EndSessionAsync(session.Token, ct);
    }

    private Task EndSessionAsync(string token, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "accounts/logout", token, ct);

    private static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Contains('/') || value.Contains('\\'))
            throw InvalidResponse();
        return Uri.EscapeDataString(value);
    }

    private async Task<T> RequestAsync<T>(HttpMethod method, string path, string? token, CancellationToken ct,
        string? version = null, object? body = null) =>
        ReadData<T>(await SendAsync(method, path, token, ct, version, body));

    private static T ReadData<T>(Envelope envelope)
    {
        if (envelope.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) throw InvalidResponse();
        try { return envelope.Data.Deserialize<T>(Json) ?? throw InvalidResponse(); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    private async Task<Envelope> SendAsync(HttpMethod method, string path, string? token, CancellationToken ct,
        string? version = null, object? body = null, Guid? operationId = null)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        var requestId = (operationId ?? Guid.NewGuid()).ToString();
        if (version is not null)
        {
            request.Headers.Add("X-NPEP-Version", version);
            request.Headers.Add("X-Request-Id", requestId);
        }
        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                if (IsTermPreparationPath(path))
                    throw await TermPreparationErrorAsync(response, ct);
                if (IsClassBatchPath(path))
                    throw await ClassBatchErrorAsync(response, ct);
                if (IsClassMaintenancePath(path))
                    throw ClassMaintenanceError(status, await AcademicErrorCodeAsync(response, ct));
                if (IsCurrentTeachingPath(path))
                    throw CurrentTeachingError(status, await AcademicErrorCodeAsync(response, ct));
                if (IsCurrentStaffPath(path))
                    throw CurrentStaffError(status, await AcademicErrorCodeAsync(response, ct));
                if (IsGradePlanPath(path))
                    throw GradePlanError(status, await AcademicErrorCodeAsync(response, ct));
                if (IsCourseGroupsPath(path))
                    throw CourseGroupError(status, await AcademicErrorCodeAsync(response, ct));
                if (IsTeacherAccountsPath(path))
                    throw TeacherAccountError(status, await AcademicErrorCodeAsync(response, ct));
                if (IsSubjectSetupPath(path))
                    throw SubjectSetupError(status, await AcademicErrorCodeAsync(response, ct));
                if (IsSchoolSubjectPath(path))
                    throw SchoolSubjectError(status, await AcademicErrorCodeAsync(response, ct));
                if (path.EndsWith(CurrentClassesSuffix, StringComparison.Ordinal) && status is 404 or 409 or 422)
                    throw ClassCreationError(status, await AcademicErrorCodeAsync(response, ct));
                if (IsGradeRenamePath(path))
                    throw GradeRenameError(status, await AcademicErrorCodeAsync(response, ct));
                if ((path.EndsWith(CurrentGradesSuffix, StringComparison.Ordinal) || path.Contains(CurrentGradesSuffix + "/", StringComparison.Ordinal)) &&
                    status is 404 or 409 or 422 or 426)
                    throw GradeCreationError(status, await AcademicErrorCodeAsync(response, ct));
                if (version is not null && status == 409)
                {
                    var error = await response.Content.ReadFromJsonAsync<Envelope>(Json, ct);
                    if (error?.RequestId == requestId && error.ProtocolVersion == version)
                        throw ProtocolConflict(error.Error?.Code);
                }
                if (version is null && method == HttpMethod.Put && status is 409 or 422 or 428)
                {
                    var code = await AcademicErrorCodeAsync(response, ct);
                    throw new ConsoleApiException(status, code switch
                    {
                        "CLASS_ROSTER_CLASS_INACTIVE" => "该班已停用，不能保存名单。",
                        "CLASS_ROSTER_TERM_CHANGED" => "该班所属学期已切换，请核对新学期的班级。",
                        "CLASS_ROSTER_CONFLICT" => "其他管理员或大屏已修改名单，请核对最新名单。",
                        "CLASS_ROSTER_VERSION_REQUIRED" => "KV 需要名单版本，请重新载入名单。",
                        "CLASS_ROSTER_STUDENT_INVALID" => "学生身份与当前班级不匹配，请重新核对。",
                        "CLASS_ROSTER_INVALID" or "CLASS_ROSTER_TOO_LARGE" or "CLASS_ROSTER_NAME_REQUIRED" or
                        "CLASS_ROSTER_DUPLICATE_NUMBER" or "CLASS_ROSTER_DUPLICATE_ID" =>
                            "KV 未接受这份名单，请核对姓名、学号、人数和重复项目。",
                        _ => HttpError(status, false).Message
                    }, code);
                }
                throw HttpError(status, token is null);
            }
            var envelope = await response.Content.ReadFromJsonAsync<Envelope>(Json, ct) ?? throw InvalidResponse();
            if (version is null ? envelope.Success != true :
                envelope.ProtocolVersion != version || envelope.RequestId != requestId || envelope.ServerTime is null)
                throw InvalidResponse();
            return envelope;
        }
        catch (JsonException) { throw InvalidResponse(); }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ConsoleApiException(0, "KV 服务响应超时，请检查连接后重试。"); }
        catch (HttpRequestException) { throw new ConsoleApiException(0, "无法连接 KV 服务，请检查地址、网络和服务证书。"); }
    }

    private static async Task<string?> AcademicErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.String ? code.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static ConsoleApiException InvalidResponse() => new(0, "KV 服务返回的数据与当前控制台不兼容，请核对服务地址或刷新。");
    private static ConsoleApiException ProtocolConflict(string? code) => new(409, code switch
    {
        "FIRST_SCREEN_EXISTS" => "该班已有大屏记录，请刷新并核对现有大屏。",
        "REVISION_CONFLICT" => "学校配置已发生变化，请刷新并核对最新设置，再重新选择。",
        "BINDING_OCCUPIED" => "这台大屏已有设备接入，请刷新班级列表。",
        "STUDY_SETTING_VERSION_CONFLICT" or "STUDY_PREVIEW_CHANGED" => "学校自习安排或其影响范围已变化，请刷新安排并重新预览。",
        "STUDY_SETTING_UPGRADE_REQUIRED" => "该班已有需要新版完整编辑器处理的自习策略。请到旧学校管理页核对。",
        "TERM_NOT_ACTIVE" => "该学期已变更，请刷新班级列表后重新选择。",
        "STUDY_SOURCE_UNAVAILABLE" => "原策略使用的作业来源已不可用。请在旧学校管理页核对原策略。",
        "STUDY_LEGACY_NOISE_ACTIVE" => "本班仍使用旧版定时噪音监测，请先在旧学校管理页核对迁移。",
        "EQUIPMENT_INVENTORY_STALE" => "设备清单已过期，请等待设备重新上报并刷新。",
        "EQUIPMENT_UNAVAILABLE" => "配置涉及的设备已不在清单中，请刷新并核对麦克风和摄像头。",
        "LOCAL_MICROPHONE_CHANGE_UNSUPPORTED" => "设备尚未上报支持现场换麦，请更新设备后刷新。",
        "LOCAL_MICROPHONE_CHANGE_UNAVAILABLE" => "该设备当前无法接受现场换麦授权，请刷新后再核对。",
        "IDEMPOTENCY_CONFLICT" => "这次请求与先前提交的操作不一致，请刷新后核对。",
        "UPDATE_ALREADY_ENROLLED" => "学校已经发出邀请，请刷新查看设备的纳管状态。",
        "UPDATE_ENROLLMENT_CHANGED" => "当前纳管邀请已变化，请刷新核对最新状态。",
        "UPDATE_NOT_MANAGED" => "这台设备尚未完成纳管确认，请先在教室端确认。",
        "UPDATE_ALREADY_PENDING" => "这台设备已有更新任务，请刷新查看进度。",
        "UPDATE_NOT_CANCELLABLE" => "当前任务已经不能取消，请刷新查看进度。",
        "UPDATE_VERSION_UNKNOWN" => "学校尚未收到本机当前版本，请等教室端上报后刷新。",
        "UPDATE_CATALOG_UNAVAILABLE" => "学校更新目录暂不可用，请稍后刷新。",
        "UPDATE_RELEASE_UNAVAILABLE" => "所选版本已不在学校更新目录，请刷新后重新选择。",
        "UPDATE_NOT_NEWER" => "所选版本必须高于终端实际运行的版本。",
        "UPDATE_ROLLBACK_UNAVAILABLE" => "学校更新目录中没有当前版本的恢复包，暂时无法批准更新。",
        _ => "绑定或设备状态已改变，请刷新。"
    }, code);
    private static ConsoleApiException HttpError(int status, bool login) => new(status, status switch
    {
        401 when login => "学校代码、账号或密码不正确。",
        401 => "登录已失效，请重新登录管理账号。",
        403 => "当前账号没有执行此操作的权限，请核对管理范围。",
        404 => "找不到目标或接口，请刷新并核对 KV 地址。",
        409 or 410 => "绑定或设备状态已改变，请刷新。",
        426 => "KV 服务与当前控制台的 NPEP 协议版本不兼容。",
        429 => "请求过于频繁，请稍后重试。",
        503 => "学校互联服务暂不可用，请稍后刷新。",
        >= 300 and < 400 => "KV 地址发生重定向，请直接输入目标服务的 HTTPS 地址。",
        >= 500 => "KV 服务暂时无法完成请求，请稍后重试。",
        _ => $"请求未成功（HTTP {status}），请核对输入后重试。"
    });

    public void Dispose()
    {
        _session = null;
        _http.Dispose();
    }
}
