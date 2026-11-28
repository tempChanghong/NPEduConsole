using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NPEduConsole.Core;

public sealed record SchoolAccessPresetFile
{
    private const string Suffix = ".np-school.json";
    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private SchoolAccessPresetFile(string backendUrl, string schoolName, string? schoolCode)
    {
        BackendUrl = backendUrl;
        SchoolName = schoolName;
        SchoolCode = schoolCode;
    }

    public string BackendUrl { get; }
    public string SchoolName { get; }
    public string? SchoolCode { get; }
    public string SuggestedFileName => "学校接入" + Suffix;

    internal static SchoolAccessPresetFile Create(Uri backend, SchoolRow school)
    {
        if (!backend.IsAbsoluteUri || backend.Scheme is not ("https" or "http") || backend.Host.Length == 0 ||
            backend.AbsolutePath != "/" || backend.Query.Length != 0 || backend.Fragment.Length != 0 || backend.UserInfo.Length != 0)
            throw new IOException("当前 KV 地址无法用于教室端预配置。请核对学校的后端根地址，再在 NPDownloader 新建配置。");
        string address = backend.GetLeftPart(UriPartial.Authority);
        if (address.Length > 2048)
            throw new IOException("学校后端地址超出教室端支持的长度。");
        string name = school.Name.Trim();
        string? code = school.Code?.Trim();
        if (name.Length is < 1 or > 191 || name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) ||
            code is not null && !Regex.IsMatch(code, @"\A[A-Za-z0-9._:-]{1,64}\z", RegexOptions.CultureInvariant))
            throw new IOException("当前学校的名称或校码无法用于教室端预配置。");
        return new(address, name, code);
    }

    public async Task ExportAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(path) is not { } name || name.Length <= Suffix.Length ||
            !name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("请选择以 .np-school.json 结尾的文件名。");
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("保存位置已有同名文件，请另选文件名。");
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".school-access-{Guid.NewGuid():N}.tmp");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
            { schemaVersion = 1, backendUrl = BackendUrl, schoolName = SchoolName, schoolCode = SchoolCode }, Format);
        bool created = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path);
            created = false;
        }
        catch (IOException) when (File.Exists(path) || Directory.Exists(path))
        { throw new IOException("保存位置已有同名文件，请另选文件名。"); }
        finally { if (created) File.Delete(temporary); }
    }
}

public sealed partial class ConsoleClient
{
    public async Task<SchoolAccessPresetFile> ReadSchoolAccessPresetAsync(string schoolId, CancellationToken ct)
    {
        var session = CurrentSession;
        var memberships = await MembershipsAsync(session.Token, ct).ConfigureAwait(false);
        CheckCreationSession(session, ct);
        var matches = memberships.Where(row => row.School.Id == schoolId && IsAdministrator(row)).ToArray();
        if (matches.Length == 0) throw new ConsoleApiException(403, "当前账号无法管理所选学校。");
        if (matches.Length != 1) throw InvalidResponse();
        return SchoolAccessPresetFile.Create(_http.BaseAddress!, matches[0].School);
    }
}
