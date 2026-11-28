using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NPEduConsole.App;

public sealed partial class SchoolPresetExportView : UserControl
{
    private bool _hasPreset;
    private bool _saving;

    public SchoolPresetExportView()
    {
        InitializeComponent();
    }

    internal event Action? CloseRequested;
    internal event Action? SaveRequested;
    internal event Action? ReloadRequested;

    internal void SetLoading()
    {
        _hasPreset = false;
        _saving = false;
        LoadingCard.IsVisible = true;
        ErrorCard.IsVisible = false;
        PreviewCard.IsVisible = false;
        FeedbackText.IsVisible = false;
        FeedbackText.Text = null;
        UpdateActionState();
    }

    internal void ShowPreset(string schoolName, string schoolCode, string backendUrl)
    {
        _hasPreset = true;
        _saving = false;
        SchoolNameText.Text = schoolName;
        SchoolCodeText.Text = string.IsNullOrWhiteSpace(schoolCode) ? "暂无" : schoolCode;
        BackendUrlText.Text = backendUrl;
        LoadingCard.IsVisible = false;
        ErrorCard.IsVisible = false;
        PreviewCard.IsVisible = true;
        UpdateActionState();
    }

    internal void ShowError(string message)
    {
        _hasPreset = false;
        _saving = false;
        ErrorText.Text = message;
        LoadingCard.IsVisible = false;
        PreviewCard.IsVisible = false;
        ErrorCard.IsVisible = true;
        FeedbackText.IsVisible = false;
        FeedbackText.Text = null;
        UpdateActionState();
    }

    internal void SetSaving(bool saving)
    {
        _saving = saving;
        UpdateActionState();
    }

    internal void ShowFeedback(string? message)
    {
        FeedbackText.Text = message;
        FeedbackText.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void UpdateActionState()
    {
        SaveProgress.IsVisible = _saving;
        SaveButton.IsEnabled = _hasPreset && !_saving;
        ReloadButton.IsEnabled = !_saving;
        CloseButton.IsEnabled = !_saving;
    }

    private void CloseClicked(object? sender, RoutedEventArgs args)
    {
        if (_saving) return;
        CloseRequested?.Invoke();
    }

    private void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (!_hasPreset || _saving) return;
        SaveRequested?.Invoke();
    }

    private void ReloadClicked(object? sender, RoutedEventArgs args)
    {
        if (_saving) return;
        ReloadRequested?.Invoke();
    }
}
