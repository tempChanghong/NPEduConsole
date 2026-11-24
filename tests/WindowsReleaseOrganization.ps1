# Invoked from the same isolated EXE scope as WindowsReleaseOobe.ps1.
Add-Type -Path (Join-Path $PSScriptRoot 'ReleaseFilePicker.cs')
Invoke-Control 'OpenClassWorkspaceButton'
Invoke-Control 'OpenOrganizationImportButton'
Invoke-Control 'OrgOpenFile'
$null = Wait-For 'the native legacy JSON picker' {
    $focus = [Windows.Automation.AutomationElement]::FocusedElement
    $modern = $false
    if ($focus -and $focus.Current.ProcessId -eq $appProcess.Id -and $focus.Current.AutomationId -ceq '1001') {
        $parent = [Windows.Automation.TreeWalker]::RawViewWalker.GetParent($focus)
        $modern = $parent -and $parent.Current.AutomationId -ceq 'FileNameControlHost'
    }
    [ReleaseFilePicker]::Choose($appProcess.Id, $hwnd, '打开旧组织 JSON', $fixture.organization.file, $modern, $true)
}
Invoke-Control 'OrgCreateDeclaredTerm'
Invoke-Control 'DetailsApply'
Wait-Text 'DetailsAfter' '空白草稿'
Save-ReleaseScreenshot 'release-organization-blank-preview' 'DetailsApply'
Invoke-Control 'DetailsApply'
Wait-Text 'OrgScope' $fixture.organization.term
Invoke-Control 'OrgPreview'
Set-Checked 'OrgApprove'
Invoke-Control 'OrgSave'
Wait-Text 'OrgStatus' '由服务器确认'
Save-ReleaseScreenshot 'release-organization-result' 'OrgClose'
[ordered]@{version = $manifest.version; zipSha256 = $actualHash; status = 'APPLIED'; term = $fixture.organization.term;
    windows = $screenEvidence} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'released-organization.json') -Encoding utf8
Write-Output 'PASS: extracted Windows EXE opens the old JSON, explicitly creates the declared empty draft, and imports into that draft.'
