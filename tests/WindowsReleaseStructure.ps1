# Sourced by WindowsReleaseOobe.ps1 after the published executable has signed in.
Add-Type -Path (Join-Path $PSScriptRoot 'ReleaseFilePicker.cs')
$mainHandle = $script:hwnd
Invoke-Control $fixture.structureRetirement.taskId
try {
    $script:hwnd = Wait-For 'the original teaching diagnostic window' {
        $handle = [ReleaseFilePicker]::VisibleOwnedWindow($appProcess.Id, $mainHandle)
        if ($handle -ne [IntPtr]::Zero) { $handle }
    }
    Wait-Text 'PlanNotice' 'PHY-CONFLICT'
    Size-ReleaseWindow 1160 780
    Save-ReleaseScreenshot 'released-original-teaching-task-wide' 'PlanBack'
    Size-ReleaseWindow 840 480
    Save-ReleaseScreenshot 'released-original-teaching-task-narrow' 'PlanBack'
    Invoke-Control 'PlanBack'
    Write-Output 'PASS: the executable from the verified release ZIP opened the original classroom or course and subject from the school overview.'
}
finally { $script:hwnd = $mainHandle }
