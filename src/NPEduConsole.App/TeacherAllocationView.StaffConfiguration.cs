using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TeacherAllocationView
{
    private bool CanOpenStaffConfiguration(StaffConfigurationDocument? file) => file is not null && !_archivedCorrection &&
        Mode is "LOCAL_PIN" or "SHARED_PASSWORD" && Term is { Status: "ACTIVE" } term &&
        file.AcademicYear == term.AcademicYear && file.Semester == term.Semester &&
        string.Equals(file.SchoolCode, _catalog?.SchoolCode, StringComparison.OrdinalIgnoreCase);

    private void OfferStaffConfiguration(string text)
    {
        if (OpenStaffConfiguration is null || _archivedCorrection) return;
        StaffConfigurationDocument file;
        try { file = StaffConfigurationDocument.Read(text); }
        catch (ArgumentException) { return; }
        if (CanOpenStaffConfiguration(file))
        {
            _staffConfigurationFile = file;
            _unsupportedFile = "这是完整师资配置。可到专页核对当前启用学期的全校方案，返回时保留原教师表。";
            return;
        }
        var declared = $"文件 {file.SchoolCode ?? "未填学校代码"} · {file.AcademicYear?.ToString() ?? "未填"} 学年 · 第 {file.Semester?.ToString() ?? "未填"} 学期";
        _unsupportedFile = $"这是完整师资配置。{declared}。当前所选 {_catalog?.SchoolCode ?? _school.Name} · {Term?.Name ?? "未选学期"}。完整师资方案面向当前启用学期及本地账号，可先在开学配置核对。";
    }

    private async void OpenStaffConfigurationClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanEdit || OpenStaffConfiguration is not { } open || _staffConfigurationFile is not { } file ||
            !CanOpenStaffConfiguration(file) || Term is not { } term) return;
        _staffConfigurationFile = null;
        _unsupportedFile = null;
        await RunAsync(async () =>
        {
            if (!await open(file, term.Id) || _closed) return;
            AccessChanged = true;
            ClearReview();
            await LoadAsync(term.Id, _catalog?.GradeId);
            if (!_closed) Status("已重新读取教师目录。原教师表已保留，请重新预览后再保存原表。");
        });
        if (!_closed) FocusLater(AllocationJson);
    }
}
