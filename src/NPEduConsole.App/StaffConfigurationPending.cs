using System.Text.Json;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed record StaffConfigurationPending(Guid Id, string Server, string ActorId, string SchoolId, string TermId, string Proof);

// An independent file per request lets another window keep its own in-flight request.
internal sealed class StaffConfigurationRequests(string? directory)
{
    private readonly Dictionary<Guid, StaffConfigurationPending> _memory = [];

    internal StaffConfigurationPending[] ForSchool(Uri server, string actor, string school)
    {
        if (directory is not null && Directory.Exists(directory))
        {
            _memory.Clear();
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    if (new FileInfo(file).Length > 8192 || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out var id)) continue;
                    var item = JsonSerializer.Deserialize<StaffConfigurationPending>(File.ReadAllText(file));
                    if (item is not null && item.Id == id && ConsoleClient.ValidStaffProof(item.Proof)) _memory[id] = item;
                }
                catch (Exception error) when (error is IOException or JsonException) { }
            }
        }
        return _memory.Values.Where(item => item.Server == server.AbsoluteUri && item.ActorId == actor && item.SchoolId == school).ToArray();
    }

    internal void Remember(StaffConfigurationPending item)
    {
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, $"{item.Id:D}.json");
            string temporary = Path.Combine(directory, $"{item.Id:D}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(item));
                File.Move(temporary, file);
            }
            finally { File.Delete(temporary); }
        }
        _memory[item.Id] = item;
    }

    internal void Clear(StaffConfigurationPending item)
    {
        if (directory is not null) File.Delete(Path.Combine(directory, $"{item.Id:D}.json"));
        _memory.Remove(item.Id);
    }
}
