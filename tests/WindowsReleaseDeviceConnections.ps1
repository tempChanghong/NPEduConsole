# Exercises only the published UI, using the browser-downloaded and hashed executable.
Add-Type -Path (Join-Path $PSScriptRoot 'ReleaseFilePicker.cs')
$mainHandle = $script:hwnd
$targets = $fixture.deviceConnections

function Open-ConnectionWorkbench {
    Invoke-Control 'DeviceConnectionsButton'
    $script:hwnd = Wait-For 'the published device connection workbench' {
        $owned = [ReleaseFilePicker]::VisibleOwnedWindow($appProcess.Id, $mainHandle)
        if ($owned -ne [IntPtr]::Zero) { $owned }
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

Invoke-Control 'OpenDeviceDirectoryButton'
Open-ConnectionWorkbench
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

    Select-ConnectionOption 'ConnectionApprovalTab' '' -Tab
    Set-Field 'ConnectionCode' $targets.rearCode
    Invoke-Control 'ConnectionResolve'
    Wait-Text 'ConnectionCandidateInfo' '按班级发起的接入申请'
    Wait-Text 'ConnectionApprovalTarget' $targets.rearBinding
    Set-Checked 'ConnectionApprovalConfirmed'
    Invoke-Control 'ConnectionApprove'
    Wait-Text 'ConnectionApprovalResult' '等待现场'
    Request-ConnectionCheckpoint 'confirm-rear'
    Invoke-Control 'ConnectionReadApproval'
    Wait-Text 'ConnectionApprovalResult' '等待该设备首次有效上报'

    Set-Field 'ConnectionCode' $targets.legacyCode
    Invoke-Control 'ConnectionResolve'
    Wait-Text 'ConnectionCandidateInfo' '管理员批准配对'
    Select-ConnectionOption 'ConnectionApprovalScreens' $targets.legacyBinding
    Set-Checked 'ConnectionApprovalConfirmed'
    Invoke-Control 'ConnectionApprove'
    Wait-Text 'ConnectionApprovalResult' $targets.legacyBinding
    Wait-Text 'ConnectionApprovalResult' '等待现场'
    if ($targets.reviewApproval) { Request-ConnectionCheckpoint 'expire-legacy-approval' }
    Invoke-Control 'ConnectionCancelApproval'
    if ($targets.reviewApproval) {
        Wait-Text 'ConnectionApprovalResult' '本申请已过期'
        if (Get-Control 'ConnectionCancelApproval' -Onscreen -Enabled) { throw 'Expired approval can still be cancelled.' }
        Set-Field 'ConnectionCode' $targets.cancellationCode
        Invoke-Control 'ConnectionResolve'
        Wait-Text 'ConnectionCandidateInfo' '管理员批准配对'
        Select-ConnectionOption 'ConnectionApprovalScreens' $targets.legacyBinding
        Set-Checked 'ConnectionApprovalConfirmed'
        Invoke-Control 'ConnectionApprove'
        Wait-Text 'ConnectionApprovalResult' '等待现场'
        Invoke-Control 'ConnectionCancelApproval'
        Wait-Text 'ConnectionApprovalResult' '本申请已取消'
    } else { Wait-Text 'ConnectionApprovalResult' '本申请已取消' }
    Invoke-Control 'ConnectionExit'
    $script:hwnd = $mainHandle
    Open-ConnectionWorkbench
    Select-ConnectionOption 'ConnectionApprovalHistory' $targets.rearPair
    Invoke-Control 'ConnectionReadApproval'
    Wait-Text 'ConnectionApprovalResult' '等待该设备首次有效上报'
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
        if (!$state.Contains($targets.rearPair) -or $state.Contains('已收到原设备有效上报')) {
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
