# Runs in the extracted, verified EXE process of WindowsReleaseOobe.ps1.
. (Join-Path $PSScriptRoot 'WindowsReleaseFileDialog.ps1')
Invoke-Control 'OpenSchoolTransferButton'
$mainHandle = $script:hwnd
try {
    $script:hwnd = Wait-For 'the source school transfer window' {
        $handle = [ReleaseFilePicker]::VisibleOwnedWindow($appProcess.Id, $mainHandle)
        if ($handle -ne [IntPtr]::Zero) { $handle }
    }
    $null = Wait-For 'the source confirms the owner can export' { Get-Control 'TransferExportPrimary' -Onscreen -Enabled }
    Wait-Text 'TransferExportScope' $fixture.login.code
    Size-ReleaseWindow 1160 780
    Save-ReleaseScreenshot 'release-source-transfer-ready' 'TransferExportPrimary'
    Set-Field 'TransferExportPin' $fixture.login.pin -Secret
    Set-Field 'TransferExportPassword' $fixture.transfer.passphrase -Secret
    Invoke-Control 'TransferExportPrimary'
    Complete-ReleaseFileDialog $fixture.transfer.file '保存学校迁移包' -Save
    try { Wait-Text 'TransferExportNotice' '已保存' }
    catch {
        $notice = Get-Control 'TransferExportNotice'
        Save-ReleaseScreenshot 'release-source-transfer-unexpected' 'TransferExportClose'
        throw "The release export did not confirm saving. Window status: $($notice.Current.Name)"
    }
    if (!(Test-Path -LiteralPath $fixture.transfer.file -PathType Leaf)) { throw 'The Windows release did not save the transfer file.' }
    Save-ReleaseScreenshot 'release-source-transfer-saved' 'TransferExportClose'
    Invoke-Control 'TransferExportClose'
    Write-Output 'PASS: the extracted Windows release exported and saved a new encrypted package via the normal owner PIN workflow.'
}
finally { $script:hwnd = $mainHandle }
