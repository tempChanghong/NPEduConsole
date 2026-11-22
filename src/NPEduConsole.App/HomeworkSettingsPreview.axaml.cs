using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

/// <summary>
/// 全校作业录入设置的一小块预览：把「快捷词条」按学科筛出来并试插进一段作业正文，
/// 把「快捷截止时间」按基准日算出实际日期。它只负责显示与试用，不保存、不读网络。
///
/// 与旧网页保持一致的两条规则都写成了静态方法，便于验收直接对照：
///   ResolveDeadline —— 基准日按本地日期，next-weekday 取「同星期下一周、其余下一次出现」；
///   InsertText      —— 普通词条替换选择区；换行词条在相邻已是换行时不重复插入。
/// </summary>
public sealed partial class HomeworkSettingsPreview : UserControl
{
    private const string BodyKey = "body";

    private bool _deadlines;
    private IReadOnlyList<HomeworkQuickInput> _words = Array.Empty<HomeworkQuickInput>();
    private IReadOnlyList<HomeworkQuickDeadline> _presets = Array.Empty<HomeworkQuickDeadline>();
    private IReadOnlyList<HomeworkSubject> _subjects = Array.Empty<HomeworkSubject>();
    private HashSet<string> _knownSubjects = new(StringComparer.Ordinal);

    private string? _subjectId;
    private DateTime _baseDate = DateTime.Today;
    private bool _filling;
    private bool _syncing;

    private string? _focusKey;
    private HomeworkQuickInput? _focusedWord;
    private int _bodyCaret;
    private readonly List<(HomeworkQuickInput Item, Control Control)> _wordControls = new();

    public HomeworkSettingsPreview()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, "作业录入设置预览");


        _syncing = true;
        HomeworkPreviewBaseDate.SelectedDate = _baseDate;
        _syncing = false;
        RefreshBaseHint();
        RefreshCursor();
    }

    /* ------------------------------------------------------------ 对外接口 */

    /// <summary>主窗每次草稿变化都会调一次。尽量原地更新：不丢所选学科、基准日、试填内容与焦点。</summary>
    public void SetPreview(
        bool deadlines,
        IReadOnlyList<HomeworkQuickInput> words,
        IReadOnlyList<HomeworkQuickDeadline> presets,
        IReadOnlyList<HomeworkSubject> subjects)
    {
        var nextWords = words ?? Array.Empty<HomeworkQuickInput>();
        var nextPresets = presets ?? Array.Empty<HomeworkQuickDeadline>();
        var nextSubjects = subjects ?? Array.Empty<HomeworkSubject>();
        var rebuild = !SameView(
            deadlines, nextWords, nextPresets, nextSubjects,
            _deadlines, _words, _presets, _subjects);

        _deadlines = deadlines;
        _words = nextWords;
        _presets = nextPresets;
        _subjects = nextSubjects;
        _knownSubjects = new HashSet<string>(_subjects.Select((item) => item.Id), StringComparer.Ordinal);

        HomeworkPreviewWordsPanel.IsVisible = !deadlines;
        HomeworkPreviewDeadlinesPanel.IsVisible = deadlines;
        HomeworkPreviewSubjectRow.IsVisible = !deadlines;
        HomeworkPreviewHint.IsVisible = !deadlines;

        if (rebuild)
        {
            FillSubjects();
            if (deadlines) FillDeadlines();
            else FillWords();
        }

        UpdateNotice();
        RestoreFocus();
    }

    /// <summary>
    /// 统一的键盘入口：词条分区落到第一个可试插的词条（没有词条时落到正文），
    /// 截止分区落到基准日选择器（改基准日才能改下面每一行的实际日期）。主窗打开「试用」时调它。
    /// </summary>
    public void FocusPreview() => FocusPreview(_deadlines);

    /// <summary>同上，但强制按指定分区选入口（主窗在切换分区、视图还没刷新时用）。</summary>
    public void FocusPreview(bool deadlines)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var target = deadlines
                ? HomeworkPreviewBaseDate
                : FirstWordControl() ?? HomeworkPreviewBody;
            if (target is null || !Usable(target)) return;
            target.BringIntoView();
            target.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>兼容旧调用：只把焦点放到词条区。</summary>
    public void FocusFirstWord() => FocusPreview(false);

    private Control? FirstWordControl()
    {
        for (var index = 0; index < _wordControls.Count; index++)
        {
            if (Usable(_wordControls[index].Control)) return _wordControls[index].Control;
        }
        return null;
    }

    /* ------------------------------------------------------------ 视图是否变化 */

    /// <summary>
    /// 只比较参与显示与试插的字段：切换学科/基准日由控件自己维持，不走这里。
    /// 用逐字段比较而不是拼字符串签名——分隔符会碰撞，<c>Math.Abs(GetHashCode())</c> 既会碰撞，也可能在
    /// <see cref="int.MinValue"/> 上抛 <see cref="OverflowException"/>。
    /// </summary>
    private static bool SameView(
        bool deadlines,
        IReadOnlyList<HomeworkQuickInput> words,
        IReadOnlyList<HomeworkQuickDeadline> presets,
        IReadOnlyList<HomeworkSubject> subjects,
        bool previousDeadlines,
        IReadOnlyList<HomeworkQuickInput> previousWords,
        IReadOnlyList<HomeworkQuickDeadline> previousPresets,
        IReadOnlyList<HomeworkSubject> previousSubjects)
    {
        if (deadlines != previousDeadlines) return false;
        if (words.Count != previousWords.Count) return false;
        if (presets.Count != previousPresets.Count) return false;
        if (subjects.Count != previousSubjects.Count) return false;

        for (var index = 0; index < words.Count; index++)
        {
            if (!SameWord(words[index], previousWords[index])) return false;
        }
        for (var index = 0; index < presets.Count; index++)
        {
            if (!SamePreset(presets[index], previousPresets[index])) return false;
        }
        for (var index = 0; index < subjects.Count; index++)
        {
            var next = subjects[index];
            var before = previousSubjects[index];
            // 学科代码会显示在下拉项里（「数学（MATH）」），只比 id+name 会让改名后的代码显示不出来。
            if (!string.Equals(next.Id, before.Id, StringComparison.Ordinal)
                || !string.Equals(next.Name, before.Name, StringComparison.Ordinal)
                || !string.Equals(next.Code, before.Code, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static bool SameWord(HomeworkQuickInput next, HomeworkQuickInput before)
    {
        if (!string.Equals(next.Label, before.Label, StringComparison.Ordinal)
            || !string.Equals(next.Text, before.Text, StringComparison.Ordinal)
            || !string.Equals(next.Group, before.Group, StringComparison.Ordinal)
            || !string.Equals(next.InsertMode, before.InsertMode, StringComparison.Ordinal))
        {
            return false;
        }
        return SameIds(next.SubjectIds, before.SubjectIds);
    }

    /// <summary>
    /// 判断两个词条是不是「同一个词条」——只用于重建后把焦点放回去。
    /// 刻意不比 <c>Group</c>：改分组会触发重建，但用户焦点应该跟着这个词条走，而不是掉到正文。
    /// </summary>
    private static bool SameWordFocus(HomeworkQuickInput next, HomeworkQuickInput before)
        => string.Equals(next.Label, before.Label, StringComparison.Ordinal)
           && string.Equals(next.Text, before.Text, StringComparison.Ordinal)
           && string.Equals(next.InsertMode, before.InsertMode, StringComparison.Ordinal)
           && SameIds(next.SubjectIds, before.SubjectIds);

    private static bool SamePreset(HomeworkQuickDeadline next, HomeworkQuickDeadline before)
        => string.Equals(next.Label, before.Label, StringComparison.Ordinal)
           && string.Equals(next.Time, before.Time, StringComparison.Ordinal)
           && next.DayOffset == before.DayOffset
           && string.Equals(next.DateRule, before.DateRule, StringComparison.Ordinal)
           && next.Weekday == before.Weekday;

    private static bool SameIds(IReadOnlyList<string>? next, IReadOnlyList<string>? before)
    {
        var left = next ?? Array.Empty<string>();
        var right = before ?? Array.Empty<string>();
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /* ------------------------------------------------------------ 静态规则 */

    /// <summary>按旧网页 <c>resolveHomeworkQuickDeadline</c> 的口径计算实际日期（本地时间）。</summary>
    public static DateTime ResolveDeadline(HomeworkQuickDeadline preset, DateTime baseDate)
    {
        var (hour, minute) = ParseTime(preset.Time);
        var result = baseDate.Date;
        if (string.Equals(preset.DateRule, "next-weekday", StringComparison.Ordinal) && preset.Weekday is { } weekday)
        {
            var current = (int)result.DayOfWeek;
            var delta = (weekday - current + 7) % 7;
            if (delta == 0) delta = 7;
            result = result.AddDays(delta);
        }
        else
        {
            result = result.AddDays(Math.Max(0, preset.DayOffset ?? 0));
        }
        return result.AddHours(hour).AddMinutes(minute);
    }

    /// <summary>词条是否出现在这个学科下：空学科表 = 全校可用；否则必须显式包含该学科。</summary>
    public static bool MatchesSubject(HomeworkQuickInput item, string? subjectId)
    {
        var ids = item?.SubjectIds ?? Array.Empty<string>();
        if (ids.Count == 0) return true;
        return !string.IsNullOrEmpty(subjectId) && ids.Contains(subjectId, StringComparer.Ordinal);
    }

    /// <summary>替换选择区并返回新的光标位置；规则与旧网页 <c>insertHomeworkQuickInput</c> 一致。</summary>
    public static (string Value, int Cursor) InsertText(string? content, int start, int end, HomeworkQuickInput item)
    {
        var source = content ?? string.Empty;
        var from = Math.Clamp(start, 0, source.Length);
        var to = Math.Clamp(Math.Max(from, end), from, source.Length);
        var text = string.Equals(item?.InsertMode, "NEW_LINE", StringComparison.Ordinal)
            ? "\n"
            : item?.Text ?? string.Empty;
        if (string.Equals(item?.InsertMode, "NEW_LINE", StringComparison.Ordinal)
            && ((from > 0 && source[from - 1] == '\n') || (to < source.Length && source[to] == '\n')))
        {
            text = string.Empty;
        }
        return (source[..from] + text + source[to..], from + text.Length);
    }

    /// <summary>规则的可读说明（用于截止时间逐行提示）。</summary>
    public static string DescribeRule(HomeworkQuickDeadline preset)
    {
        if (string.Equals(preset.DateRule, "next-weekday", StringComparison.Ordinal) && preset.Weekday is { } weekday)
        {
            return $"下个{WeekdayName((DayOfWeek)Math.Clamp(weekday, 0, 6))}";
        }
        var offset = Math.Max(0, preset.DayOffset ?? 0);
        return offset switch
        {
            0 => "当天",
            1 => "明天",
            2 => "后天",
            _ => $"{offset} 天后",
        };
    }

    public static string FormatDeadline(DateTime value)
        => $"{value:yyyy-MM-dd} {WeekdayName(value.DayOfWeek)} {value:HH:mm}";

    private static (int Hour, int Minute) ParseTime(string? text)
    {
        if (DateTime.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return (parsed.Hour, parsed.Minute);
        }
        return (0, 0);
    }

    private static string WeekdayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    /* ------------------------------------------------------------ 词条面板 */

    private void FillSubjects()
    {
        _filling = true;
        var choices = new List<SubjectChoice> { new(null, "全校可用") };
        choices.AddRange(_subjects.Select((item) => new SubjectChoice(item.Id, string.IsNullOrWhiteSpace(item.Code) ? item.Name : $"{item.Name}（{item.Code}）")));
        HomeworkPreviewSubject.ItemsSource = choices;
        var selected = choices.FirstOrDefault((choice) => string.Equals(choice.Id, _subjectId, StringComparison.Ordinal)) ?? choices[0];
        HomeworkPreviewSubject.SelectedItem = selected;
        _subjectId = selected.Id;
        _filling = false;

        var stale = _words
            .Where((item) => (item.SubjectIds ?? Array.Empty<string>()).Any((id) => !_knownSubjects.Contains(id)))
            .ToList();
        HomeworkPreviewSubjectNote.IsVisible = stale.Count > 0;
        HomeworkPreviewSubjectNote.Text = stale.Count == 0
            ? string.Empty
            : $"{stale.Count} 条词条引用了本校目录里不存在的学科：{string.Join("、", stale.Select((item) => item.Label).Take(4))}"
              + (stale.Count > 4 ? " 等。" : "。")
              + "试用仅按仍在目录的适用学科匹配。";
    }

    private void FillWords()
    {
        HomeworkPreviewGroups.Children.Clear();
        _wordControls.Clear();

        var visible = _words.Where((item) => MatchesSubject(item, _subjectId)).ToList();
        HomeworkPreviewHint.Text = _subjectId is null
            ? "当前只显示「全校可用」的词条；选择学科后才会出现只对该学科可用的词条。"
            : $"当前显示「{SubjectName(_subjectId)}」可用的词条。";

        foreach (var group in visible.GroupBy((item) => string.IsNullOrWhiteSpace(item.Group) ? "常用" : item.Group.Trim()))
        {
            var header = new TextBlock
            {
                Text = group.Key,
                Margin = new Thickness(0, 10, 0, 4),
            };
            header.Classes.Add("hw-note");
            HomeworkPreviewGroups.Children.Add(header);

            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var item in group)
            {
                var label = string.IsNullOrWhiteSpace(item.Label) ? "（未命名）" : item.Label;
                var newLine = string.Equals(item.InsertMode, "NEW_LINE", StringComparison.Ordinal);
                var button = new Button
                {
                    Content = newLine ? "换行" : label,
                    Margin = new Thickness(0, 0, 6, 6),
                };
                button.Classes.Add("hw-word");
                AutomationProperties.SetName(button, newLine ? "换行" : label);
                if (newLine)
                {
                    AutomationProperties.SetHelpText(button, "插入一个换行");
                }
                else
                {
                    AutomationProperties.SetHelpText(button, item.Text);
                    if (!string.IsNullOrWhiteSpace(item.Text)) ToolTip.SetTip(button, item.Text);
                }
                button.GotFocus += (_, _) =>
                {
                    _focusedWord = item;
                    _focusKey = null;
                };
                button.Click += (_, _) => Insert(item);
                _wordControls.Add((item, button));
                wrap.Children.Add(button);
            }
            HomeworkPreviewGroups.Children.Add(wrap);
        }

        if (visible.Count == 0)
        {
            var empty = new TextBlock { Text = "这个学科名下还没有可试插的词条。", Margin = new Thickness(0, 6, 0, 0) };
            empty.Classes.Add("hw-note");
            HomeworkPreviewGroups.Children.Add(empty);
        }
    }

    private void Insert(HomeworkQuickInput item)
    {
        var text = HomeworkPreviewBody.Text ?? string.Empty;
        var (value, cursor) = InsertText(text, HomeworkPreviewBody.SelectionStart, HomeworkPreviewBody.SelectionEnd, item);
        HomeworkPreviewBody.Text = value;
        HomeworkPreviewBody.CaretIndex = cursor;
        _bodyCaret = cursor;
        _focusedWord = null;
        _focusKey = BodyKey;
        RefreshCursor();
        HomeworkPreviewBody.Focus(NavigationMethod.Tab);
        HomeworkPreviewBody.CaretIndex = Math.Min(cursor, HomeworkPreviewBody.Text?.Length ?? 0);
    }

    /* ------------------------------------------------------------ 截止面板 */

    private void FillDeadlines()
    {
        HomeworkPreviewDeadlines.Children.Clear();
        _syncing = true;
        HomeworkPreviewBaseDate.SelectedDate = _baseDate;
        _syncing = false;

        if (_presets.Count == 0)
        {
            var empty = new TextBlock { Text = "还没有配置快捷截止时间。", Margin = new Thickness(0, 6, 0, 0) };
            empty.Classes.Add("hw-note");
            HomeworkPreviewDeadlines.Children.Add(empty);
            return;
        }

        foreach (var preset in _presets)
        {
            var valid = DateTime.TryParseExact(preset.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            var resolved = ResolveDeadline(preset, _baseDate);
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                RowDefinitions = new RowDefinitions("Auto,Auto"),
            };
            var name = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(preset.Label) ? "（未命名）" : preset.Label,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            };
            var rule = new TextBlock
            {
                Text = $"{DescribeRule(preset)} · {preset.Time}"
                       + (valid ? string.Empty : " · 时间格式无效，保存前需修正"),
                TextWrapping = TextWrapping.Wrap,
            };
            rule.Classes.Add("hw-note");
            var result = new TextBlock
            {
                Text = valid ? FormatDeadline(resolved) : "—",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetRow(name, 0);
            Grid.SetRow(rule, 1);
            Grid.SetColumn(name, 0);
            Grid.SetColumn(rule, 0);
            Grid.SetRowSpan(result, 2);
            Grid.SetColumn(result, 1);
            grid.Children.Add(name);
            grid.Children.Add(rule);
            grid.Children.Add(result);

            var card = new Border { Child = grid };
            card.Classes.Add("hw-deadline");
            AutomationProperties.SetName(card, $"{preset.Label}：{(valid ? FormatDeadline(resolved) : "时间无效")}");
            HomeworkPreviewDeadlines.Children.Add(card);
        }
    }

    /* ------------------------------------------------------------ 交互 */

    private void SubjectChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling) return;
        _subjectId = (HomeworkPreviewSubject.SelectedItem as SubjectChoice)?.Id;
        _focusedWord = null;
        _focusKey = "subject";
        if (_deadlines) FillDeadlines();
        else FillWords();
        UpdateNotice();
        RestoreFocus();
    }

    private void BaseDateChanged(object? sender, DatePickerSelectedValueChangedEventArgs args)
    {
        if (_syncing) return;
        if (HomeworkPreviewBaseDate.SelectedDate is { } picked)
        {
            _baseDate = picked.Date;
        }
        _focusedWord = null;
        _focusKey = "base";
        RefreshBaseHint();
        if (_deadlines) FillDeadlines();
        RestoreFocus();
    }

    private void BodyChanged(object? sender, TextChangedEventArgs args)
    {
        _bodyCaret = HomeworkPreviewBody.CaretIndex;
        _focusedWord = null;
        _focusKey = BodyKey;
        RefreshCursor();
    }

    private void RefreshBaseHint()
        => HomeworkPreviewBaseHint.Text = $"按 {_baseDate:yyyy-MM-dd} {WeekdayName(_baseDate.DayOfWeek)} 起算，下面是每一行实际会落到的时间。";

    private void RefreshCursor()
    {
        var text = HomeworkPreviewBody.Text ?? string.Empty;
        var caret = Math.Clamp(HomeworkPreviewBody.CaretIndex, 0, text.Length);
        HomeworkPreviewCursor.Text = $"试填内容 {text.Length} 字，光标在第 {caret} 位。";
    }

    private void UpdateNotice()
    {
        var stale = _words.Count((item) => (item.SubjectIds ?? Array.Empty<string>()).Any((id) => !_knownSubjects.Contains(id)));
        var visible = _words.Count((item) => MatchesSubject(item, _subjectId));
        var parts = new List<string>();
        if (stale > 0) parts.Add($"{stale} 条词条引用了已不存在的学科，试插前需要先修正词条。");
        if (!_deadlines && visible == 0 && _words.Count > 0) parts.Add("当前学科下没有可用词条，切回「全校可用」看看。");
        HomeworkPreviewNotice.IsVisible = parts.Count > 0;
        HomeworkPreviewNoticeText.Text = string.Join(" ", parts);
    }

    /* ------------------------------------------------------------ 焦点 */

    private void RestoreFocus()
    {
        if (_focusedWord is null && _focusKey is null) return;
        var word = _focusedWord;
        var key = _focusKey;
        Dispatcher.UIThread.Post(() =>
        {
            Control? target = null;
            if (word is not null && !_deadlines)
            {
                // 按结构比较找同一词条：改分组也会重建，但焦点要回到这个词条上。
                for (var index = 0; index < _wordControls.Count; index++)
                {
                    if (!SameWordFocus(_wordControls[index].Item, word)) continue;
                    target = _wordControls[index].Control;
                    break;
                }
                target ??= HomeworkPreviewBody;
            }
            target ??= key switch
            {
                BodyKey => HomeworkPreviewBody,
                "subject" => HomeworkPreviewSubject,
                "base" => HomeworkPreviewBaseDate,
                _ => null,
            };
            if (target is null || !Usable(target)) return;
            target.BringIntoView();
            target.Focus(NavigationMethod.Tab);
            if (ReferenceEquals(target, HomeworkPreviewBody))
            {
                HomeworkPreviewBody.CaretIndex = Math.Min(_bodyCaret, HomeworkPreviewBody.Text?.Length ?? 0);
            }
        }, DispatcherPriority.Loaded);
    }

    private static bool Usable(Control control)
        => control.IsEffectivelyVisible && control.IsEffectivelyEnabled;

    private string SubjectName(string? id)
        => _subjects.FirstOrDefault((item) => string.Equals(item.Id, id, StringComparison.Ordinal))?.Name ?? "该学科";

    private sealed record SubjectChoice(string? Id, string Label)
    {
        public override string ToString() => Label;
    }
}
