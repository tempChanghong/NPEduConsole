using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace NPEduConsole.App;

/// <summary>A visible field label and its inline validation message, around the original editor.</summary>
public sealed class FormField : HeaderedContentControl
{
    public static readonly StyledProperty<string?> ErrorMessageProperty =
        AvaloniaProperty.Register<FormField, string?>(nameof(ErrorMessage));

    public string? ErrorMessage
    {
        get => GetValue(ErrorMessageProperty);
        set => SetValue(ErrorMessageProperty, value);
    }

    public FormField()
    {
        AddHandler(TextBox.TextChangedEvent, (_, _) => ErrorMessage = null);
    }

    public void ReportError(string message)
    {
        ErrorMessage = message;
        if (Content is Control editor) editor.Focus();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ErrorMessageProperty) PseudoClasses.Set(":invalid", !string.IsNullOrEmpty(ErrorMessage));
    }
}
