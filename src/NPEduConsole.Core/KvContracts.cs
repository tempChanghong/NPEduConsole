using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPEduConsole.Core;

internal sealed record Envelope(JsonElement Data = default, bool? Success = null,
    string? ProtocolVersion = null, string? RequestId = null, DateTimeOffset? ServerTime = null, NpepError? Error = null);

internal sealed record LoginResponse([property: JsonPropertyName("access_token")] string AccessToken);
internal sealed record TermRow(string Id, string Status);
internal sealed record SchoolRow(string Id, string Name, TermRow[] Terms);
internal sealed record Membership(string Role, SchoolRow School);
internal sealed record GradeRow(string Name);
internal sealed record ClassRow(string Id, string Name, bool IsActive, GradeRow? Grade);
internal sealed record Structure(TermRow Term, ClassRow[] AdministrativeClasses);
internal sealed record ScreenRow(string Id, string SchoolId, string? AdministrativeClassId,
    string Name, bool IsActive, long NpepBindingRevision);
internal sealed record DeviceRow(string DeviceId, string SchoolId, string AdministrativeClassId, string ScreenBindingId,
    long BindingRevision, bool BindingChanged, string DeviceName, string State, string Connectivity, DateTimeOffset? LastSeenAt);
internal sealed record OverviewPage(DeviceRow[] Items, string? NextCursor);
internal sealed record Policy(long Revision, bool Locked, string? MicrophoneId, string? CameraId);
internal sealed record Inventory(long AppliedRevision, bool Unlocked, Microphone[] Microphones,
    DateTimeOffset ReceivedAt, string? Error, string? SelectedMicrophoneId = null);
internal sealed record EquipmentResponse(Policy Policy, Inventory? Inventory);
internal sealed record NpepError(string Code);
