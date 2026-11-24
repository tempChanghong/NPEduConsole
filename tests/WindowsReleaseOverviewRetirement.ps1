# Run only inside the isolated released Windows EXE.
function Assert-OverviewMetrics($metrics) {
    Wait-Text 'OverviewMetrics' "启用走班 $($metrics.courseGroups) 个 · 任课教师 $($metrics.teachers) 人 · 网页值守在线 $($metrics.onlineScreens)/$($metrics.browserScreens) 台 · 错误 $($metrics.errors) 项 · 提醒 $($metrics.warnings) 项"
    $null = Wait-For 'onscreen statistics from the selected term' { Get-Control 'OverviewMetrics' -Onscreen }
}

Assert-OverviewMetrics $fixture.overviewRetirement.active
Save-ReleaseScreenshot 'released-overview-wide' 'OverviewMetrics'
Size-ReleaseWindow 840 480
Assert-OverviewMetrics $fixture.overviewRetirement.active
Save-ReleaseScreenshot 'released-overview-narrow' 'OverviewMetrics'
Size-ReleaseWindow 1160 780
Invoke-Control 'OpenClassWorkspaceButton'
Invoke-Control 'OpenTermPreparationButton'
Wait-Text 'ActiveTermName' $fixture.overviewRetirement.activeName
$null = Wait-For 'the original archived term in the published term directory' {
    $list = Get-Control 'PreparedTerms' -Onscreen -Enabled
    if (!$list) { return }
    $match = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,
        $fixture.overviewRetirement.archiveName)
    $element = $list.FindFirst([Windows.Automation.TreeScope]::Descendants, $match)
    $pattern = $null
    while ($element -and $element.Current.ProcessId -eq $appProcess.Id -and !$element.Current.IsOffscreen) {
        if ($element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref] $pattern)) {
            $pattern.Select()
            return $true
        }
        $element = [Windows.Automation.TreeWalker]::RawViewWalker.GetParent($element)
    }
}
Wait-Text 'ViewedTermName' $fixture.overviewRetirement.archiveName
Invoke-Control 'TermViewDiagnostics'
Wait-Text 'DiagnosticsHeading' $fixture.overviewRetirement.archiveName
Wait-Text 'DiagnosticsActiveTerm' $fixture.overviewRetirement.activeName
Assert-OverviewMetrics $fixture.overviewRetirement.archive
Save-ReleaseScreenshot 'released-original-archive-diagnostics-wide' 'OverviewMetrics'
Size-ReleaseWindow 840 480
Assert-OverviewMetrics $fixture.overviewRetirement.archive
Save-ReleaseScreenshot 'released-original-archive-diagnostics-narrow' 'OverviewMetrics'
Write-Output 'PASS: released Windows overview and exact archived diagnostics show KV metrics at both window sizes.'
