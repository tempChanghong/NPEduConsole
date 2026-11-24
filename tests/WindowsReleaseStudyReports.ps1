# Use the running, independently extracted application from WindowsReleaseOobe.ps1.
function Select-VisibleItem($root, [string] $text) {
    $name = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $text)
    $pidMatch = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $appProcess.Id)
    $label = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.AndCondition]::new($name, $pidMatch))
    $pattern = $null
    while ($label -and $label.Current.ProcessId -eq $appProcess.Id -and !$label.Current.IsOffscreen) {
        if ($label.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref] $pattern)) {
            $pattern.Select()
            return $true
        }
        $label = [Windows.Automation.TreeWalker]::RawViewWalker.GetParent($label)
    }
}

Invoke-Control 'OpenClassWorkspaceButton'
$null = Wait-For 'isolated class in published directory' {
    $list = Get-Control 'ClassPicker' -Enabled
    if ($list) { Select-VisibleItem $list $fixture.studyReports.className }
}
Invoke-Control 'OpenClassStudyButton'
$mode = Wait-For 'released mode selector' { Get-Control 'StudyReportModePicker' -Enabled }
$mode.SetFocus()
$expand = $mode.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
$expand.Expand()
$null = Wait-For 'the released exam filter option' {
    Select-VisibleItem ([Windows.Automation.AutomationElement]::RootElement) '考试模式 · Windows 时间'
}
if ($expand.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Expanded) { $expand.Collapse() }
$session = Wait-For 'exam results from the real KV in the published client' {
    $picker = Get-Control 'ReportPicker' -Enabled
    if (!$picker) { return }
    $selection = $picker.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($selection.Count -eq 1 -and $selection[0].Current.Name.Contains(' · 考试 · ')) { $selection[0].Current.Name }
}
$mode.SetFocus()
Save-ReleaseScreenshot 'released-exam-report-filter' 'StudyReportModePicker'
[ordered]@{ version = $manifest.version; zipSha256 = $actualHash; report = $session; windows = $screenEvidence } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'released-study-reports.json') -Encoding utf8
Write-Output 'PASS: extracted Windows EXE opens current classroom and requests the Exam report mode.'
