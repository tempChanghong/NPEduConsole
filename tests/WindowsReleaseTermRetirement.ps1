# Invoked in the locked, isolated release EXE scope of WindowsReleaseOobe.ps1.
Invoke-Control 'OpenClassWorkspaceButton'
Invoke-Control 'OpenTermPreparationButton'
Wait-Text 'ActiveTermName' '暂无启用学期'
if ($fixture.termRetirement.stage -eq 'blank') {
    Invoke-Control 'TermBlankDraft'
    Set-Field 'DetailsYear' '2079'
    Set-Field 'DetailsSemester' '2'
    Set-Field 'DetailsName' $fixture.termRetirement.name
    Invoke-Control 'DetailsApply'
    Wait-Text 'DetailsAfter' '空白草稿'
    Save-ReleaseScreenshot 'released-zero-term-preview' 'DetailsApply'
    Invoke-Control 'DetailsApply'
    Wait-Text 'ViewedTermName' $fixture.termRetirement.name
    Save-ReleaseScreenshot 'released-zero-term-saved' 'TermEditDetails'
    Write-Output 'PASS: released Console created the first blank draft for the school with zero terms.'
    return
}

Wait-Text 'ViewedTermName' $fixture.termRetirement.name
Add-Type -Path (Join-Path $PSScriptRoot 'ReleaseFilePicker.cs')
Invoke-Control 'TermStatusActivate'
$mainHandle = $script:hwnd
try {
    $script:hwnd = Wait-For 'the original term activation window' {
        $handle = [ReleaseFilePicker]::VisibleOwnedWindow($appProcess.Id, $mainHandle)
        if ($handle -ne [IntPtr]::Zero) { $handle }
    }
    Size-ReleaseWindow 1160 780
    Wait-Text 'StatusAfter' $fixture.termRetirement.name
    Save-ReleaseScreenshot 'released-archived-activation-preview' 'StatusCommit'
    Invoke-Control 'StatusCommit'
    Wait-Text 'StatusMessage' '已核对服务器回执'
    Wait-Text 'StatusReceiptNumber' '操作回执'
    Save-ReleaseScreenshot 'released-archived-original-receipt' 'StatusBack'
    Invoke-Control 'StatusBack'
    Write-Output 'PASS: released Console recovered the original activation after the success response was dropped.'
}
finally { $script:hwnd = $mainHandle }
