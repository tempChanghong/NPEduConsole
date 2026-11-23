using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NPEduConsole.Core;

public sealed record SetupStatus(string State, bool CanStart, bool SupportsDesktopSetup, SetupCheck[] Checks);
public sealed record SetupCheck(string Code, string Message, string Severity, bool Ok);
public sealed record TransferSchool(string Id, string Code, string Name);
public sealed record TransferManifest(TransferSchool School, int FormatVersion, DateTimeOffset ExportedAt, Guid MigrationId);
public sealed record TransferScreen(string Id, string Name, string ClassName, bool IsActive, bool WebLogin, bool NativeNeedsPairing, bool ClassSecretNeedsReset);
public sealed record TransferRetained(int Photos, int ExitEvents);
public sealed record TransferOwnerRoute(string Name, string Method, bool Available);
public sealed record SchoolTransferPreview(TransferManifest Manifest, Dictionary<string, int> Counts, string[] Warnings, string? PreviewToken,
    TransferScreen[]? ScreenActions, TransferRetained? Retained, TransferOwnerRoute[]? OwnerRoutes);
public sealed record SchoolTransferReadiness(TransferSchool School, int FormatVersion, string Role, string ReauthMethod,
    Dictionary<string, int> Counts, TransferRetained Retained, TransferScreen[] ScreenActions, bool? HasActiveTerm = null);
public sealed record SchoolTransferReceipt(bool Imported, TransferSchool School, TransferManifest Manifest, Dictionary<string, int> Counts,
    TransferScreen[] ScreenActions, TransferRetained? Retained, TransferOwnerRoute[] OwnerRoutes);
public sealed record SetupOwner(string Id, string Name, string? LocalUsername = null, string? Provider = null);
public sealed record SetupTerm(string Id, string Name);
public sealed record SetupContext(TransferSchool School, SetupOwner Owner, Guid? MigrationId = null, SetupTerm? Term = null);
public sealed record SetupCreated(TransferSchool School, SetupOwner Account, SetupTerm? Term = null);
public sealed record SetupCompleted(string SchoolId, string State, DateTimeOffset CompletedAt, DateTimeOffset OwnerVerifiedAt, string Method);
public sealed record NewSchoolSetup(string SchoolName, string SchoolCode, string AdministratorName, string Username, string Pin,
    string TermName, int AcademicYear, int Semester, DateOnly? StartsAt, DateOnly? EndsAt,
    string TeacherAuthMode, bool AllowOAuthTeacherLogin, string? SharedPassword);
public sealed record ExportVerification(string ReauthId, string BrowserPath, int ExpiresIn)
{
    internal Guid SessionId { get; init; }
}

public sealed class SchoolSetupSession
{
    internal SchoolSetupSession(ConsoleClient client, string token) { Client = client; Token = token; }
    internal ConsoleClient Client { get; }
    internal string Token { get; }
}

public sealed partial class ConsoleClient
{
    public const long SchoolTransferMaxBytes = 64L * 1024 * 1024;
    private const string SetupPath = "api/v2/setup/";
    private const string TransferMime = "application/vnd.npclassworks.transfer+json";
    private sealed record SetupToken(string Token);
    private sealed record ExportState(string State);

    public Uri ServerUri => _http.BaseAddress!;

    public async Task<SetupStatus> GetSetupStatusAsync(CancellationToken ct)
    {
        try
        {
            var status = await ReadTransferAsync<SetupStatus>(HttpMethod.Get, SetupPath + "status", ct);
            if (status.State is not ("NEW" or "CONFIGURING" or "COMPLETED")) throw InvalidResponse();
            return status;
        }
        catch (ConsoleApiException error) when (error.StatusCode == 0 && error.Code is null)
        {
            throw new ConsoleApiException(502, "目标返回了网页或无法识别的初始化数据，请核对 API 网关。", "SETUP_STATUS_INVALID");
        }
    }

    public async Task<SchoolSetupSession> BeginSchoolSetupAsync(string key, CancellationToken ct)
    {
        var result = await ReadTransferAsync<SetupToken>(HttpMethod.Post, SetupPath + "session", ct, body: new { setupKey = key });
        if (string.IsNullOrWhiteSpace(result.Token) || result.Token.Length > 1024 || result.Token.Any(char.IsWhiteSpace))
            throw InvalidResponse();
        return new(this, result.Token);
    }

    public Task<SetupContext> GetSetupContextAsync(SchoolSetupSession setup, CancellationToken ct) =>
        ReadTransferAsync<SetupContext>(HttpMethod.Get, SetupPath + "context", ct, setup);

    public Task<SetupCreated> InitializeSchoolAsync(SchoolSetupSession setup, NewSchoolSetup input, CancellationToken ct) =>
        ReadTransferAsync<SetupCreated>(HttpMethod.Post, SetupPath + "initialize", ct, setup, body: input);

    public async Task<SetupCompleted> FinishSchoolSetupAsync(SchoolSetupSession setup, string schoolId, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await ReadTransferAsync<SetupCompleted>(HttpMethod.Post, SetupPath + "desktop-complete", ct, setup,
            session, new { schoolId });
        if (result.SchoolId != schoolId || result.State != "COMPLETED" || result.OwnerVerifiedAt == default || result.CompletedAt == default)
            throw InvalidResponse();
        return result;
    }

    public async Task<SchoolTransferReadiness> GetSchoolTransferReadinessAsync(string schoolId, CancellationToken ct)
    {
        var result = await ReadTransferAsync<SchoolTransferReadiness>(HttpMethod.Get, MigrationPath(schoolId) + "readiness", ct,
            session: CurrentSession);
        if (result.School.Id != schoolId || result.Role is not ("OWNER" or "ADMIN")) throw InvalidResponse();
        return result;
    }

    public async Task<ExportVerification> BeginTransferReauthenticationAsync(string schoolId, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await ReadTransferAsync<ExportVerification>(HttpMethod.Post, MigrationPath(schoolId) + "reauth", ct,
            session: session, body: new { requestId });
        if (!OAuthKey.IsMatch(result.ReauthId) || result.BrowserPath != "accounts/oauth/migration/" + result.ReauthId ||
            result.ExpiresIn is < 1 or > 300) throw InvalidResponse();
        return result with { SessionId = session.Id };
    }

    public async Task<string> ReadTransferReauthenticationAsync(string schoolId, ExportVerification auth, CancellationToken ct)
    {
        var session = CurrentSession;
        if (session.Id != auth.SessionId) throw new ConsoleApiException(401, "请在本次管理会话重新核验。");
        return (await ReadTransferAsync<ExportState>(HttpMethod.Get, MigrationPath(schoolId) + "reauth/" + Segment(auth.ReauthId),
            ct, session: session)).State;
    }

    public async Task SaveSchoolTransferAsync(string schoolId, Guid requestId, string passphrase, string? currentPin,
        ExportVerification? auth, string path, CancellationToken ct)
    {
        var session = CurrentSession;
        if (auth is not null && session.Id != auth.SessionId) throw new ConsoleApiException(401, "请在本次管理会话重新核验。");
        using var request = MakeTransferRequest(HttpMethod.Post, MigrationPath(schoolId) + "export", null, session);
        request.Content = JsonContent.Create(new { requestId, passphrase, currentPin, reauthId = auth?.ReauthId });
        using var client = CreateTransferHttpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await EnsureTransferSuccessAsync(response, timeout.Token);
            if (response.Content.Headers.ContentType?.MediaType != TransferMime ||
                response.Content.Headers.ContentLength is <= 0 or > SchoolTransferMaxBytes)
                throw new ConsoleApiException(502, "服务器未返回可保存的迁移包格式。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                long total = 0;
                int length;
                while ((length = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += length;
                    if (total > SchoolTransferMaxBytes) throw InvalidResponse();
                    await output.WriteAsync(buffer.AsMemory(0, length), timeout.Token);
                }
                if (total == 0 || response.Content.Headers.ContentLength is { } size && total != size)
                    throw new ConsoleApiException(502, "迁移包下载未完成，请重新导出。");
            }
            CheckCreationSession(session, ct);
            File.Move(temporary, path);
        }
        catch (HttpRequestException) { throw new ConsoleApiException(0, "连接在导出期间中断。请重新核验后导出。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ConsoleApiException(408, "导出超时，请重新核验后尝试。"); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<SchoolTransferPreview> PreviewSchoolTransferAsync(SchoolSetupSession setup, string path, string passphrase, CancellationToken ct)
    {
        var preview = await UploadTransferAsync<SchoolTransferPreview>(setup, "preview", path, passphrase, null, ct);
        if (preview.Manifest.FormatVersion is not (1 or 2) || preview.Manifest.MigrationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(preview.PreviewToken)) throw InvalidResponse();
        return preview;
    }

    public Task<SchoolTransferReceipt> ImportSchoolTransferAsync(SchoolSetupSession setup, SchoolTransferPreview preview,
        string path, string passphrase, CancellationToken ct) =>
        UploadTransferAsync<SchoolTransferReceipt>(setup, "import", path, passphrase, preview.PreviewToken, ct);

    public Task<SchoolTransferReceipt> GetSchoolTransferReceiptAsync(SchoolSetupSession setup, Guid migrationId, CancellationToken ct) =>
        ReadTransferAsync<SchoolTransferReceipt>(HttpMethod.Get, SetupPath + "migration/receipts/" + migrationId, ct, setup);

    private async Task<T> UploadTransferAsync<T>(SchoolSetupSession setup, string operation, string path, string passphrase,
        string? previewToken, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        if (input.Length is <= 0 or > SchoolTransferMaxBytes)
            throw new ConsoleApiException(413, "迁移包为空或超过 64 MB。", "MIGRATION_PACKAGE_SIZE_INVALID");
        using var request = MakeTransferRequest(HttpMethod.Post, SetupPath + "migration/" + operation, setup, null);
        request.Content = new StreamContent(input);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(TransferMime);
        request.Headers.Add("X-NPClassworks-Migration-Passphrase-B64", Base64Url(Encoding.UTF8.GetBytes(passphrase)));
        if (previewToken is not null) request.Headers.Add("X-NPClassworks-Migration-Preview", previewToken);
        using var upload = CreateTransferHttpClient();
        return await SendTransferAsync<T>(upload, request, ct);
    }

    private static string MigrationPath(string schoolId) => $"api/v2/admin/schools/{Segment(schoolId)}/migration/";

    private async Task<T> ReadTransferAsync<T>(HttpMethod method, string path, CancellationToken ct, SchoolSetupSession? setup = null,
        Session? session = null, object? body = null)
    {
        using var request = MakeTransferRequest(method, path, setup, session);
        if (body is not null) request.Content = JsonContent.Create(body);
        var result = await SendTransferAsync<T>(_http, request, ct);
        if (session is not null) CheckCreationSession(session, ct);
        return result;
    }

    private HttpRequestMessage MakeTransferRequest(HttpMethod method, string path, SchoolSetupSession? setup, Session? session)
    {
        if (setup is not null && !ReferenceEquals(setup.Client, this))
            throw new ArgumentException("初始化会话属于另一台服务器。", nameof(setup));
        var request = new HttpRequestMessage(method, path);
        if (setup is not null) request.Headers.Add("X-Classworks-Setup-Token", setup.Token);
        if (session is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        return request;
    }

    private HttpClient CreateTransferHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        BaseAddress = ServerUri, Timeout = TimeSpan.FromMinutes(3), MaxResponseContentBufferSize = 8 * 1024 * 1024
    };

    private static async Task<T> SendTransferAsync<T>(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await client.SendAsync(request, ct);
            await EnsureTransferSuccessAsync(response, ct);
            var envelope = await ReadEnvelopeAsync(response.Content, ct);
            return envelope is { Success: true } ? ReadData<T>(envelope) : throw InvalidResponse();
        }
        catch (JsonException) { throw InvalidResponse(); }
        catch (HttpRequestException) { throw new ConsoleApiException(0, "与服务器的连接中断，请先核对当前状态。", "TRANSFER_CONNECTION_FAILED"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ConsoleApiException(408, "服务器响应超时，请先核对当前状态。"); }
    }

    private static async Task EnsureTransferSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? code = null, message = null;
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (json.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (json.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String) code = value.GetString();
                if (json.RootElement.TryGetProperty("message", out value) && value.ValueKind == JsonValueKind.String) message = value.GetString();
            }
        }
        catch (JsonException) { }
        var status = (int)response.StatusCode;
        throw new ConsoleApiException(status, code is not null && (code.StartsWith("MIGRATION_", StringComparison.Ordinal) ||
            code.StartsWith("SETUP_", StringComparison.Ordinal) || code is "INVALID_BOOTSTRAP_KEY" or "SCHOOL_OWNER_REQUIRED") &&
            message is { Length: > 0 and <= 450 } ? message : HttpError(status, false).Message, code);
    }
}
