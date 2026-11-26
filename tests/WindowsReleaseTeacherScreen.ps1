# Sourced by the published executable acceptance harness after management login.
Add-Type -Path (Join-Path $PSScriptRoot 'ReleaseFilePicker.cs')
$mainHandle = $script:hwnd
$targets = $fixture.teacherScreenRetirement
function Wait-TeacherScreenWindow([string] $description) {
    Wait-For $description {
        $handle = [ReleaseFilePicker]::VisibleOwnedWindow($appProcess.Id, $mainHandle)
        if ($handle -ne [IntPtr]::Zero) { $handle }
    }
}

Invoke-Control $targets.taskId
try {
    $script:hwnd = Wait-TeacherScreenWindow 'the original teacher access dialog'
    Wait-Text 'TeacherAccessImpact' $targets.teacherUsername
    Wait-Text 'TeacherAccessImpact' '助教'
    Save-ReleaseScreenshot 'released-teacher-original-access' 'TeacherAccessConfirm'
    Invoke-Control 'TeacherAccessConfirm'
    Wait-Text 'TeacherAccessNotice' '已核对本人回执'
    Invoke-Control 'TeacherAccessBack'
} finally { $script:hwnd = $mainHandle }

Invoke-Control 'OpenDeviceDirectoryButton'
Invoke-Control 'DeviceDutyDirectory'
try {
    $script:hwnd = Wait-For 'the original school device directory' {
        if (Get-Control 'DutyBack' -Onscreen) { return $mainHandle }
        $handle = [ReleaseFilePicker]::VisibleOwnedWindow($appProcess.Id, $mainHandle)
        if ($handle -ne [IntPtr]::Zero) { $handle }
    }
    $sameWindow = $script:hwnd -eq $mainHandle
    Set-Checked 'DutyHistory'
    Set-Field 'DutySearch' $targets.bindingId
    Invoke-Control 'DutyOpen'
    if ($sameWindow) {
        $details = (Wait-For 'binding details' { Get-Control 'DutyBindingDetails' -Onscreen }).GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
        $details.Expand()
        Wait-Text 'DutyBindingIdentity' $targets.bindingId
        $details.Collapse()
    } else { Wait-Text 'DutyIdentity' $targets.bindingId }
    Wait-Text 'DutyIdentity' '归档'
    Size-ReleaseWindow 1160 780
    Save-ReleaseScreenshot 'released-historical-binding-wide' 'DutyBack'
    Size-ReleaseWindow 840 480
    Save-ReleaseScreenshot 'released-historical-binding-narrow' 'DutyBack'
    if ($sameWindow) { Invoke-Control 'DutyManageStep' }
    Invoke-Control 'DutyUnbindBrowser'
    Invoke-Control 'DutyConfirm'
    Wait-Text 'DutyReviewWeb' '原学期归档期间无法网页登录'
    Save-ReleaseScreenshot 'released-historical-unlink-narrow' 'DutyConfirm'
    Invoke-Control 'DutyConfirm'
    Wait-Text 'DutyNotice' '服务器已确认'
    Invoke-Control 'DutyBack'
    Set-Field 'DutySearch' $targets.placementBindingId
    Invoke-Control 'DutyOpen'
    if ($sameWindow) {
        $details = (Wait-For 'binding details' { Get-Control 'DutyBindingDetails' -Onscreen }).GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
        $details.Expand()
        Wait-Text 'DutyBindingIdentity' $targets.placementBindingId
        $details.Collapse()
        Invoke-Control 'DutyManageStep'
    } else { Wait-Text 'DutyIdentity' $targets.placementBindingId }
    $directoryHandle = $script:hwnd
    Invoke-Control 'DutyPlacement'
    try {
        $script:hwnd = Wait-For 'screen placement under the original activation round' {
            $handle = [ReleaseFilePicker]::VisibleOwnedWindow($appProcess.Id, $directoryHandle)
            if ($handle -ne [IntPtr]::Zero) { $handle }
        }
        Wait-Text 'PlacementBindingId' $targets.placementBindingId
        Wait-Text 'PlacementOriginal' '发行验收归档学期'
        Save-ReleaseScreenshot 'released-original-activation-round' 'PlacementBack'
        Invoke-Control 'PlacementBack'
    } finally { $script:hwnd = $directoryHandle }
    Write-Output 'PASS: the verified release repaired exact teacher access and unbound the original archived screen browser.'
} finally { $script:hwnd = $mainHandle }
