# Exercises only the published UI, using the browser-downloaded and hashed executable.
Add-Type -Path (Join-Path $PSScriptRoot 'ReleaseFilePicker.cs')
$mainHandle = $script:hwnd
$targets = $fixture.deviceConnections

function Open-ConnectionWorkbench([string] $opener = 'DeviceConnectionsButton') {
    $script:hwnd = $mainHandle
    Invoke-Control $opener
    $script:hwnd = Wait-For 'the published device connection workbench' {
        if (Get-Control 'ConnectionExit' -Onscreen) { return $mainHandle }
        $owned = [ReleaseFilePicker]::VisibleOwnedWindow($appProcess.Id, $mainHandle)
        if ($owned -ne [IntPtr]::Zero) { $owned }
    }
    if ($targets.requireWorkspace -and ($script:hwnd -ne $mainHandle -or
        [ReleaseFilePicker]::VisibleOwnedWindow($appProcess.Id, $mainHandle) -ne [IntPtr]::Zero)) {
        throw 'The workbench must use the original main HWND without opening another window.'
    }
    Wait-Text 'ConnectionScope' '在用学期'
}

function Select-ConnectionOption([string] $id, [string] $name, [switch] $tab) {
    $control = Wait-For "available choice in $id" { Get-Control $id -Onscreen -Enabled }
    if ($tab) {
        $control.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        return
    }
    $expand = $control.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    $root = [Windows.Automation.AutomationElement]::RootElement
    $type = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::ListItem)
    $process = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $appProcess.Id)
    $match = [Windows.Automation.AndCondition]::new($type, $process)
    $item = Wait-For "choice $name" {
        foreach ($candidate in $root.FindAll([Windows.Automation.TreeScope]::Descendants, $match)) {
            if (!$candidate.Current.IsOffscreen -and $candidate.Current.Name.Contains($name)) { return $candidate }
        }
    }
    $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    if ($expand.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Expanded) { $expand.Collapse() }
}

function Request-ConnectionCheckpoint([string] $name) {
    New-Item -Path (Join-Path $targets.exchange "$name.request") -ItemType File | Out-Null
    $null = Wait-For "isolated fixture $name" { Test-Path -LiteralPath (Join-Path $targets.exchange "$name.ready") } 65
}

function Approval-ChoiceId([string] $id) {
    if ($sameWindow) { return $id.Substring($id.Length - 6) }
    return $id
}

function Open-NewApproval {
    if ($sameWindow -and (Get-Control 'ApprovalNewStepButton' -Onscreen) -and
        !(Get-Control 'ConnectionCode' -Onscreen)) { Invoke-Control 'ApprovalNewStepButton' }
}

function Assert-ApprovalIdentity([string] $id) {
    if (!$sameWindow) { Wait-Text 'ConnectionApprovalResult' $id; return }
    $details = Wait-For 'exact approval details' { Get-Control 'ApprovalResultDetails' -Onscreen -Enabled }
    $panel = $details.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($panel.Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Expanded) { $panel.Expand() }
    Wait-Text 'ApprovalResultIdentity' $id
    $panel.Collapse()
}

Invoke-Control 'OpenDeviceDirectoryButton'
Open-ConnectionWorkbench
$sameWindow = $script:hwnd -eq $mainHandle
try {
    Size-ReleaseWindow 1160 780
    Select-ConnectionOption 'ConnectionScreens' $targets.rearBinding
    Wait-Text 'ConnectionAdmissionState' '本班尚无管理口令'
    Set-Checked 'ConnectionPairingEnabled'
    Set-Checked 'ConnectionRotate'
    Set-Field 'ConnectionSecret' $targets.secret -Secret
    Set-Field 'ConnectionSecretAgain' $targets.secret -Secret
    Invoke-Control 'ConnectionAdmissionSave'
    Wait-Text 'ConnectionAdmissionState' '网页配对已开放'
    Wait-Text 'ConnectionAdmissionState' '版本 1'
    Select-ConnectionOption 'ConnectionScreens' $targets.frontBinding
    Wait-Text 'ConnectionAdmissionState' '版本 1'
    Wait-Text 'ConnectionAdmissionState' '网页配对未开放'
    Set-Checked 'ConnectionRotate'
    Set-Field 'ConnectionSecret' $targets.rotatedSecret -Secret
    Set-Field 'ConnectionSecretAgain' $targets.rotatedSecret -Secret
    Invoke-Control 'ConnectionAdmissionSave'
    Wait-Text 'ConnectionAdmissionState' '版本 2'
    Wait-Text 'ConnectionAdmissionState' '网页配对未开放'

    Select-ConnectionOption 'ConnectionBatchRange' '高二'
    Select-ConnectionOption 'ConnectionBatchChange' '关闭网页配对'
    Invoke-Control 'ConnectionBatchPreview'
    Wait-Text 'ConnectionBatchState' '变更 1 台'
    Save-ReleaseScreenshot '01-preauthorization-wide' 'ConnectionBatchConfirmed'
    Request-ConnectionCheckpoint 'change-batch'
    Set-Checked 'ConnectionBatchConfirmed'
    Invoke-Control 'ConnectionBatchApply'
    Wait-Text 'ConnectionMessage' '变化'
    Invoke-Control 'ConnectionBatchPreview'
    Wait-Text 'ConnectionBatchState' '变更 2 台'
    Set-Checked 'ConnectionBatchConfirmed'
    Invoke-Control 'ConnectionBatchApply'
    Wait-Text 'ConnectionMessage' '待核对'
    Invoke-Control 'ConnectionBatchCheck'
    Wait-Text 'ConnectionBatchState' '均已达到所选状态'
    Wait-Text 'ConnectionBatchState' '无法认定本人'

    if ($sameWindow) {
        Invoke-Control 'ConnectionExit'
        Invoke-Control ('SchoolDevice-' + $targets.frontBinding)
        Open-ConnectionWorkbench 'OpenScreenConnectionsButton'
        Select-ConnectionOption 'ConnectionApprovalTab' '' -Tab
        Size-ReleaseWindow 840 480
        Wait-Text 'ApprovalScopeText' ('#' + (Approval-ChoiceId $targets.frontBinding))
        Set-Field 'ConnectionCode' $targets.rearCode
        Invoke-Control 'ConnectionResolve'
        Wait-Text 'ConnectionMessage' '无法在这里批准'
        if (Get-Control 'ConnectionApprove' -Onscreen -Enabled) { throw 'Another screen would receive the approval.' }
        Save-ReleaseScreenshot '00-exact-screen-refuses-other-code-narrow' 'ConnectionCode'
        Set-Field 'ConnectionCode' '22222222'
        Invoke-Control 'ConnectionExit'
        Wait-Text 'ConnectionMessage' '尚未批准'
        Invoke-Control 'ApprovalDiscardButton'
        $null = Wait-For 'focus returned to the exact screen entry' {
            try {
                $entry = Get-Control 'OpenScreenConnectionsButton' -Onscreen -Enabled
                $entry -and $entry.Current.HasKeyboardFocus
            } catch {}
        }
        Invoke-Control 'OpenDeviceDirectoryButton'
        Open-ConnectionWorkbench
        Size-ReleaseWindow 1160 780
    }
    Select-ConnectionOption 'ConnectionApprovalTab' '' -Tab
    Open-NewApproval
    Set-Field 'ConnectionCode' $targets.rearCode
    Invoke-Control 'ConnectionResolve'
    Wait-Text 'ConnectionCandidateInfo' '按班级发起的接入申请'
    Wait-Text 'ConnectionApprovalTarget' (Approval-ChoiceId $targets.rearBinding)
    Set-Checked 'ConnectionApprovalConfirmed'
    Invoke-Control 'ConnectionApprove'
    Wait-Text 'ConnectionApprovalResult' '等待现场'
    Request-ConnectionCheckpoint 'confirm-rear'
    Invoke-Control 'ConnectionReadApproval'
    Wait-Text 'ConnectionApprovalResult' '等待该设备首次有效上报'
    if ($sameWindow) {
        Save-ReleaseScreenshot '01a-approval-side-by-side-wide' 'ConnectionCode'
        Size-ReleaseWindow 840 480
        Open-NewApproval
        $null = Wait-For 'narrow short code and action are both on screen' {
            (Get-Control 'ConnectionCode' -Onscreen -Enabled) -and (Get-Control 'ConnectionResolve' -Onscreen) -and
            (Get-Control 'ApprovalScopeText' -Onscreen)
        }
        Save-ReleaseScreenshot '01b-new-approval-with-pending-original-narrow' 'ConnectionCode'
    }

    Set-Field 'ConnectionCode' $targets.legacyCode
    Invoke-Control 'ConnectionResolve'
    Wait-Text 'ConnectionCandidateInfo' '管理员批准配对'
    Select-ConnectionOption 'ConnectionApprovalScreens' (Approval-ChoiceId $targets.legacyBinding)
    Set-Checked 'ConnectionApprovalConfirmed'
    Invoke-Control 'ConnectionApprove'
    Assert-ApprovalIdentity $targets.legacyBinding
    Wait-Text 'ConnectionApprovalResult' '等待现场'
    if ($targets.reviewApproval) { Request-ConnectionCheckpoint 'expire-legacy-approval' }
    Invoke-Control 'ConnectionCancelApproval'
    if ($targets.reviewApproval) {
        Wait-Text 'ConnectionApprovalResult' '本申请已过期'
        if (Get-Control 'ConnectionCancelApproval' -Onscreen -Enabled) { throw 'Expired approval can still be cancelled.' }
        Open-NewApproval
        Set-Field 'ConnectionCode' $targets.cancellationCode
        Invoke-Control 'ConnectionResolve'
        Wait-Text 'ConnectionCandidateInfo' '管理员批准配对'
        Select-ConnectionOption 'ConnectionApprovalScreens' (Approval-ChoiceId $targets.legacyBinding)
        Set-Checked 'ConnectionApprovalConfirmed'
        Invoke-Control 'ConnectionApprove'
        Wait-Text 'ConnectionApprovalResult' '等待现场'
        Invoke-Control 'ConnectionCancelApproval'
        Wait-Text 'ConnectionApprovalResult' '本申请已取消'
    } else { Wait-Text 'ConnectionApprovalResult' '本申请已取消' }
    if ($targets.requireWorkspace) {
        Open-NewApproval
        Set-Field 'ConnectionCode' $targets.otherCode
        Invoke-Control 'ConnectionResolve'
        Wait-Text 'ConnectionCandidateInfo' '管理员批准配对'
        Select-ConnectionOption 'ConnectionApprovalScreens' (Approval-ChoiceId $targets.legacyBinding)
        Set-Checked 'ConnectionApprovalConfirmed'
        Invoke-Control 'ConnectionApprove'
        Wait-Text 'ConnectionApprovalResult' '等待现场'
        Request-ConnectionCheckpoint 'report-other-approval'
        Invoke-Control 'ConnectionReadApproval'
        Wait-Text 'ConnectionApprovalResult' '已收到原设备有效上报'
        Assert-ApprovalIdentity $targets.legacyBinding
    }
    Invoke-Control 'ConnectionExit'
    $script:hwnd = $mainHandle
    Open-ConnectionWorkbench
    if ($sameWindow) {
        $null = Wait-For 'original request step is available on return' { Get-Control 'ApprovalExistingStepButton' -Onscreen -Enabled }
        $null = Wait-For 'approval history is visible' { Get-Control 'ConnectionApprovalHistory' -Onscreen -Enabled }
    }
    Select-ConnectionOption 'ConnectionApprovalHistory' (Approval-ChoiceId $targets.rearPair)
    Invoke-Control 'ConnectionReadApproval'
    Wait-Text 'ConnectionApprovalResult' '等待该设备首次有效上报'
    Assert-ApprovalIdentity $targets.rearPair
    if ($targets.requireWorkspace) {
        $null = Wait-For 'narrow original selection stays on screen while reading its result' {
            (Get-Control 'ConnectionApprovalHistory' -Onscreen -Enabled) -and (Get-Control 'ConnectionReadApproval' -Onscreen -Enabled)
        }
        Save-ReleaseScreenshot '01c-original-still-waiting-after-other-report-narrow' 'ConnectionReadApproval'
    }
    if (Get-Control 'ConnectionCancelApproval' -Onscreen -Enabled) { throw 'Cancellation targeted an already-confirmed device.' }
    Request-ConnectionCheckpoint 'report-rear'
    Invoke-Control 'ConnectionReadApproval'
    Wait-Text 'ConnectionApprovalResult' '已收到原设备有效上报'
    Size-ReleaseWindow 840 480
    Save-ReleaseScreenshot '02-original-approval-reported-narrow' 'ConnectionReadApproval'

    Size-ReleaseWindow 1160 780
    Invoke-Control 'ConnectionRefresh'
    Select-ConnectionOption 'ConnectionEquipmentTab' '' -Tab
    Select-ConnectionOption 'ConnectionEquipmentScreens' $targets.rearBinding
    Wait-Text 'ConnectionEquipmentState' 'Host 已报告应用当前版本'
    Select-ConnectionOption 'ConnectionMicrophone' '验收麦克风'
    Select-ConnectionOption 'ConnectionCamera' '验收摄像头'
    Set-Checked 'ConnectionEquipmentLocked'
    Set-Checked 'ConnectionEquipmentConfirmed'
    Invoke-Control 'ConnectionEquipmentSave'
    Wait-Text 'ConnectionEquipmentState' '本次保存版本 1'
    Wait-Text 'ConnectionEquipmentState' '当前版本待设备应用'
    Request-ConnectionCheckpoint 'apply-equipment'
    Invoke-Control 'ConnectionReadEquipment'
    Wait-Text 'ConnectionEquipmentState' 'Host 已报告应用当前版本'
    Size-ReleaseWindow 840 480
    Save-ReleaseScreenshot '03-host-equipment-receipt-narrow' 'ConnectionReadEquipment'
    Size-ReleaseWindow 1160 780
    if ($targets.reviewEquipment) {
        Request-ConnectionCheckpoint 'equipment-mismatch'
        Invoke-Control 'ConnectionReadEquipment'
        Wait-Text 'ConnectionEquipmentState' '尚未确认使用学校指定的摄像头'
        Wait-Text 'ConnectionEquipmentState' '尚未确认使用指定麦克风'
        Wait-Text 'ConnectionEquipmentState' '现场已解锁'
        $state = (Get-Control 'ConnectionEquipmentState').Current.Name
        if ($state.Contains('Host 已报告应用当前版本')) { throw 'A mismatched receipt was displayed as fully applied.' }
        Save-ReleaseScreenshot '04-mismatched-equipment-receipt-wide' 'ConnectionReadEquipment'

        Request-ConnectionCheckpoint 'equipment-near-expiry'
        Invoke-Control 'ConnectionReadEquipment'
        Wait-Text 'ConnectionEquipmentState' 'Host 已报告应用当前版本'
        Select-ConnectionOption 'ConnectionCamera' '清除学校指定'
        Set-Checked 'ConnectionEquipmentConfirmed'
        Wait-Text 'ConnectionEquipmentState' '当前清单已过期'
        if (Get-Control 'ConnectionEquipmentSave' -Enabled) { throw 'Aged equipment inventory still allows publishing.' }
        Save-ReleaseScreenshot '05-expired-equipment-receipt-wide' 'ConnectionReadEquipment'
        Request-ConnectionCheckpoint 'equipment-renew'
        Invoke-Control 'ConnectionReadEquipment'
        Wait-Text 'ConnectionEquipmentState' 'Host 已报告应用当前版本'
    }
    Select-ConnectionOption 'ConnectionCamera' '清除学校指定'
    Select-ConnectionOption 'ConnectionMicrophone' '清除学校指定'
    (Get-Control 'ConnectionEquipmentLocked' -Enabled).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    Set-Checked 'ConnectionEquipmentConfirmed'
    Invoke-Control 'ConnectionEquipmentSave'
    Wait-Text 'ConnectionEquipmentState' '本次保存版本 2'
    Request-ConnectionCheckpoint 'clear-equipment'
    Invoke-Control 'ConnectionReadEquipment'
    Wait-Text 'ConnectionEquipmentState' 'Host 已报告应用当前版本'
    if ($targets.reviewApproval) {
        Select-ConnectionOption 'ConnectionApprovalTab' '' -Tab
        Wait-Text 'ConnectionApprovalResult' '已收到原设备有效上报'
        Request-ConnectionCheckpoint 'withdraw-rear-approval-device'
        Invoke-Control 'ConnectionRefresh'
        Wait-Text 'ConnectionApprovalResult' '原设备授权已变化'
        $state = (Get-Control 'ConnectionApprovalResult').Current.Name
        Assert-ApprovalIdentity $targets.rearPair
        if ($state.Contains('已收到原设备有效上报')) {
            throw 'Refreshing after revocation lost the original request or kept its old online evidence.'
        }
        Size-ReleaseWindow 840 480
        Save-ReleaseScreenshot '06-original-approval-revoked-narrow' 'ConnectionReadApproval'
    }
    Write-Output 'PASS: released device connection workbench, independent approvals, unattributed batch recovery and real Host policy revisions.'
} catch {
    foreach ($id in @('ConnectionMessage', 'ConnectionAdmissionState', 'ConnectionBatchState', 'ConnectionApprovalResult', 'ConnectionEquipmentState')) {
        $element = Get-Control $id
        if ($element) { Write-Output "$id = $($element.Current.Name)" }
    }
    if (Get-Control 'ConnectionExit' -Onscreen -Enabled) { Save-ReleaseScreenshot 'workbench-failure' 'ConnectionExit' }
    throw
} finally { $script:hwnd = $mainHandle }
