using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private IReadOnlyList<Classroom>? _directoryClasses;
    private string? _directoryTermId;

    private void ClearClassDirectory()
    {
        _directoryClasses = null;
        ClassDirectoryCount.Text = "";
        _settingSelection = true;
        ClassPicker.ItemsSource = null;
        _settingSelection = false;
    }

    private Classroom? FilterClassDirectory(string? selectedId, bool revealTarget = false)
    {
        if (_directoryClasses is not { } rooms) return null;
        string[] words = (ClassSearch.Text ?? "").Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool Matches(Classroom room) => words.All(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
            || room.GradeName?.Contains(word, StringComparison.OrdinalIgnoreCase) == true
            || room.Code?.Contains(word, StringComparison.OrdinalIgnoreCase) == true);
        var selected = rooms.FirstOrDefault(room => room.Id == selectedId);
        _settingSelection = true;
        try
        {
            if (selected is not null && revealTarget && !Matches(selected))
            {
                ClassSearch.Clear();
                words = [];
            }
            var visible = words.Length == 0 ? rooms : rooms.Where(Matches).ToArray();
            ClassPicker.ItemsSource = visible;
            if (selected is not null && !Matches(selected)) selected = null;
            ClassPicker.SelectedItem = selected;
            ClassDirectoryCount.Text = words.Length == 0 ? $"{rooms.Count} 个班" : $"{visible.Count} / {rooms.Count} 个班";
            ClassListMessage.IsVisible = visible.Count == 0;
            ClassListMessage.Text = rooms.Count == 0 ? InactiveClassesFilter.IsChecked == true
                ? "当前学期暂无已停用行政班。" : "该学校暂无可查看班级。"
                : visible.Count == 0 ? "没有匹配的班级。" : null;
            return selected;
        }
        finally { _settingSelection = false; }
    }

    private async void ClassSearchChanged(object? sender, TextChangedEventArgs args)
    {
        ClearClassSearch.IsVisible = !string.IsNullOrEmpty(ClassSearch.Text);
        if (_settingSelection || _directoryClasses is null) return;
        var selected = FilterClassDirectory(_classroom?.Id);
        if (_classroom is not null && selected is null)
        {
            _showClassStudy = false;
            await SelectClassroomAsync(null);
        }
    }

    private void ClearClassSearchClicked(object? sender, RoutedEventArgs args)
    {
        ClassSearch.Clear();
        ClassSearch.Focus();
    }

    private void ClassSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(ClassSearch.Text)) ClassSearch.Clear();
        else if ((e.Key == Key.Down || e.Key == Key.Enter && ClassPicker.ItemCount == 1) && ClassPicker.ItemCount > 0)
        {
            if (ClassPicker.SelectedIndex < 0) ClassPicker.SelectedIndex = 0;
            ClassPicker.ScrollIntoView(ClassPicker.SelectedIndex);
            (ClassPicker.ContainerFromIndex(ClassPicker.SelectedIndex) as Control ?? ClassPicker).Focus();
        }
        else return;
        e.Handled = true;
    }
}
