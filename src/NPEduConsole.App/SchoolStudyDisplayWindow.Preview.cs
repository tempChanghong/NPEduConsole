using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class SchoolStudyDisplayWindow
{
    private sealed record DisplayPage(StudyDisplayItem Item, string Body, int Part, int Parts);

    private async Task ReadDisplayAsync()
    {
        _savedDisplay = null;
        DisplayScreenRows.ItemsSource = null;
        SetDisplay(null);
        _savedDisplay = await _client.ReadSchoolStudyDisplayAsync(_scope, _date, _lifetime.Token);
        if (!_closed) SetDisplay(_savedDisplay);
    }

    private async void ReloadClicked(object? sender, RoutedEventArgs args) => await RunAsync(ReloadAsync);
    private async Task ReloadAsync()
    {
        var target = Selected;
        var previous = _original;
        var local = ReadControls();
        var current = await _client.ReadSchoolStudyAsync(_school.Id, _lifetime.Token);
        if (_closed) return;
        if (current.TermId != _scope.TermId || current.Classes.All(row => row.Id != _scope.ClassId))
            throw new ConsoleApiException(409, "当前学期或班级已变化，请返回学校自习重新选择。", "TERM_NOT_ACTIVE");
        _review = null; _edit = null; _draftBeforeReview = null;
        _catalog = current;
        BindTargets(target);
        if (Selected is { } next && next.Type == target?.Type && next.Id == target.Id && local is not null && !local.SameSettings(previous))
        {
            var latest = _original;
            var retained = latest with
            {
                NoiseDisplay = local.NoiseDisplay == previous.NoiseDisplay ? latest.NoiseDisplay : local.NoiseDisplay,
                HomeworkEnabled = local.HomeworkEnabled == previous.HomeworkEnabled ? latest.HomeworkEnabled : local.HomeworkEnabled,
                SourceMode = local.SameSources(previous) ? latest.SourceMode : local.SourceMode,
                SourceClassIds = local.SameSources(previous) ? latest.SourceClassIds : local.SourceClassIds,
                RotationSeconds = local.RotationSeconds == previous.RotationSeconds ? latest.RotationSeconds : local.RotationSeconds,
            };
            BindTarget(next, retained);
            _targetWithDraft = next;
            Notice(!latest.SameSettings(previous) ? "本次未保存。已保留你改过的字段，学校当前值已重新读取，请再次预览。" :
                "已保留草稿并重新读取学校当前设置，请再次预览。");
        }
        else Notice("已重新读取学校当前设置。");
        await ReadDisplayAsync();
    }

    private void DiscardDraft(object? sender, RoutedEventArgs args)
    {
        if (Selected is not { } target || _busy || Pending is not null) return;
        _review = null; _edit = null; _draftBeforeReview = null;
        BindTarget(target);
        DropDraftPreview();
        Render();
    }

    private async void PlanClicked(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (Selected is not { Editable: true } target || Pending is not null) return;
        if (ReadControls() is not { } options)
        {
            DisplayRotation.Focus();
            throw new ArgumentException("请输入整数秒数（15 至 600）。");
        }
        var edit = ConsoleClient.BuildStudyDisplayEdit(_school.Id, _catalog, target.Type, target.Id, options, target.Independent);
        SchoolStudyReview preview;
        try { preview = await _client.PreviewSchoolStudyAsync(edit, _date, _lifetime.Token, _scope.ClassId); }
        catch (ConsoleApiException error) when (NeedsNewPreview(error))
        {
            await RefreshStalePreviewAsync();
            return;
        }
        if (_closed) return;
        _edit = edit;
        _review = preview;
        _draftBeforeReview = options;
        DisplayImpacts.ItemsSource = preview.Items.Select(row => new StudyDisplayLine(row.Name, row.Changed ?
            $"{Summary(row.Before)}\n保存后 · {Summary(row.Effective)}" :
            $"保持 · {SchoolStudyWindow.SourceText(row.Effective.Source, row.LegacyNoiseAfter)}")).ToArray();
        DisplayImpactPanel.IsExpanded = true;
        SetDisplay(preview.Display);
        _showPreview = true;
        Arrange();
        Notice($"将改变 {preview.ChangedClasses} 班，共核对 {preview.TotalClasses} 班。请确认作用范围和实际来源。");
    });

    private string Summary(StudyImpactPolicy policy)
    {
        var source = SchoolStudyWindow.SourceText(policy.Source);
        if (policy.Settings is not { } settings) return source;
        var h = settings.GetProperty("homework");
        var style = settings.GetProperty("presentation").GetProperty("noiseDisplay").GetString();
        var homework = !h.GetProperty("enabled").GetBoolean() ? "作业关闭" : h.GetProperty("sourceMode").GetString() == "OwnClass" ? "各班本班作业" :
            string.Join("、", h.GetProperty("sourceClassIds").EnumerateArray().Select(id => _catalog.Classes
                .FirstOrDefault(room => room.Id == id.GetString())?.Name ?? "已不可用来源"));
        return $"{source} · {NoiseStyles.FirstOrDefault(row => row.Id == style)?.Label ?? "噪音样式待核对"} · {homework} · 每页 {h.GetProperty("rotationSeconds").GetInt32()} 秒";
    }

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (_edit is not { } edit || _review is not { } preview || _draftBeforeReview is not { } draft ||
            ReadControls() is not { } current || !draft.SameSettings(current) || _busy || Pending is not null) return;
        var operation = new Operation(edit, preview, Guid.NewGuid());
        Operations[OperationKey] = operation;
        await RunAsync(() => SubmitAsync(operation));
    }

    private async Task SubmitAsync(Operation operation)
    {
        _checkedMissing = false;
        try
        {
            int revision = await _client.SaveSchoolStudyAsync(operation.Edit, operation.Preview, operation.Id, _lifetime.Token);
            if (!_closed) await ConfirmedAsync(revision);
        }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
        {
            Notice("保存结果待核对，正在读取本账号回执。");
            await VerifyAsync(operation);
        }
        catch (ConsoleApiException error)
        {
            Operations.Remove(OperationKey);
            _review = null; _edit = null;
            if (NeedsNewPreview(error)) await RefreshStalePreviewAsync();
            else throw;
        }
    }

    private static bool NeedsNewPreview(ConsoleApiException error) => error.Code is "STUDY_PREVIEW_CHANGED" or
        "STUDY_SETTING_VERSION_CONFLICT" or "STUDY_SOURCE_UNAVAILABLE" or "STUDY_DISPLAY_BASE_CHANGED" or "STUDY_DISPLAY_INHERITANCE_CHANGED";

    private async Task RefreshStalePreviewAsync()
    {
        var previous = _original;
        await ReloadAsync();
        if (_closed) return;
        _showPreview = false;
        Arrange();
        string source = "学校设置或来源已变化。";
        if (Selected is { Independent: true } && !_original.SameSources(previous))
        {
            var sources = _original.SourceClassIds.Select(id => _catalog.Classes.FirstOrDefault(room => room.Id == id)?.Name ?? "不可用班级");
            var names = _original.SourceMode == "OwnClass" ? "各班自己的作业" :
                string.Join("、", sources.Take(3)) + (_original.SourceClassIds.Length > 3 ? $"等 {_original.SourceClassIds.Length} 班" : "");
            source = $"年级当前来源为{names}。";
        }
        Notice($"本次未保存。{source}已保留你改过的字段，请重新预览。");
    }

    private async Task VerifyAsync(Operation operation)
    {
        int? revision = await _client.ReadSchoolStudySaveAsync(operation.Edit, operation.Preview, operation.Id, _lifetime.Token);
        if (_closed) return;
        if (revision is { } saved)
        {
            await ConfirmedAsync(saved);
            return;
        }
        _checkedMissing = true;
        Notice("暂未找到本账号的回执。可再次核对，或由服务器按原编号和原预览重新检查。");
    }

    private async Task ConfirmedAsync(int revision)
    {
        Operations.Remove(OperationKey);
        _checkedMissing = false;
        _savedRevision = revision;
        Published = true;
        _review = null; _edit = null; _draftBeforeReview = null;
        var target = Selected;
        var current = await _client.ReadSchoolStudyAsync(_school.Id, _lifetime.Token);
        if (_closed) return;
        if (current.TermId != _scope.TermId || current.Classes.All(row => row.Id != _scope.ClassId))
            throw new ConsoleApiException(409, $"学校已保存 v{revision}。当前学期或班级后来已变化，请返回核对。", "TERM_NOT_ACTIVE");
        _catalog = current;
        BindTargets(target);
        Notice($"学校已保存 · 已核对本人回执 v{revision}。教室应用请逐屏核对。");
        await ReadDisplayAsync();
        if (_closed) return;
        await ReadScreenEvidenceAsync();
    }

    private async void CheckReceipt(object? sender, RoutedEventArgs args)
    {
        if (Pending is { } operation) await RunAsync(() => VerifyAsync(operation));
    }
    private async void Retry(object? sender, RoutedEventArgs args)
    {
        if (_checkedMissing && Pending is { } operation) await RunAsync(() => SubmitAsync(operation));
    }

    private void SetDisplay(StudyDisplaySnapshot? display)
    {
        _display = display;
        _page = 0;
        _pages = display?.Items.SelectMany(item =>
        {
            var indexes = StringInfo.ParseCombiningCharacters(item.Content);
            int count = Math.Max(1, (indexes.Length + 819) / 820);
            return Enumerable.Range(0, count).Select(index =>
            {
                int begin = indexes.Length == 0 ? 0 : indexes[index * 820];
                int end = (index + 1) * 820 < indexes.Length ? indexes[(index + 1) * 820] : item.Content.Length;
                return new DisplayPage(item, item.Content[begin..end], index + 1, count);
            });
        }).ToArray() ?? [];
        DisplayPreviewContext.Text = display is null ? "学校当天的作业待核对" :
            $"{(_review is null ? "学校当前设置" : "保存后的预览")} · {SchoolStudyWindow.SourceText(display.Source)}";
        DisplayHomeworkState.Text = display?.State switch
        {
            "Available" => $"{display.Items.Length} 项已发布作业 · 每页 {display.Homework?.RotationSeconds} 秒",
            "Off" => "当天作业展示已关闭",
            "NoPolicy" => "此班暂无生效的学校自习展示策略",
            "Empty" => "当天暂无可展示的已发布作业",
            "Unavailable" => "作业暂不可读取，请核对来源或稍后刷新",
            _ => "作业读取状态待核对"
        };
        DisplayExpandedSources.IsVisible = display?.Sources.Length > 0;
        DisplaySourceLabels.Text = display is null ? "" : string.Join("\n", display.Sources.Select(row =>
            $"{(row.Type == "COURSE_GROUP" ? "走班" : "行政班")} · {row.Name} {row.Code}" +
            (row.SubjectName is { } subject ? $" · {subject}" : "")));
        RenderSample();
        RenderPage();
    }

    private void RenderPage()
    {
        DisplayPaging.IsVisible = _pages.Length > 0;
        DisplayPrevious.IsEnabled = _page > 0;
        DisplayNext.IsEnabled = _page + 1 < _pages.Length;
        DisplayPageNumber.Text = _pages.Length > 0 ? $"内容预览第 {_page + 1}/{_pages.Length} 段" : null;
        var page = _pages.Length == 0 ? null : _pages[_page];
        DisplayAssignmentTitle.Text = page is null ? "" : $"{page.Item.Subject}　{page.Item.Title}" +
            (page.Parts > 1 ? $"（{page.Part}/{page.Parts}）" : "");
        DisplayAssignmentText.Text = page?.Body;
        DisplayAssignmentSources.Text = page is null ? "" : string.Join(" · ", page.Item.Sources.Select(row => row.Name));
    }
    private void PreviousPage(object? sender, RoutedEventArgs args) => TurnPage(-1);
    private void NextPage(object? sender, RoutedEventArgs args) => TurnPage(1);

    private void TurnPage(int direction)
    {
        int next = _page + direction;
        if (next < 0 || next >= _pages.Length) return;
        _page = next;
        RenderPage();
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed) DisplayAssignmentText.BringIntoView(new Rect(0, 0, 1, Math.Min(65, DisplayAssignmentText.Bounds.Height)));
        }, DispatcherPriority.Loaded);
    }
    private void ToggleSignal(object? sender, RoutedEventArgs args) { _sampleSignal = !_sampleSignal; RenderSample(); }

    private void RenderSample()
    {
        string? style = (_display?.Presentation?.NoiseDisplay ?? (DisplayNoiseStyle.SelectedItem as Choice)?.Id);
        bool estimated = _catalog?.BasePolicy("CLASS", _scope.ClassId)?.Monitoring.Estimated == true;
        DisplayNoiseSample.Text = !_sampleSignal ? "样例 · 暂无有效噪音信号" : style != "StatusDbfs" ? "样例 · 安静" :
            estimated ? "样例 · 安静 · 42 dB(A)（按本机参考值校准时）" : "样例 · 安静 · −57 dBFS";
        DisplaySampleTrend.IsVisible = _sampleSignal && style == "StatusTrend";
        DisplayToggleSignal.Content = _sampleSignal ? "试看无信号" : "恢复示例信号";
    }

    private async void RefreshScreens(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        await ReadScreenEvidenceAsync();
        Dispatcher.UIThread.Post(() => { if (!_closed) DisplayScreenRows.BringIntoView(); }, DispatcherPriority.Loaded);
    });
    private async Task ReadScreenEvidenceAsync()
    {
        DisplayDevicePanel.IsExpanded = true;
        DisplayScreenRows.ItemsSource = new StudyDisplayLine[] { new("正在核对本班绑定", "") };
        var metadata = await _client.ReadSchoolSeatingAsync(_scope, _lifetime.Token);
        var lines = await Task.WhenAll(metadata.Bindings.Select(ReadBindingAsync));
        if (!_closed) DisplayScreenRows.ItemsSource = lines.Length > 0 ? lines : [new("本班暂无已登记的大屏", "")];
    }

    private async Task<StudyDisplayLine> ReadBindingAsync(StudySeatingBinding binding)
    {
        string school = $"绑定编号 {binding.BindingId}\n" + (_savedRevision is { } revision ? $"学校保存回执 v{revision}\n" : "");
        if (binding.DeviceId is not { } device) return new(binding.Name, school + "无有效教室终端。应用与窗口状态待核对。");
        try
        {
            var receipt = await _client.ReadStudyReceiptAsync(_scope, device, _lifetime.Token);
            var seen = receipt.ReceivedAt?.ToOffset(TimeSpan.FromHours(8)).ToString("M/d HH:mm:ss");
            bool applied = receipt.ObservationFresh && receipt.ConfigurationApplied && receipt.Observation?.Version == receipt.Current.Version;
            var application = applied ? $"Host 已应用当前配置 · {seen}" : "Host 应用待核对" + (seen is null ? "" : $" · 最近策略回执 {seen}");
            var window = receipt.VisibleWindowAgeMs is { } visibleAge
                ? $"近期观测到自习窗口可见 · 核对于 {DateTimeOffset.Now:M/d HH:mm:ss}，当时约 {Math.Ceiling(visibleAge / 1000)} 秒前"
                : "窗口可见性待核对";
            return new(binding.Name, school + application + "\n" + window, binding);
        }
        catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403))
        {
            return new(binding.Name, school + "暂未读取本屏当前回执，应用与窗口状态待核对。", binding);
        }
    }

    private async void OpenFieldAction(object? sender, RoutedEventArgs args)
    {
        if (_closed || _busy || _fieldAction is not null || sender is not Button { DataContext: StudyDisplayLine selected } anchor ||
            selected.Registration is not { DeviceId: { } device } binding) return;
        string classroom = _catalog.Classes.First(row => row.Id == _scope.ClassId).Name;
        var window = new DeviceFieldActionWindow(_client, new(_scope.SchoolId, _scope.TermId, _scope.ClassId, binding.BindingId, device),
            _school.Name, classroom, binding.Name, device);
        _fieldAction = window;
        try { await window.ShowDialog(this); }
        finally { if (_fieldAction == window) _fieldAction = null; }
        if (_closed) return;
        if (window.Revoked) await RunAsync(ReadScreenEvidenceAsync);
        if (!_closed)
        {
            var control = anchor.IsEffectivelyVisible ? anchor : DisplayRefreshScreens;
            control.BringIntoView();
            control.Focus();
        }
    }
}
