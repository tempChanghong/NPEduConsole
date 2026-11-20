using System.Globalization;
using System.Text.Json;

namespace NPEduConsole.Core;

public enum StudyRepeat { Once, Weekly }

public sealed record StudyWindow(DateTime Start, DateTime End);
public sealed record StudyRule(IReadOnlyList<int> Days, TimeOnly Start, TimeOnly End);
public sealed record StudyDateException(DateOnly Date, string Kind, int? Weekday);

public sealed record StudySchedule(DateOnly? From, DateOnly? To, IReadOnlyList<StudyRule> Rules,
    IReadOnlyList<StudyDateException> Exceptions)
{
    internal static StudySchedule Empty { get; } = new(null, null, [], []);
    internal static int Weekday(DateOnly date) => ((int)date.DayOfWeek + 6) % 7 + 1;

    public IReadOnlyList<StudyWindow> WindowsOn(DateOnly date) => InRange(date) && Pattern(date) is { } day
        ? WindowsFor(day, date) : [];

    private bool InRange(DateOnly date) => (!From.HasValue || From <= date) && (!To.HasValue || To >= date);

    private int? Pattern(DateOnly date)
    {
        var exception = Exceptions.FirstOrDefault(entry => entry.Date == date);
        return exception?.Kind == "Skip" ? null : exception?.Weekday ?? Weekday(date);
    }

    private IReadOnlyList<StudyWindow> WindowsFor(int day, DateOnly date)
    {
        var windows = Rules.Where(rule => rule.Days.Contains(day)).Select(rule => ToWindow(date, rule.Start, rule.End))
            .OrderBy(window => window.Start);
        var merged = new List<StudyWindow>();
        foreach (var window in windows)
        {
            if (merged.Count == 0 || merged[^1].End < window.Start) merged.Add(window);
            else if (merged[^1].End < window.End) merged[^1] = merged[^1] with { End = window.End };
        }
        return merged;
    }

    internal StudySchedule Add(DateOnly date, TimeOnly start, TimeOnly end, StudyRepeat repeat)
    {
        if (date.Year is < 2000 or > 9998 || start.Ticks % TimeSpan.TicksPerMinute != 0 || end.Ticks % TimeSpan.TicksPerMinute != 0)
            throw Invalid("请输入有效日期和精确到分钟的时段。");
        var added = ToWindow(date, start, end);
        if (added.End - added.Start > TimeSpan.FromHours(3)) throw Invalid("每场自习的时长须为 1 至 180 分钟。");
        var rule = new StudyRule([Weekday(date)], start, end);
        if (Rules.Count == 0 && Exceptions.Count == 0)
            return new(date, repeat == StudyRepeat.Once ? date : null, [rule], []);
        if (!InRange(date))
        {
            if (repeat == StudyRepeat.Once && From is { } boundedFrom && To is { } boundedTo && Exceptions.Count == 0)
                return ExtendForSingleDate(date, added, rule, boundedFrom, boundedTo);
            throw Invalid("所选日期超出现有日常安排的生效期。请选择生效期内的日期，或到旧学校管理页调整生效期。");
        }
        if (Exceptions.Any(entry => entry.Date == date))
            throw Invalid("所选日期已有停用或调休例外。请换一天，或到旧学校管理页核对该例外。");

        EnsureSeparate(added, Nearby(date));
        if (repeat == StudyRepeat.Weekly)
        {
            if (From is not { } firstDate || FirstOccurrence(Weekday(date), firstDate, date) != date)
                throw Invalid("按周新增会追溯到所选日期之前。本页只开放能从所选日期开始的规则，可以改为仅指定日期。原有规则的调整请到旧学校管理页。");
            ValidateWeeklyPlacement(date, added);
            return WithRules([..Rules, rule]);
        }

        int weekday = Weekday(date);
        var desired = WindowsOn(date).Append(added).OrderBy(window => window.Start);
        // A date can reuse another existing weekday's hours without changing that weekday anywhere else.
        var matchingDay = Enumerable.Range(1, 7).FirstOrDefault(day => WindowsFor(day, date).SequenceEqual(desired));
        if (matchingDay != 0) return WithExceptions([..Exceptions, new(date, "UseWeekday", matchingDay)]);

        // A naturally bounded weekday occurs only once, without synthetic calendar exceptions.
        if (From is { } from && To is { } to && DaysInRange(weekday, from, to).Take(2).Count() == 1 &&
            !Exceptions.Any(entry => entry.Weekday == weekday))
            return WithRules([..Rules, rule]);

        throw Invalid("现有规则无法只增加这一天的时段。可选择与已有日期模板相同的时段，或到旧学校管理页核对并调整完整日历。");
    }

    private StudySchedule ExtendForSingleDate(DateOnly date, StudyWindow added, StudyRule rule, DateOnly from, DateOnly to)
    {
        var extendedFrom = date < from ? date : from;
        var extendedTo = date > to ? date : to;
        var newDaysFrom = date < from ? date : to.AddDays(1);
        var newDaysTo = date < from ? from.AddDays(-1) : date;
        if (Rules.SelectMany(existing => existing.Days).Distinct().Any(day => DaysInRange(day, newDaysFrom, newDaysTo).Any()) ||
            DaysInRange(Weekday(date), extendedFrom, extendedTo).Take(2).Count() != 1)
            throw Invalid("扩大生效期会让旧时段在额外日期运行，或使新时段重复。请另选日期，或到旧学校管理页调整日历。");

        EnsureSeparate(added, Nearby(date));
        return WithRules([..Rules, rule]) with { From = extendedFrom, To = extendedTo };
    }

    private DateOnly FirstOccurrence(int weekday, DateOnly from, DateOnly until)
    {
        var firstRegular = DaysInRange(weekday, from, until).First(date => Pattern(date) == weekday);
        return Exceptions.Where(entry => entry.Date >= from && entry.Date <= until && entry.Weekday == weekday)
            .Select(entry => entry.Date).Append(firstRegular).Min();
    }

    private void ValidateWeeklyPlacement(DateOnly date, StudyWindow added)
    {
        // Check the weekly template, then every substitution and its neighbours where a new occurrence can run.
        EnsureSeparate(added, Neighbours(date).SelectMany(day => WindowsFor(Weekday(day), day)));
        foreach (var affected in Exceptions.SelectMany(entry => Neighbours(entry.Date)).Distinct())
        {
            if (!InRange(affected) || Pattern(affected) != Weekday(date)) continue;
            var occurrence = ToWindow(affected, TimeOnly.FromDateTime(added.Start), TimeOnly.FromDateTime(added.End));
            EnsureSeparate(occurrence, Nearby(affected));
        }
    }

    private StudySchedule WithRules(IReadOnlyList<StudyRule> rules) => rules.Count <= 32
        ? this with { Rules = rules } : throw Invalid("现有规则已接近 32 条上限。请在旧学校管理页整理后再安排。");

    private StudySchedule WithExceptions(IReadOnlyList<StudyDateException> exceptions) => exceptions.Count <= 366
        ? this with { Exceptions = exceptions } : throw TooManyExceptions();

    private IEnumerable<StudyWindow> Nearby(DateOnly date) => Neighbours(date).SelectMany(WindowsOn);
    private static DateOnly[] Neighbours(DateOnly date) => [date.AddDays(-1), date, date.AddDays(1)];

    private static IEnumerable<DateOnly> DaysInRange(int weekday, DateOnly from, DateOnly to)
    {
        int first = from.DayNumber + (weekday - Weekday(from) + 7) % 7;
        for (int day = first; day <= to.DayNumber; day += 7) yield return DateOnly.FromDayNumber(day);
    }

    private static StudyWindow ToWindow(DateOnly date, TimeOnly start, TimeOnly end) =>
        new(date.ToDateTime(start), (end <= start ? date.AddDays(1) : date).ToDateTime(end));

    private static void EnsureSeparate(StudyWindow added, IEnumerable<StudyWindow> windows)
    {
        if (windows.Any(existing => added.Start <= existing.End && existing.Start <= added.End))
            throw Invalid("新时段与现有日常自习重叠或相邻，会改变原时段。请选择留有间隔的时间。");
    }

    private static ConsoleApiException TooManyExceptions() => Invalid("日期例外已达 366 条上限。请到旧学校管理页核对日历。");

    private static ConsoleApiException Invalid(string message) => new(400, message, "STUDY_DRAFT_UNAVAILABLE");

    internal object Wire() => new
    {
        timeZone = "Asia/Shanghai", effectiveFrom = From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", effectiveTo = To,
        rules = Rules.Select(rule => new { days = rule.Days, start = Format(rule.Start), end = Format(rule.End) }).ToArray(),
        exceptions = Exceptions.Select(entry => new { date = entry.Date, kind = entry.Kind, weekday = entry.Weekday }).ToArray()
    };

    internal static StudySchedule Read(JsonElement data)
    {
        if (data.EnumerateObject().Count() != 5 || data.GetProperty("timeZone").GetString() != "Asia/Shanghai")
            throw new JsonException("Unknown study calendar shape.");
        string? from = data.GetProperty("effectiveFrom").GetString();
        string? to = data.GetProperty("effectiveTo").GetString();
        var rawRules = data.GetProperty("rules");
        var rawExceptions = data.GetProperty("exceptions");
        if (rawRules.EnumerateArray().Concat(rawExceptions.EnumerateArray()).Any(entry => entry.EnumerateObject().Count() != 3))
            throw new JsonException("Unknown study calendar entry shape.");
        var rules = rawRules.EnumerateArray().Select(rule => new StudyRule(
            rule.GetProperty("days").EnumerateArray().Select(day => day.GetInt32()).ToArray(),
            ParseTime(rule.GetProperty("start").GetString()), ParseTime(rule.GetProperty("end").GetString()))).ToArray();
        var exceptions = rawExceptions.EnumerateArray().Select(entry => new StudyDateException(
            ParseDate(entry.GetProperty("date").GetString()), entry.GetProperty("kind").GetString()!,
            entry.GetProperty("weekday") is { ValueKind: JsonValueKind.Number } weekday ? weekday.GetInt32() : null)).ToArray();
        if (rules.Length > 32 || rules.Any(rule => rule.Days.Count == 0 || rule.Days.Any(day => day is < 1 or > 7)) ||
            exceptions.Length > 366 || exceptions.Any(entry => entry.Kind switch
            {
                "Skip" => entry.Weekday is not null,
                "UseWeekday" => entry.Weekday is not (>= 1 and <= 7),
                _ => true
            })) throw new JsonException("Invalid study calendar rules.");
        return new(string.IsNullOrEmpty(from) ? null : ParseDate(from), to is null ? null : ParseDate(to), rules, exceptions);
    }

    internal static DateOnly ParseDate(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date) && date.Year is >= 2000 and <= 9998 ? date : throw new JsonException("Invalid study date.");

    private static TimeOnly ParseTime(string? value) => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var time) ? time : throw new JsonException("Invalid study time.");

    private static string Format(TimeOnly value) => value.ToString("HH:mm", CultureInfo.InvariantCulture);
}
