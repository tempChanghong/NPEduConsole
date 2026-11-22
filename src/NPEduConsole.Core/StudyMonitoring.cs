using System.Text.Json;
using System.Text.Json.Nodes;

namespace NPEduConsole.Core;

public sealed record StudyMonitoring(bool Enabled, bool Estimated, double? Medium, double? High, int HighSeconds,
    int ReminderSeconds, bool PhotosEnabled, int PhotoSeconds, int RetentionDays, bool ExtendedRetentionAcknowledged)
{
    public static StudyMonitoring Read(JsonElement? settings)
    {
        if (settings is not { } policy || policy.ValueKind != JsonValueKind.Object)
            return new(false, false, null, null, 3, 600, false, 30, 30, false);
        var noise = policy.GetProperty("noise");
        var photos = policy.GetProperty("photos");
        bool estimated = noise.TryGetProperty("estimated", out var acoustic) && acoustic.ValueKind == JsonValueKind.Object;
        var lines = estimated ? acoustic : noise;
        return new(noise.GetProperty("enabled").GetBoolean(), estimated, Level(lines, estimated ? "mediumDbA" : "mediumLineDbfs"),
            Level(lines, estimated ? "highDbA" : "redLineDbfs"),
            noise.TryGetProperty("highContinuousSeconds", out var seconds) ? seconds.GetInt32() : photos.GetProperty("continuousSeconds").GetInt32(),
            noise.TryGetProperty("disciplineCooldownSeconds", out var reminder) ? reminder.GetInt32() : 600,
            photos.GetProperty("enabled").GetBoolean(), photos.GetProperty("cooldownSeconds").GetInt32(),
            photos.GetProperty("retentionDays").GetInt32(), photos.GetProperty("extendedRetentionAcknowledged").GetBoolean());
    }

    private static double? Level(JsonElement data, string key) =>
        data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    internal void Validate()
    {
        bool ValidLevel(double? value) => value is null || double.IsFinite(value.Value) &&
            (Estimated ? value is >= 30 and <= 130 : value is >= -90 and <= 0);
        if (!ValidLevel(High) || !ValidLevel(Medium) || Medium is { } medium && (High is null || medium >= High))
            throw new ArgumentException(Estimated
                ? "估算阈值须在 30 至 130 dB(A)，中线须低于高线。可留空，填写中线时须同时设置高线。"
                : "信号阈值须在 −90 至 0 dBFS，中线须低于高线。可留空，填写中线时须同时设置高线。");
        if (HighSeconds is < 1 or > 10 || ReminderSeconds is < 1 or > 86400 ||
            PhotoSeconds is < 30 or > 300 || RetentionDays is < 1 or > 180)
            throw new ArgumentException("请核对高线时长（1–10 秒）、提醒间隔（1–86400 秒）、拍照间隔（30–300 秒）和保留天数（1–180 天）。");
        if (PhotosEnabled && (!Enabled || High is null))
            throw new ArgumentException("开通拍照须同时启用监测并设置高线。");
        if (PhotosEnabled && RetentionDays > 30 && !ExtendedRetentionAcknowledged)
            throw new ArgumentException("保留超过 30 天须确认学校已批准延长保留。");
    }
}

public sealed partial class ConsoleClient
{
    public static SchoolStudyEdit BuildMonitoringEdit(string school, SchoolStudyCatalog catalog, string type, string id,
        string mode, StudyMonitoring? monitoring)
    {
        var edit = BuildSchoolStudyEdit(school, catalog, type, id, mode, null, null);
        if (mode != "Override") return edit;
        ArgumentNullException.ThrowIfNull(monitoring);
        monitoring.Validate();
        var policy = JsonNode.Parse(edit.Policy.GetRawText())!.AsObject();
        var settings = policy["settings"]!.AsObject();
        var noise = settings["noise"]!.AsObject();
        noise["enabled"] = monitoring.Enabled;
        noise["highContinuousSeconds"] = monitoring.HighSeconds;
        noise["disciplineCooldownSeconds"] = monitoring.ReminderSeconds;
        if (monitoring.Estimated)
            noise["estimated"] = JsonSerializer.SerializeToNode(new { mediumDbA = monitoring.Medium, highDbA = monitoring.High }, Json);
        else
        {
            if (noise["estimated"] is JsonObject) noise.Remove("estimated");
            noise["mediumLineDbfs"] = monitoring.Medium;
            noise["redLineDbfs"] = monitoring.High;
        }
        var photos = settings["photos"]!.AsObject();
        photos["enabled"] = monitoring.PhotosEnabled;
        photos["continuousSeconds"] = monitoring.HighSeconds;
        photos["cooldownSeconds"] = monitoring.PhotoSeconds;
        photos["retentionDays"] = monitoring.RetentionDays;
        photos["extendedRetentionAcknowledged"] = monitoring.ExtendedRetentionAcknowledged;
        return edit with { Policy = JsonSerializer.SerializeToElement(policy, Json) };
    }
}
