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

function Test-FixtureLogin([string] $username, [string] $password) {
    $credentials = @{schoolCode = $fixture.login.code; username = $username; password = $password} | ConvertTo-Json -Compress
    try {
        $response = Invoke-RestMethod -Method Post -Uri ($fixture.login.url + '/accounts/local/login') -Body $credentials -ContentType 'application/json'
        return $response.success -eq $true -and ![string]::IsNullOrWhiteSpace($response.data.access_token)
    } catch { return $false }
}

Invoke-Control 'OpenSchoolOpeningButton'
Invoke-Control 'OpeningTeachers'
Invoke-Control 'OpeningSecondary'
Invoke-Control 'AllocationJson'
Complete-ReleaseFileDialog $fixture.batch.input '打开旧教师分配 JSON'
Wait-Text 'AllocationStatus' '已载入教师表'
Invoke-Control 'AllocationPreviewButton'
try { Set-Checked 'AllocationApprove' }
catch {
    Save-ReleaseScreenshot 'preview-needs-attention' 'AllocationClose'
    throw
}
Invoke-Control 'AllocationSave'
try { Invoke-Control 'TeacherBatchPinOpen' }
catch {
    Save-ReleaseScreenshot 'result-before-delivery' 'AllocationClose'
    throw 'The delivery action was unavailable after submitting the teacher table.'
}
Save-ReleaseScreenshot 'batch-review' 'TeacherBatchPinSave'
Invoke-Control 'TeacherBatchPinSave'
Complete-ReleaseFileDialog $fixture.batch.output '另存本次教师初始 PIN' -Save
$null = Wait-For 'the complete saved CSV' { Test-Path -LiteralPath $fixture.batch.output -PathType Leaf }
$bytes = [IO.File]::ReadAllBytes($fixture.batch.output)
if ($bytes.Length -lt 4 -or $bytes[0] -ne 239 -or $bytes[1] -ne 187 -or $bytes[2] -ne 191) { throw 'Missing UTF-8 BOM.' }
$rows = @(Import-Csv -LiteralPath $fixture.batch.output)
if ($rows.Count -ne 2 -or ($rows | Where-Object '短账号' -eq $fixture.batch.first).'初始PIN' -cne '003047' -or
    ($rows | Where-Object '短账号' -eq $fixture.batch.second).'初始PIN' -cne '002209') {
    throw 'Expected two new accounts with the original leading zero PIN.'
}
foreach ($row in $rows) {
    $expectedName = if ($row.'短账号' -ceq $fixture.batch.first) { $fixture.batch.firstName } else { $fixture.batch.secondName }
    if ($row.'姓名' -cne $expectedName -or $row.'学校代码' -cne $fixture.login.code -or
        $row.'短账号' -notin @($fixture.batch.first, $fixture.batch.second) -or
        !(Test-FixtureLogin $row.'短账号' $row.'初始PIN')) { throw 'Delivered teacher does not match the account at login.' }
}
if (!(Test-FixtureLogin 'viewer' $fixture.login.pin)) { throw 'Existing teacher must retain the original PIN.' }
@{users = @($rows.'短账号'); pinLogins = 2; originalViewerLogin = $true} | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $evidence 'batch-result.json') -Encoding utf8NoBOM
