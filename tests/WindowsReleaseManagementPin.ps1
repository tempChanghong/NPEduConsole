# Invoked in the isolated, logged-in release EXE scope of WindowsReleaseOobe.ps1.
. (Join-Path $PSScriptRoot 'WindowsReleaseFileDialog.ps1')
Invoke-Control 'OpenSchoolManagementAccountsButton'
$mainHandle = $script:hwnd
try {
    $script:hwnd = Wait-For 'account management window' {
        $handle = [ReleaseFilePicker]::VisibleOwnedWindow($appProcess.Id, $mainHandle)
        if ($handle -ne [IntPtr]::Zero) { $handle }
    }
    Size-ReleaseWindow 1160 780
    Invoke-Control 'ManagementCreate'
    Set-Field 'ManagementNewName' $fixture.accountsRetirement.name
    Set-Field 'ManagementNewUsername' $fixture.accountsRetirement.user
    Invoke-Control 'ManagementCreatePreview'
    Set-Field 'ManagementPin' $fixture.accountsRetirement.pin -Secret
    Invoke-Control 'SchoolAccountImpactConfirm'
    Wait-Text 'ManagementStatus' '服务器已确认本次操作'
    Wait-Text 'ManagementAccountTitle' $fixture.accountsRetirement.user
    Save-ReleaseScreenshot 'released-owner-wide-fresh-delivery' 'ManagementSavePin'

    Size-ReleaseWindow 840 480
    Save-ReleaseScreenshot 'released-owner-narrow-fresh-delivery' 'ManagementSavePin'
    Invoke-Control 'ManagementSavePin'
    Complete-ReleaseFileDialog $fixture.accountsRetirement.output '另存本次账号与 PIN' -Save
    try { Wait-Text 'ManagementStatus' '已另存本次凭据' }
    catch {
        $notice = Get-Control 'ManagementStatus'
        if ($notice) { Write-Output $notice.Current.Name }
        Save-ReleaseScreenshot 'released-native-save-failure' 'ManagementClose'
        throw
    }
    if (Get-Control 'ManagementSavePin' -Onscreen) { throw 'Delivery should be consumed after save.' }
    Write-Output 'PASS: the released EXE used the native picker and saved the confirmed PIN from the fresh write.'
    Invoke-Control 'ManagementClose'
} finally { $script:hwnd = $mainHandle }
