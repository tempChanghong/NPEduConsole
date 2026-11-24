using System.ComponentModel;
using System.Diagnostics;

namespace NPEduConsole.App;

internal static class SchoolAdminBrowser
{
    internal static Uri? Link(string? adminUrl, string schoolId, string? termId, string section, string? screenId = null,
        string? classId = null, string? accountId = null)
    {
        if (!Uri.TryCreate(adminUrl, UriKind.Absolute, out var url) || url.UserInfo.Length != 0 ||
            url.Query.Length != 0 || url.Fragment.Length != 0 ||
            url.Scheme != Uri.UriSchemeHttps && !(url.IsLoopback && url.Scheme == Uri.UriSchemeHttp)) return null;
        var link = new UriBuilder(url) { Query = $"school={Uri.EscapeDataString(schoolId)}&section={section}" };
        if (termId is not null && section != "accounts") link.Query += "&term=" + Uri.EscapeDataString(termId);
        if (section == "accounts" && accountId is not null) link.Query += "&account=" + Uri.EscapeDataString(accountId);
        if (section == "screens" && screenId is not null)
        {
            link.Query += "&screen=" + Uri.EscapeDataString(screenId);
            if (classId is not null) link.Query += "&classId=" + Uri.EscapeDataString(classId);
        }
        return link.Uri;
    }

    internal static bool Open(Uri url)
    {
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { return false; }
    }
}
