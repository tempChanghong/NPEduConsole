using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace NPEduConsole.Core;

public sealed class ConsoleClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
    private readonly HttpClient _http;
    private string? _accessToken;

    public ConsoleClient(string serverAddress)
    {
        if (!Uri.TryCreate(serverAddress.Trim(), UriKind.Absolute, out var address)
            || !IsTrustedTransport(address) || address.UserInfo.Length > 0
            || address.Fragment.Length > 0 || address.Query.Length > 0)
            throw new ConsoleApiException(0, "请输入 KV 服务的 HTTPS 地址。本机测试可使用 localhost 或回环 IP 的 HTTP 地址。");

        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = new Uri(address.AbsoluteUri.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = 32 * 1024 * 1024
        };
    }

    private static bool IsTrustedTransport(Uri uri) => uri.Scheme == Uri.UriSchemeHttps ||
        uri.Scheme == Uri.UriSchemeHttp && (uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    public async Task<IReadOnlyList<School>> LoginAsync(string schoolCode, string username, string password, CancellationToken ct)
    {
        if (_accessToken is not null) throw new ConsoleApiException(0, "请先退出当前账号。");
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
            _accessToken = token;
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
    private string Token => _accessToken ?? throw new ConsoleApiException(401, "请先登录管理账号。");

    public async Task<IReadOnlyList<Classroom>> GetClassroomsAsync(string schoolId, CancellationToken ct)
    {
        var token = Token;
        var path = "api/v2/admin/schools/" + Segment(schoolId);
        // This protected endpoint checks the current school grant even when there is no active term.
        var screens = await RequestAsync<ScreenRow[]>(HttpMethod.Get, path + "/classroom-screens", token, ct);
        var memberships = await MembershipsAsync(token, ct);
        var membership = memberships.FirstOrDefault(m => m.School.Id == schoolId && IsAdministrator(m));
        if (membership is null) throw new ConsoleApiException(403, "当前账号已无权查看该学校。");
        var term = membership.School.Terms.FirstOrDefault(t => t.Status == "ACTIVE");
        if (term is null) return [];

        var structureTask = RequestAsync<Structure>(HttpMethod.Get, path + "/academic-structure?termId=" + Segment(term.Id), token, ct);
        var devicesTask = ReadDevicesAsync(schoolId, token, ct);
        await Task.WhenAll(structureTask, devicesTask);
        var structure = await structureTask;
        if (structure.Term.Id != term.Id || structure.Term.Status != "ACTIVE")
            throw new ConsoleApiException(409, "学校已切换学期，请刷新班级列表。");

        // A screen account may already exist before NPEduTools pairs. Keep it visible in that state.
        var byScreen = (await devicesTask).ToLookup(d => (d.AdministrativeClassId, d.ScreenBindingId, d.BindingRevision));
        var byClass = screens.Where(s => s.IsActive && s.SchoolId == schoolId && s.AdministrativeClassId is not null)
            .ToLookup(s => s.AdministrativeClassId!);
        var classrooms = new List<Classroom>();
        foreach (var room in structure.AdministrativeClasses.Where(c => c.IsActive))
        {
            var roomScreens = new List<Screen>();
            foreach (var screen in byClass[room.Id])
            {
                var matched = byScreen[(room.Id, screen.Id, screen.NpepBindingRevision)];
                var connected = matched.Select(d => new Device(d.DeviceId, d.DeviceName, d.Connectivity, d.LastSeenAt));
                roomScreens.Add(new Screen(screen.Id, screen.Name, connected.ToArray()));
            }
            classrooms.Add(new Classroom(room.Id, room.Name, room.Grade?.Name, roomScreens));
        }
        return classrooms;
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
        var token = Token;
        var schoolPath = $"api/v2/npep/schools/{Segment(schoolId)}";
        var path = $"{schoolPath}/devices/{Segment(deviceId)}/equipment";
        EquipmentResponse response;
        try
        {
            response = await RequestAsync<EquipmentResponse>(HttpMethod.Get, path, token, ct, version: "0.16");
        }
        catch (ConsoleApiException ex) when (ex.StatusCode == 401)
        {
            // KV also uses 401 when a listed device expires or its binding changes.
            // The same school's overview authenticates the manager independently of that device.
            await RequestAsync<OverviewPage>(HttpMethod.Get, schoolPath + "/device-overview?limit=1", token, ct, version: "0.1");
            throw new ConsoleApiException(409, "这台设备的绑定已改变或已过期，请刷新班级列表。");
        }
        var policy = response.Policy;
        var inventory = response.Inventory;
        string? name = null;
        if (policy.MicrophoneId is not null)
            name = inventory?.Microphones.FirstOrDefault(m => m.Id == policy.MicrophoneId)?.Name ?? "已指定（上报清单中无名称）";
        return new Equipment(policy.Revision, inventory?.AppliedRevision, policy.Locked, inventory?.Unlocked, name,
            inventory?.ReceivedAt, inventory?.Error);
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        var token = Interlocked.Exchange(ref _accessToken, null);
        if (token is not null) await EndSessionAsync(token, ct);
    }

    private Task EndSessionAsync(string token, CancellationToken ct) =>
        RequestAsync<JsonElement>(HttpMethod.Post, "accounts/logout", token, ct);

    private static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Contains('/') || value.Contains('\\'))
            throw InvalidResponse();
        return Uri.EscapeDataString(value);
    }

    private async Task<T> RequestAsync<T>(HttpMethod method, string path, string? token, CancellationToken ct,
        string? version = null, object? body = null)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        var requestId = Guid.NewGuid().ToString();
        if (version is not null)
        {
            request.Headers.Add("X-NPEP-Version", version);
            request.Headers.Add("X-Request-Id", requestId);
        }
        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw HttpError((int)response.StatusCode, token is null);
            var envelope = await response.Content.ReadFromJsonAsync<Envelope>(Json, ct) ?? throw InvalidResponse();
            if (version is null ? envelope.Success != true :
                envelope.ProtocolVersion != version || envelope.RequestId != requestId || envelope.ServerTime is null)
                throw InvalidResponse();
            if (typeof(T) == typeof(JsonElement)) return (T)(object)envelope.Data;
            if (envelope.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) throw InvalidResponse();
            return envelope.Data.Deserialize<T>(Json) ?? throw InvalidResponse();
        }
        catch (JsonException) { throw InvalidResponse(); }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ConsoleApiException(0, "KV 服务响应超时，请检查连接后重试。"); }
        catch (HttpRequestException) { throw new ConsoleApiException(0, "无法连接 KV 服务，请检查地址、网络和服务证书。"); }
    }

    private static ConsoleApiException InvalidResponse() => new(0, "KV 服务返回的数据与当前控制台不兼容，请核对服务地址或刷新。");
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
        _accessToken = null;
        _http.Dispose();
    }
}
