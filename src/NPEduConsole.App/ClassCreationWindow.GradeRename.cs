using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class ClassCreationWindow
{
    private enum RenameReadbackReason { Conflict, Uncertain, ManualCheck, Refresh }

    private ClassCreationGrade? _renameGrade;
    private string? _renameContextTermId;
    private string _renameAttemptedName = "";
    private string _renameOriginalName = "";
    private bool _editingGradeRename;
    private bool _renameSubmitting;
    private bool _renameChecking;
    private bool _renameNeedsReadback;
    private bool _renameRefreshRequired;
    private bool _renameUnsupported;
    private bool _renameCanRetry;
    private bool _gradeRenameVersionStale;

    internal bool RenameConfirmed { get; private set; }
    internal bool GradeDirectoryChanged { get; private set; }
    internal string? RenamedGradeName { get; private set; }

    private void OpenGradeRenameClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || _editingGradeRename || _editingGrade || _request is not null || _submitting || _renameSubmitting ||
            _renameChecking || _blocked || _renameUnsupported || _gradeRenameVersionStale || _pending is not null ||
            _pendingGrade is not null || !GradeContextReady) return;
        if (CreationGrade.SelectedItem is not ClassCreationGrade grade || grade.UpdatedAt is null) return;

        _editingGradeRename = true;
        _renameGrade = grade;
        _renameContextTermId = _context?.TermId;
        _renameOriginalName = grade.Name;
        _renameAttemptedName = "";
        _renameNeedsReadback = _renameRefreshRequired = _renameUnsupported = _renameCanRetry = false;
        _gradeRenameVersionStale = false;
        RenameGradeName.Text = grade.Name;
        GradeRenameServerName.Text = null;
        GradeRenameServerName.IsVisible = false;
        SetFeedback("仅修改年级名称；已填写的班级草稿会保留。");
        Render();
        Dispatcher.UIThread.Post(() =>
        {
            if (!_editingGradeRename || _closed) return;
            RenameGradeName.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void CancelGradeRenameClicked(object? sender, RoutedEventArgs args)
    {
        if (_renameSubmitting || _renameChecking) return;
        ResetRenameState();
        SetFeedback(_contextInvalid ? "请刷新学期与年级后重新选择。" : _context is { } context ? CreationContextMessage(context) : null);
        Render();
    }

    private async void SubmitGradeRenameClicked(object? sender, RoutedEventArgs args) => await SubmitRenameAsync();

    private async Task SubmitRenameAsync()
    {
        if (_closed || _client is not { } client || _school is null || _request is not null || _blocked ||
            _renameSubmitting || _renameChecking || _renameNeedsReadback || _renameRefreshRequired || _renameUnsupported ||
            _gradeRenameVersionStale || !_editingGradeRename || !GradeContextReady || _pending is not null ||
            _pendingGrade is not null || _context is not { } context || _renameGrade is not { } snapshot) return;

        string name = (RenameGradeName.Text ?? "").Trim();
        if (name.Length is < 1 or > 191)
        {
            SetFeedback("年级名称需为 1 至 191 个字符。", error: true);
            RenameGradeName.Focus();
            return;
        }
        if (name == snapshot.Name)
        {
            SetFeedback("年级名称未变化，无需提交。");
            return;
        }

        _renameAttemptedName = name;
        _renameCanRetry = false;
        using var request = new CancellationTokenSource();
        _request = request;
        _renameSubmitting = true;
        SetFeedback("正在保存年级名称…");
        Render();
        try
        {
            var renamed = await client.RenameCurrentGradeAsync(context, snapshot.Id, name, request.Token);
            if (_request != request) return;
            await ApplyRenameSuccessAsync(client, renamed, request.Token);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_request != request) return;
            if (error.StatusCode is 401 or 403)
            {
                LoseAccess(error);
                return;
            }
            if (error.StatusCode is 409 or 0 or 408 or >= 500)
            {
                SetFeedback(error.Message, error: true);
                Render();
                await ReadRenameContextAsync(request.Token, error.StatusCode == 409
                    ? RenameReadbackReason.Conflict : RenameReadbackReason.Uncertain);
            }
            else HandleRenameError(error);
        }
        finally
        {
            if (_request == request) _renameSubmitting = false;
            EndRequest(request);
            Render();
        }
    }

    private async Task ApplyRenameSuccessAsync(ConsoleClient client, ClassCreationGrade renamed, CancellationToken ct)
    {
        RenameConfirmed = true;
        GradeDirectoryChanged = true;
        RenamedGradeName = renamed.Name;
        CloseNotice = null;
        if (_context is not { } context) return;
        SetFeedback($"服务器已确认修改为「{renamed.Name}」，正在刷新年级列表…");
        try
        {
            var directory = await client.GetClassCreationContextAsync(context.SchoolId, ct);
            if (_context is null) return;
            var current = directory.Grades.FirstOrDefault(grade => grade.Id == renamed.Id);
            if (directory.TermId != context.TermId || current is null || current.UpdatedAt is null)
            {
                _contextInvalid = true;
                _editingGradeRename = false;
                _renameGrade = null;
                _renameNeedsReadback = false;
                _renameCanRetry = false;
                _renameRefreshRequired = false;
                RenameGradeName.Clear();
                GradeRenameServerName.Text = $"服务器已确认名称：「{renamed.Name}」，但当前学期或年级列表已变化。";
                GradeRenameServerName.IsVisible = true;
                CreationGrade.SelectedItem = null;
                SetFeedback("年级名称已由服务器确认，但当前学期或年级列表已变化。请刷新学期与年级并重新选择；班级草稿已保留。", error: true);
                CloseNotice = "年级名称已修改，但当前学期或年级列表已变化。请刷新后重新选择。";
                return;
            }

            ShowContext(directory, current.Id);
            _contextInvalid = false;
            _gradeRenameVersionStale = false;
            _renameGrade = current;
            _renameNeedsReadback = false;
            _renameCanRetry = false;
            _renameRefreshRequired = false;
            _editingGradeRename = false;
            RenameGradeName.Clear();
            // PATCH 回包是本次确认值；随后的 GET 可能已含别人的更新，两者必须分开表述。
            bool superseded = current.Name != renamed.Name;
            GradeRenameServerName.Text = superseded
                ? $"本次已确认名称：「{renamed.Name}」；服务器最新名称：「{current.Name}」"
                : $"服务器已确认名称：「{current.Name}」";
            GradeRenameServerName.IsVisible = true;
            SetFeedback(superseded
                ? $"本次修改已由服务器确认，确认值为「{renamed.Name}」；随后刷新看到该年级最新名称为「{current.Name}」，可能已被他人更新。已按同一 ID 继续选中该年级，班级草稿已保留；如需改为「{renamed.Name}」，可再次修改名称。"
                : $"服务器已确认修改为「{current.Name}」，已按 ID 恢复选中，班级草稿已保留。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ConsoleApiException error)
        {
            if (error.StatusCode is 401 or 403)
            {
                LoseAccess(error);
                return;
            }

            // The write was acknowledged. Keep the confirmed name visible and allow the
            // class draft to continue, but require a directory refresh before another rename.
            var grades = context.Grades.Select(grade => grade.Id == renamed.Id ? renamed : grade).ToArray();
            CreationGrade.ItemsSource = grades;
            CreationGrade.SelectedItem = grades.FirstOrDefault(grade => grade.Id == renamed.Id);
            _contextInvalid = false;
            _gradeRenameVersionStale = true;
            _renameGrade = renamed;
            _editingGradeRename = false;
            _renameNeedsReadback = false;
            _renameCanRetry = false;
            _renameRefreshRequired = false;
            RenameGradeName.Clear();
            GradeRenameServerName.Text = $"服务器已确认名称：「{renamed.Name}」";
            GradeRenameServerName.IsVisible = true;
            SetFeedback($"服务器已确认修改为「{renamed.Name}」，但年级列表暂未刷新。班级草稿已保留；可继续建班，刷新后可再次改名。", error: true);
        }
    }

    private async Task ReadRenameContextAsync(CancellationToken ct, RenameReadbackReason reason)
    {
        if (_client is not { } client || _context is not { } context || _renameGrade is not { } snapshot ||
            _renameContextTermId is not { } originalTerm) return;
        _renameChecking = true;
        Render();
        try
        {
            var directory = await client.GetClassCreationContextAsync(context.SchoolId, ct);
            if (_context is null) return;
            ApplyRenameReadback(directory, snapshot, originalTerm, reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ConsoleApiException error)
        {
            if (error.StatusCode is 401 or 403)
            {
                LoseAccess(error);
                return;
            }
            MarkRenameUnverified(error.Message);
        }
        finally
        {
            _renameChecking = false;
        }
    }

    private void ApplyRenameReadback(ClassCreationContext directory, ClassCreationGrade snapshot, string originalTerm,
        RenameReadbackReason reason)
    {
        if (directory.TermId != originalTerm)
        {
            MarkRenameRefreshRequired("当前启用学期已改变，无法在原学期核实该年级名称。",
                "未能核实：当前启用学期已改变。");
            return;
        }
        var current = directory.Grades.FirstOrDefault(grade => grade.Id == snapshot.Id);
        if (current is null)
        {
            MarkRenameRefreshRequired("当前学期已找不到此年级，无法核实年级名称。",
                "未能核实：当前学期已找不到此年级。");
            return;
        }

        ShowContext(directory, current.Id);
        _contextInvalid = false;
        _gradeRenameVersionStale = current.UpdatedAt is null;
        _renameGrade = current;
        _renameNeedsReadback = false;
        _renameRefreshRequired = false;
        _renameUnsupported = false;
        GradeRenameServerName.IsVisible = true;
        if (current.Name != _renameOriginalName)
        {
            GradeDirectoryChanged = true;
            RenamedGradeName = current.Name;
        }
        if (current.UpdatedAt is null)
        {
            _renameRefreshRequired = true;
            _renameCanRetry = false;
            GradeRenameServerName.Text = $"服务器当前名称：「{current.Name}」，但未提供年级版本信息。";
            SetFeedback("已读取服务器当前名称，但该 KV 未提供年级版本信息，无法安全提交改名。请刷新年级列表或升级 KV；班级草稿已保留。", error: true);
            CloseNotice = "年级改名未提交：当前 KV 未提供年级版本信息。";
            return;
        }

        string prefix = reason switch
        {
            RenameReadbackReason.Conflict => "年级名称存在版本冲突。",
            RenameReadbackReason.Uncertain => "保存结果未确认。",
            RenameReadbackReason.ManualCheck => "只读核对结果：",
            _ => "已刷新年级。"
        };
        if (_renameAttemptedName.Length == 0)
        {
            _renameCanRetry = false;
            GradeRenameServerName.Text = $"服务器当前名称：「{current.Name}」";
            SetFeedback($"{prefix}服务器当前名称为「{current.Name}」，可继续编辑名称。班级草稿已保留。");
            return;
        }

        bool matches = current.Name == _renameAttemptedName;
        _renameCanRetry = !matches;
        if (matches)
        {
            GradeRenameServerName.Text = $"服务器当前名称：「{current.Name}」（与本次输入一致，不能据此认定本次写入已生效）";
            SetFeedback($"{prefix}已读取到服务器当前名称「{current.Name}」，与本次输入一致；这不能证明本次修改已生效。已保留班级草稿，可返回建班或刷新后核对。",
                error: true);
            CloseNotice = "年级改名结果待核对：服务器当前名称与本次输入一致，但无法确认由本次写入造成。";
        }
        else
        {
            GradeRenameServerName.Text = $"服务器当前名称：「{current.Name}」（与本次输入「{_renameAttemptedName}」不符）";
            SetFeedback($"{prefix}已读取到服务器当前名称「{current.Name}」，与本次输入不符，本次修改可能未生效。请核对后手动再次确认提交。",
                error: true);
            CloseNotice = "年级改名结果待核对：服务器当前名称与本次输入不符。请重新打开建班窗口核对。";
        }
    }

    private async void CheckGradeRenameClicked(object? sender, RoutedEventArgs args) => await CheckRenameAsync();

    private async Task CheckRenameAsync()
    {
        if (_closed || _client is null || _request is not null || _blocked || _renameSubmitting || _renameChecking ||
            !_editingGradeRename || !_renameNeedsReadback || _renameUnsupported || _context is null || _renameGrade is null)
            return;
        using var request = new CancellationTokenSource();
        _request = request;
        SetFeedback("正在只读核对服务器当前名称…");
        Render();
        try { await ReadRenameContextAsync(request.Token, RenameReadbackReason.ManualCheck); }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally { EndRequest(request); Render(); }
    }

    private void HandleRenameError(ConsoleApiException error)
    {
        switch (error.Code)
        {
            case "GRADE_RENAME_UNSUPPORTED":
                _renameUnsupported = true;
                _editingGradeRename = false;
                _renameNeedsReadback = _renameCanRetry = _renameRefreshRequired = false;
                RenameGradeName.Clear();
                SetFeedback(error.Message + "建班和新建年级仍可使用。", error: true);
                break;
            case "GRADE_RENAME_TERM_CHANGED":
            case "GRADE_NOT_FOUND":
            case "EXPECTED_UPDATED_AT_INVALID":
                MarkRenameRefreshRequired(error.Message,
                    error.Code == "GRADE_RENAME_TERM_CHANGED" ? "未能核实：当前启用学期已改变。"
                    : "未能核实服务器当前名称。");
                break;
            case "GRADE_INVALID":
                SetFeedback(error.Message, error: true);
                RenameGradeName.Focus();
                break;
            default:
                SetFeedback(error.Message, error: true);
                break;
        }
    }

    private void MarkRenameUnverified(string? detail)
    {
        _renameNeedsReadback = true;
        _renameCanRetry = false;
        _renameRefreshRequired = false;
        GradeRenameServerName.Text = "未能核实服务器当前名称。";
        GradeRenameServerName.IsVisible = true;
        SetFeedback("暂时无法读取服务器当前名称，已保留班级草稿。请先点击「只读核对」，核实前不能再次提交。" +
                    (string.IsNullOrWhiteSpace(detail) ? "" : $" {detail}"), error: true);
        CloseNotice = "年级改名结果待核对：未能读取服务器当前名称。请重新打开建班窗口核对。";
    }

    private void MarkRenameRefreshRequired(string message, string serverText)
    {
        _contextInvalid = true;
        _renameNeedsReadback = false;
        _renameCanRetry = false;
        _renameRefreshRequired = true;
        GradeRenameServerName.Text = serverText;
        GradeRenameServerName.IsVisible = true;
        SetFeedback(message + " 已保留班级草稿；请点击「刷新学期与年级」并重新选择后再改名。", error: true);
        CloseNotice = "年级改名未完成：" + message + " 请刷新学期与年级并重新选择。";
    }

    private void ApplyRenameContextRefresh(ClassCreationContext directory)
    {
        _gradeRenameVersionStale = false;
        if (!_editingGradeRename || _renameGrade is not { } snapshot || _renameContextTermId is not { } originalTerm)
            return;
        if (directory.TermId != originalTerm)
        {
            FinishRenameAfterRefresh("已刷新到新的启用学期，原年级改名草稿未提交。请重新选择年级后再改名；班级草稿已保留。");
            return;
        }
        if (directory.Grades.FirstOrDefault(grade => grade.Id == snapshot.Id) is null)
        {
            FinishRenameAfterRefresh("已刷新年级，原年级已不在当前学期。请重新选择年级后再改名；班级草稿已保留。");
            return;
        }
        ApplyRenameReadback(directory, snapshot, originalTerm, RenameReadbackReason.Refresh);
    }

    private void FinishRenameAfterRefresh(string message)
    {
        _editingGradeRename = false;
        _renameGrade = null;
        _renameNeedsReadback = false;
        _renameCanRetry = false;
        _renameRefreshRequired = false;
        RenameGradeName.Clear();
        GradeRenameServerName.Text = null;
        GradeRenameServerName.IsVisible = false;
        CloseNotice = null;
        SetFeedback(message, error: true);
    }

    private void ResetRenameState()
    {
        _editingGradeRename = false;
        _renameGrade = null;
        _renameContextTermId = null;
        _renameAttemptedName = "";
        _renameOriginalName = "";
        _renameNeedsReadback = false;
        _renameRefreshRequired = false;
        _renameUnsupported = false;
        _renameCanRetry = false;
        _gradeRenameVersionStale = false;
        RenameGradeName.Clear();
        GradeRenameServerName.Text = null;
        GradeRenameServerName.IsVisible = false;
    }

    private void GradeRenameFieldsChanged(object? sender, RoutedEventArgs args)
    {
        if (_closed) return;
        Render();
    }

    private void RenderGradeRename(bool busy)
    {
        if (_closed || GradeRenameFields is null) return;
        GradeRenameFields.IsVisible = _editingGradeRename;
        ShowRenameTarget(_editingGradeRename ? _renameGrade : null);
        OpenGradeRenameButton.IsEnabled = !busy && GradeContextReady && !_editingGrade && !_editingGradeRename &&
            _pending is null && _pendingGrade is null && !_gradeRenameVersionStale &&
            CreationGrade.SelectedItem is ClassCreationGrade { UpdatedAt: not null };
        if (!_editingGradeRename) return;

        string name = (RenameGradeName.Text ?? "").Trim();
        bool canEdit = !busy && !_blocked && !_renameUnsupported && !_renameRefreshRequired;
        RenameGradeName.IsEnabled = canEdit;
        CheckGradeRenameButton.IsVisible = _renameNeedsReadback;
        CheckGradeRenameButton.IsEnabled = !busy && !_blocked && !_renameUnsupported;
        SubmitGradeRenameButton.Content = _renameSubmitting ? "正在保存…" : _renameChecking ? "正在核对…"
            : _renameNeedsReadback ? "请先核对" : _renameRefreshRequired ? "请刷新并重选"
            : _renameCanRetry ? "再次确认修改" : "保存名称";
        SubmitGradeRenameButton.IsEnabled = canEdit && !_renameNeedsReadback && _renameGrade is not null &&
            !_gradeRenameVersionStale && name.Length is >= 1 and <= 191 && name != _renameGrade.Name;
        CancelGradeRenameButton.IsEnabled = !busy && !_renameSubmitting && !_renameChecking && !_renameNeedsReadback;
    }

    // 建班表单在改名期间收起，因此改名区域要自己说明正在改哪个年级（名称或代码）。
    private void ShowRenameTarget(ClassCreationGrade? grade)
    {
        GradeRenameTarget.Text = grade is null ? null
            : string.IsNullOrWhiteSpace(grade.Code) ? $"目标年级：{grade.Name}"
            : $"目标年级：{grade.Name}（{grade.Code}）";
    }
}
