using System.Net.Http.Headers;

namespace NPEduConsole.Core;

public sealed record StudyPhotoRecord(string EventId, string Reason, DateTimeOffset CapturedAt, DateTimeOffset ExpiresAt,
    string? ClassId = null, string? DeviceId = null, string? State = null);

public sealed record StudyExitRecord(string EventId, string Reason, DateTimeOffset CreatedAt, string? PhotoEventId,
    string PhotoState, DateTimeOffset? ExpiresAt = null, string? ClassId = null, string? DeviceId = null, string? SchoolNow = null);

public sealed record StudyRecordPage<T>(T[] Items, string? Cursor);

public sealed partial class ConsoleClient
{
    private static string StudyRecordsPath(StudyEvidenceScope scope, string resource) =>
        $"api/v2/npep/schools/{Segment(scope.SchoolId)}/{resource}";

    private static string RecordsQuery(StudyEvidenceScope scope, DateOnly? date = null, string? cursor = null) =>
        SeatingQuery(scope) + (date is { } day ? "&date=" + StudyDate(day) : "") +
        (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));

    public async Task<StudyRecordPage<StudyPhotoRecord>> ReadStudyPhotosAsync(StudyEvidenceScope scope, DateOnly? date, string? cursor, CancellationToken ct)
    {
        var session = CurrentSession;
        var page = await RequestAsync<StudyRecordPage<StudyPhotoRecord>>(HttpMethod.Get,
            StudyRecordsPath(scope, "study-photos") + RecordsQuery(scope, date, cursor), session.Token, ct, "0.15");
        CheckCreationSession(session, ct);
        VerifyRecordPage(page, cursor, row => row.ClassId == scope.ClassId && ValidRecordId(row.EventId));
        return page;
    }

    public async Task<StudyRecordPage<StudyExitRecord>> ReadStudyExitsAsync(StudyEvidenceScope scope, DateOnly? date, string? cursor, CancellationToken ct)
    {
        var session = CurrentSession;
        var page = await RequestAsync<StudyRecordPage<StudyExitRecord>>(HttpMethod.Get,
            StudyRecordsPath(scope, "study-exit-events") + RecordsQuery(scope, date, cursor), session.Token, ct, "0.15");
        CheckCreationSession(session, ct);
        VerifyRecordPage(page, cursor, row => row.ClassId == scope.ClassId && ValidRecordId(row.EventId) &&
            (row.PhotoEventId is null || row.PhotoEventId == row.EventId));
        return page;
    }

    private static void VerifyRecordPage<T>(StudyRecordPage<T> page, string? cursor, Func<T, bool> valid) =>
        _ = page.Items.Length <= 30 && page.Items.All(item => item is not null && valid(item)) &&
            (page.Cursor is null || page.Cursor.Length is > 0 and <= 256 && page.Cursor != cursor)
            ? true : throw InvalidResponse();

    private static bool ValidRecordId(string id) => Guid.TryParseExact(id, "D", out _);

    public async Task<byte[]> ReadStudyPhotoAsync(StudyEvidenceScope scope, string eventId, bool download, CancellationToken ct)
    {
        if (!ValidRecordId(eventId)) throw new ArgumentException("图片编号无效。");
        var session = CurrentSession;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, StudyRecordsPath(scope, $"study-photos/{eventId}/image") +
            RecordsQuery(scope) + (download ? "&download=1" : ""));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("X-NPEP-Version", "0.15");
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("D"));
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw HttpError((int)response.StatusCode, false);
            const int maxBytes = 262144;
            if (response.Content.Headers.ContentType?.MediaType != "image/jpeg" ||
                response.Content.Headers.ContentLength is 0 or > maxBytes) throw InvalidResponse();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var output = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, token)) != 0)
            {
                if (output.Length + count > maxBytes) throw InvalidResponse();
                output.Write(chunk, 0, count);
            }
            CheckCreationSession(session, ct);
            var bytes = output.ToArray();
            if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) throw InvalidResponse();
            return bytes;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ConsoleApiException(0, "照片读取超时，请重新核对。"); }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { throw new ConsoleApiException(0, "照片读取中断，请检查连接后重试。"); }
    }

    public async Task DeleteStudyPhotoAsync(StudyEvidenceScope scope, string eventId, CancellationToken ct)
    {
        if (!ValidRecordId(eventId)) throw new ArgumentException("图片编号无效。");
        var session = CurrentSession;
        var requestId = Guid.NewGuid();
        var result = ReadData<PhotoDeletion>(await SendAsync(HttpMethod.Post,
            StudyRecordsPath(scope, $"study-photos/{eventId}/delete") + RecordsQuery(scope),
            session.Token, ct, "0.15", new { requestId }, requestId));
        CheckCreationSession(session, ct);
        if (result.EventId != eventId || result.State != "Deleted") throw InvalidResponse();
    }

    private sealed record PhotoDeletion(string EventId, string State);
}
