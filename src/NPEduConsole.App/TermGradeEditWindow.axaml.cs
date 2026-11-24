using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TermGradeEditWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private ConsoleClient? _client;
    private ClassCreationContext? _context;
    private ClassCreationContext? _observed;
    private string _gradeId = "";
    private bool _busy, _closed, _checking, _blocked;
    private GradeFields? _uncertain;
    private sealed record GradeFields(string Code, string Name, int Order);

    public TermGradeEditWindow()
    {
        InitializeComponent();
        Closing += (_, args) => args.Cancel = _busy && !_closed;
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    internal TermGradeEditWindow(ConsoleClient client, ClassCreationContext context, ClassCreationGrade grade) : this()
    {
        _client = client;
        _context = context;
        _gradeId = grade.Id;
        ShowScope(context, grade);
        GradeNameInput.Text = grade.Name;
        GradeCodeInput.Text = grade.Code;
        GradeOrderInput.Text = grade.SortOrder.ToString(CultureInfo.InvariantCulture);
        Opened += (_, _) => GradeNameInput.Focus();
    }

    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal void CloseForScopeChange() { _closed = true; Close(); }

    private void ShowScope(ClassCreationContext context, ClassCreationGrade grade) =>
        GradeScope.Text = $"{context.TermName} · {context.TermStatus} / {grade.Name}";

    private bool TryReadOrder(out int order) =>
        int.TryParse(GradeOrderInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out order);

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _checking || _blocked || _client is not { } client || _context is not { } context) return;
        if (!TryReadOrder(out int order) || order is < -10000 or > 10000)
        {
            GradeEditNotice.Text = "显示顺序请输入 −10000 至 10000 的整数。";
            GradeOrderInput.Focus();
            return;
        }
        var draft = new GradeFields((GradeCodeInput.Text ?? "").Trim().ToUpperInvariant(), (GradeNameInput.Text ?? "").Trim(), order);
        _busy = true;
        Render();
        try
        {
            await client.UpdateTermGradeAsync(context, _gradeId, draft.Code, draft.Name, draft.Order, _lifetime.Token);
            if (!_closed)
            {
                _busy = false;
                Close();
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            GradeEditNotice.Text = error.Message;
            bool uncertain = error.StatusCode is 0 or 408 or >= 500;
            if (uncertain) _uncertain = draft;
            _checking = uncertain || error.Code == "ORGANIZATION_VERSION_CONFLICT";
            _blocked = error.Code is "GRADE_NOT_FOUND" or "TERM_STRUCTURE_SCOPE_CHANGED" || error.StatusCode is 401 or 403;
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
            if (error.Code == "GRADE_CODE_EXISTS") GradeCodeInput.Focus();
        }
        finally
        {
            _busy = false;
            if (!_closed) Render();
        }
        if (_checking && !_closed) await ReadCurrentAsync();
    }

    private async void CheckClicked(object? sender, RoutedEventArgs args) => await ReadCurrentAsync();
    private async Task ReadCurrentAsync()
    {
        if (_busy || _blocked || _client is not { } client || _context?.TermId is not { } termId) return;
        _busy = true;
        _observed = null;
        Render();
        try
        {
            var current = (await client.GetTermClassStructureAsync(_context.SchoolId, termId, _lifetime.Token)).Creation;
            if (_closed) return;
            if (current.Grades.FirstOrDefault(grade => grade.Id == _gradeId) is not { } grade)
            {
                _blocked = true;
                GradeEditNotice.Text = "所选年级已不可用。";
                return;
            }
            GradeServerValue.Text = $"服务器当前　{grade.Name}（{grade.Code}） · 顺序 {grade.SortOrder}";
            GradeServerValue.IsVisible = true;
            if (!ConsoleClient.IsSameTermStructure(_context, current))
            {
                _blocked = true;
                GradeEditNotice.Text = "学期状态已变化。草稿仍在，请返回学期管理重新核对。";
            }
            else if (_uncertain == new GradeFields(grade.Code, grade.Name, grade.SortOrder))
            {
                _blocked = true;
                GradeEditNotice.Text = "已核对，服务器当前值与本次提交一致。";
            }
            else
            {
                _observed = current;
                GradeEditNotice.Text = "草稿已保留。采用当前版本会更新你未修改的字段。";
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            GradeEditNotice.Text = _uncertain is null ? $"暂时无法读取当前值。{error.Message}"
                : $"暂时无法核对本次保存，输入已锁定。请重试读取当前值。{error.Message}";
            if (error.StatusCode is 401 or 403) _blocked = true;
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        }
        finally { _busy = false; if (!_closed) Render(); }
    }

    private void AdoptClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _blocked || _observed is not { } current ||
            _context?.Grades.FirstOrDefault(row => row.Id == _gradeId) is not { } before ||
            current.Grades.FirstOrDefault(row => row.Id == _gradeId) is not { } latest) return;

        if ((GradeNameInput.Text ?? "").Trim() == before.Name) GradeNameInput.Text = latest.Name;
        if ((GradeCodeInput.Text ?? "").Trim().ToUpperInvariant() == before.Code) GradeCodeInput.Text = latest.Code;
        if (TryReadOrder(out int order) && order == before.SortOrder)
            GradeOrderInput.Text = latest.SortOrder.ToString(CultureInfo.InvariantCulture);

        _context = current;
        _observed = null;
        _checking = false;
        _uncertain = null;
        ShowScope(current, latest);
        GradeEditNotice.Text = "未修改的字段已更新为服务器当前值。请核对你的修改后再保存。";
        Render();
        GradeEditSave.Focus();
    }

    private void Render()
    {
        GradeEditSave.IsEnabled = !_busy && !_checking && !_blocked;
        GradeEditClose.IsEnabled = !_busy;
        GradeCurrentCheck.IsVisible = _checking && !_blocked;
        GradeCurrentCheck.IsEnabled = !_busy;
        GradeAdoptVersion.IsVisible = _observed is not null && !_blocked;
        GradeAdoptVersion.IsEnabled = !_busy;
        GradeNameInput.IsReadOnly = GradeCodeInput.IsReadOnly = GradeOrderInput.IsReadOnly =
            _busy || _blocked || _uncertain is not null && _checking;
    }

    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();
}
