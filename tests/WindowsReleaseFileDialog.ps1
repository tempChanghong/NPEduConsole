# Invoked in the isolated, logged-in process scope of WindowsReleaseOobe.ps1.
Add-Type -Path (Join-Path $PSScriptRoot 'ReleaseFilePicker.cs')
function Complete-ReleaseFileDialog([string] $path, [string] $title, [switch] $Save) {
    try {
        $null = Wait-For 'file name entry in the released application picker' {
            $focused = [Windows.Automation.AutomationElement]::FocusedElement
            $modernFileName = $false
            if ($focused -and $focused.Current.ProcessId -eq $appProcess.Id -and $focused.Current.AutomationId -ceq '1001') {
                $parent = [Windows.Automation.TreeWalker]::RawViewWalker.GetParent($focused)
                $modernFileName = $parent -and $parent.Current.AutomationId -ceq 'FileNameControlHost'
            }
            [ReleaseFilePicker]::Choose($appProcess.Id, $hwnd, $title, $path, $modernFileName, !$Save)
        }
        if ($Save) {
            $dialog = [Windows.Automation.AutomationElement]::FromHandle([ReleaseFilePicker]::VisibleDialog($appProcess.Id, $title))
            $field = [Windows.Automation.AutomationElement]::FocusedElement
            $nativeValue = $null
            if ($field.Current.ProcessId -eq $appProcess.Id -and $field.Current.ClassName -ceq 'Edit' -and
                $field.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$nativeValue)) {
                if ($nativeValue.Current.Value -cne $path) { $nativeValue.SetValue($path) }
                if ($nativeValue.Current.Value -cne $path) { throw 'The file picker did not accept the fixture path.' }
            }
            $buttonFilter = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, '1')
            $null = Wait-For 'the file picker save button' {
                foreach ($element in $dialog.FindAll([Windows.Automation.TreeScope]::Descendants, $buttonFilter)) {
                    if ($element.Current.ClassName -ceq 'Button' -and $element.Current.Name -match '^(保存|Save)' -and $element.Current.IsEnabled) {
                        $rect = $element.Current.BoundingRectangle
                        return [ReleaseFilePicker]::ClickSave($appProcess.Id, $title, [int]($rect.Left + $rect.Width/2), [int]($rect.Top + $rect.Height/2))
                    }
                }
            }
        }
        $null = Wait-For 'the file picker to finish' { [ReleaseFilePicker]::VisibleDialog($appProcess.Id, $title) -eq [IntPtr]::Zero }
    } catch {
        throw "$title — $($_.Exception.Message) Last check: $([ReleaseFilePicker]::LastCheck)"
    }
}
