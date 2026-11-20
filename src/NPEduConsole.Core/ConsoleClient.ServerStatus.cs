namespace NPEduConsole.Core;

public sealed partial class ConsoleClient
{
    public async Task<ServerStatus> GetServerStatusAsync(CancellationToken ct)
    {
        var session = CurrentSession;
        var status = await ServerStatusClient.CheckSchoolSessionAsync(_http, session.Token, ct);
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_session, session)) throw new ConsoleApiException(401, "登录已失效，请重新登录管理账号。");
        return status;
    }
}
