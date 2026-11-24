[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $FixturePath,
    [Parameter(Mandatory)][string] $ZipPath,
    [Parameter(Mandatory)][string] $EvidenceDirectory,
    [string] $ManifestPath,
    [switch] $ManagementLogin,
    [switch] $OwnerRecovery
)

$ErrorActionPreference = 'Stop'
$fixture = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
if (!$fixture.isolated) { throw 'This acceptance test requires disposable school servers.' }
$origins = if ($ManagementLogin -or $OwnerRecovery) { @($fixture.login.url, $fixture.login.website) } else { @($fixture.same.url, $fixture.split.url, $fixture.split.website) }
foreach ($address in $origins) {
    $uri = [Uri] $address
    if (!$uri.IsAbsoluteUri -or $uri.Scheme -ne 'http' -or $uri.Host -ne '127.0.0.1' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) {
        throw 'The fixture must use uncredentialed IPv4 loopback origins.'
    }
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing.Common
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ReleaseOobeWindow {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out Rect rect, int size);
}
'@
$null = [ReleaseOobeWindow]::SetThreadDpiAwarenessContext([IntPtr] (-4))

$zipItem = Get-Item -LiteralPath $ZipPath
if (!$ManifestPath) { $ManifestPath = Join-Path $zipItem.DirectoryName 'console-release.json' }
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$actualHash = (Get-FileHash -LiteralPath $zipItem.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($manifest.schemaVersion -ne 1 -or $manifest.platform -ne 'win-x64' -or $manifest.fileName -cne "NPEduConsole-$($manifest.version)-win-x64.zip" -or
    $manifest.sizeBytes -ne $zipItem.Length -or $manifest.sha256 -cne $actualHash) { throw 'The published ZIP does not match its release manifest.' }

$evidence = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($EvidenceDirectory)).FullName
$expanded = [IO.Directory]::CreateDirectory((Join-Path $evidence ('.console-exe-' + [Guid]::NewGuid().ToString('N')))).FullName
$moduleEvidence = [Collections.Generic.List[object]]::new()
$screenEvidence = [Collections.Generic.List[object]]::new()
$script:appProcess = $null
$preference = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'NPEduConsole/last-server.txt'
$previousPreference = if (Test-Path -LiteralPath $preference -PathType Leaf) { [IO.File]::ReadAllText($preference) } else { $null }

function Wait-For([string] $checkpoint, [scriptblock] $condition, [int] $seconds = 18) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        try {
            $result = & $condition
            if ($result) { return $result }
        }
        catch [Windows.Automation.ElementNotAvailableException] { }
        catch [System.Runtime.InteropServices.COMException] {
            if ($_.Exception.HResult -ne -2147418094 -or !$script:appProcess -or $script:appProcess.HasExited) { throw }
        }
        Start-Sleep -Milliseconds 100
    } while ($timer.Elapsed.TotalSeconds -lt $seconds)
    throw "Timed out at $checkpoint."
}

function Start-ReleaseConsole {
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $expanded 'NPEduConsole.App.exe'))
    $info.WorkingDirectory = $expanded
    $info.UseShellExecute = $false
    $info.EnvironmentVariables['PATH'] = "$env:WINDIR\System32;$env:WINDIR"
    $info.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $expanded 'no-external-runtime'
    $info.EnvironmentVariables['DOTNET_ROOT_X64'] = $info.EnvironmentVariables['DOTNET_ROOT']
    $info.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $script:appProcess = [Diagnostics.Process]::Start($info)
    $script:hwnd = Wait-For 'the released EXE main window' {
        $script:appProcess.Refresh()
        if ($script:appProcess.HasExited) { throw 'Published EXE exited before opening its window.' }
        $handle = $script:appProcess.MainWindowHandle
        if ($handle -ne [IntPtr]::Zero) { $handle }
    }
    $null = Wait-For 'the published login controls' { Get-Control 'OpenFirstSchoolSetup' -Onscreen }
    $modules = foreach ($name in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll')) {
        $module = $script:appProcess.Modules | Where-Object ModuleName -eq $name | Select-Object -First 1
        if (!$module -or [IO.Path]::GetDirectoryName($module.FileName) -ine $expanded) {
            throw "Runtime module $name did not load from the extracted release."
        }
        @{ name = $name; version = $module.FileVersionInfo.FileVersion; source = 'extracted ZIP directory' }
    }
    $moduleEvidence.Add(@{ pid = $script:appProcess.Id; modules = @($modules) })
}

function Stop-ReleaseConsole {
    if (!$script:appProcess) { return }
    try {
        if (!$script:appProcess.HasExited) {
            $null = $script:appProcess.CloseMainWindow()
            if (!$script:appProcess.WaitForExit(4000)) { $script:appProcess.Kill(); $script:appProcess.WaitForExit() }
        }
    }
    finally { $script:appProcess.Dispose(); $script:appProcess = $null }
}

function Get-Control([string] $id, [switch] $Onscreen, [switch] $Enabled) {
    $root = [Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
    if (!$root) { return }
    $filter = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    foreach ($element in $root.FindAll([Windows.Automation.TreeScope]::Descendants, $filter)) {
        if ((!$Onscreen -or !$element.Current.IsOffscreen) -and (!$Enabled -or $element.Current.IsEnabled)) { return $element }
    }
}

function Invoke-Control([string] $id) {
    $element = Wait-For "button $id" { Get-Control $id -Onscreen -Enabled }
    $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Set-Field([string] $id, [string] $value, [switch] $Secret) {
    $element = Wait-For "field $id" { Get-Control $id -Enabled }
    if (!$Secret) {
        $element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value)
        return
    }
    if ($id -notin @('SetupKey', 'SetupPin', 'SetupOwnerPin', 'Password', 'OwnerRecoverySetupKey', 'OwnerRecoveryNewPin', 'OwnerRecoveryRepeatPin') -or $value -notmatch '^[a-zA-Z0-9_-]+$') {
        throw 'Invalid target or input for the fixture credential.'
    }
    try { $element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
    catch { throw "UI Automation could not enter the fixture credential into $id." }
}

function Set-Checked([string] $id) {
    $element = Wait-For "checkbox $id" { Get-Control $id -Onscreen -Enabled }
    $pattern = $element.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($pattern.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) { $pattern.Toggle() }
}

function Wait-Text([string] $id, [string] $text) {
    $null = Wait-For "message $id" {
        $element = Get-Control $id
        $element -and $element.Current.Name.Contains($text)
    }
}

function Size-ReleaseWindow([int] $width, [int] $height) {
    $script:dpi = [ReleaseOobeWindow]::GetDpiForWindow($script:hwnd)
    $scale = $script:dpi / 96.0
    $client = [ReleaseOobeWindow+Rect]::new()
    $outer = [ReleaseOobeWindow+Rect]::new()
    $null = [ReleaseOobeWindow]::GetClientRect($script:hwnd, [ref] $client)
    $null = [ReleaseOobeWindow]::GetWindowRect($script:hwnd, [ref] $outer)
    $targetWidth = [int] [Math]::Round($width * $scale) + $outer.Right - $outer.Left - $client.Right
    $targetHeight = [int] [Math]::Round($height * $scale) + $outer.Bottom - $outer.Top - $client.Bottom
    if (![ReleaseOobeWindow]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 32, 32, $targetWidth, $targetHeight, 0x0044)) {
        throw 'Could not size the released window.'
    }
    $null = Wait-For "${width}x${height} client area" {
        $now = [ReleaseOobeWindow+Rect]::new()
        $null = [ReleaseOobeWindow]::GetClientRect($script:hwnd, [ref] $now)
        [Math]::Abs($now.Right / $scale - $width) -le 2 -and [Math]::Abs($now.Bottom / $scale - $height) -le 2
    }
    $script:clientWidth = $width
    $script:clientHeight = $height
}

function Save-ReleaseScreenshot([string] $name, [string] $visibleAction) {
    $null = [ReleaseOobeWindow]::SetForegroundWindow($script:hwnd)
    $null = Wait-For 'the release window foreground for capture' { [ReleaseOobeWindow]::GetForegroundWindow() -eq $script:hwnd }
    Start-Sleep -Milliseconds 180
    $rect = [ReleaseOobeWindow+Rect]::new()
    if ([ReleaseOobeWindow]::DwmGetWindowAttribute($script:hwnd, 9, [ref] $rect, 16) -ne 0) {
        $null = [ReleaseOobeWindow]::GetWindowRect($script:hwnd, [ref] $rect)
    }
    $action = (Wait-For "screenshot control $visibleAction" { Get-Control $visibleAction -Onscreen -Enabled }).Current.BoundingRectangle
    if ($action.Left -lt $rect.Left -or $action.Right -gt $rect.Right -or $action.Top -lt $rect.Top -or $action.Bottom -gt $rect.Bottom) {
        throw "The action $visibleAction is clipped at ${script:clientWidth}x${script:clientHeight}."
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, [Drawing.Size]::new($width, $height)) }
        finally { $graphics.Dispose() }
        $bitmap.Save((Join-Path $evidence "$name.png"), [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
    $screenEvidence.Add(@{ name = $name; widthDip = $script:clientWidth; heightDip = $script:clientHeight; dpi = $script:dpi })
}

function Open-Connection($instance) {
    Invoke-Control 'OpenFirstSchoolSetup'
    $website = if ($instance.website) { $instance.website } else { $instance.url }
    Set-Field 'SetupWebsiteAddress' $website
    if ($instance.website) { Set-Checked 'SetupSeparateApi'; Set-Field 'SetupApiAddress' $instance.url }
}

function Open-AuthorizedSetup([string] $button, $instance, [string] $nextControl) {
    Invoke-Control $button
    Set-Field 'SetupKey' $instance.setupKey -Secret
    Invoke-Control 'SetupPrimary'
    $null = Wait-For 'the authorized setup step' { Get-Control $nextControl -Enabled }
}

function Write-School($instance, [string] $name, [string] $term) {
    Set-Field 'SetupSchoolName' $name
    Set-Field 'SetupSchoolCode' $instance.code
    Set-Field 'SetupAdministratorName' '发行验收员'
    Set-Field 'SetupUsername' 'founder'
    Set-Field 'SetupPin' $instance.pin -Secret
    Set-Field 'SetupTermName' $term
    Set-Field 'SetupYear' '2026'
    Invoke-Control 'SetupPrimary'
    Wait-Text 'SetupOwnerInfo' $instance.code
}

function Complete-School($instance) {
    Set-Field 'SetupOwnerUsername' 'founder'
    Set-Field 'SetupOwnerPin' $instance.pin -Secret
    Invoke-Control 'SetupPrimary'
    $null = Wait-For 'the first class entry after real owner sign-in' { Get-Control 'OpenFirstSchoolClass' -Onscreen -Enabled }
}

try {
    [IO.Compression.ZipFile]::ExtractToDirectory($zipItem.FullName, $expanded)
    Start-ReleaseConsole
    if ($OwnerRecovery) {
        $credential = [Console]::In.ReadToEnd() | ConvertFrom-Json
        $login = $fixture.login
        Size-ReleaseWindow $login.width $login.height
        Set-Field 'ServerAddress' $login.url
        Set-Field 'SchoolCode' $login.code
        Set-Field 'Username' $login.username
        Invoke-Control 'OpenOwnerRecoveryButton'
        Invoke-Control 'OwnerRecoveryCheckServer'
        Invoke-Control 'OwnerRecoveryConfirmTarget'
        Wait-Text 'OwnerRecoveryVerifiedSchool' $login.code
        Wait-Text 'OwnerRecoveryVerifiedAddress' $login.url
        Save-ReleaseScreenshot '01-confirmed-target' 'OwnerRecoverySave'
        Set-Field 'OwnerRecoverySetupKey' $credential.setupKey -Secret
        Set-Field 'OwnerRecoveryNewPin' $credential.pin -Secret
        Set-Field 'OwnerRecoveryRepeatPin' $credential.pin -Secret
        Invoke-Control 'OwnerRecoverySave'
        $notice = if ($login.confirmed) { '服务器已确认恢复' } else { '恢复结果暂未确认' }
        Wait-Text 'OwnerRecoveryNotice' $notice
        Save-ReleaseScreenshot '02-server-result' 'OwnerRecoveryReturnToLogin'
        Invoke-Control 'OwnerRecoveryReturnToLogin'
        foreach ($field in @{SchoolCode = $login.code; Username = $login.username}.GetEnumerator()) {
            $textBox = Wait-For $field.Key { Get-Control $field.Key -Enabled }
            if ($textBox.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -cne $field.Value) {
                throw "Wrong recovery login target in $($field.Key)."
            }
        }
        Set-Field 'Password' $credential.pin -Secret
        Invoke-Control 'LoginButton'
        if ($login.expectLogin) {
            $null = Wait-For 'signed-in school selector' { Get-Control 'SchoolPicker' -Onscreen -Enabled }
        } else {
            $null = Wait-For 'ordinary login rejected' {
                $label = Get-Control 'LoginMessage' -Onscreen
                $label -and $label.Current.Name -match '错误|失败|有误|不正确|未接受'
            }
        }
        [ordered]@{version = $manifest.version; zipSha256 = $actualHash; code = $login.code; confirmed = $login.confirmed;
            loginAccepted = $login.expectLogin; runs = $moduleEvidence; screenshots = $screenEvidence} | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath (Join-Path $evidence 'released-recovery.json') -Encoding utf8
        return
    }
    if ($ManagementLogin) {
        Size-ReleaseWindow 1160 780
        Set-Field 'ServerAddress' $fixture.login.url
        Set-Field 'SchoolCode' $fixture.login.code
        Set-Field 'Username' $fixture.login.username
        Set-Field 'Password' $fixture.login.pin -Secret
        Invoke-Control 'LoginButton'
        Wait-Text 'OverviewFeedback' '核对时间'
        Save-ReleaseScreenshot 'windows-school-overview' 'OpenSchoolOpeningButton'
        if ($fixture.batch) { . (Join-Path $PSScriptRoot 'WindowsReleaseTeacherPins.ps1') }
        [ordered]@{version = $manifest.version; zipSha256 = $actualHash; api = $fixture.login.url; code = $fixture.login.code;
            runs = $moduleEvidence; screenshots = $screenEvidence} | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath (Join-Path $evidence 'windows-login.json') -Encoding utf8
        return
    }
    Size-ReleaseWindow 840 480
    Save-ReleaseScreenshot '01-narrow-released-first-launch' 'OpenFirstSchoolSetup'
    Open-Connection $fixture.split
    Set-Field 'SetupApiAddress' $fixture.same.url
    Invoke-Control 'SetupCheckConnection'
    Wait-Text 'SetupConnectionStatus' '尚未建校'
    Wait-Text 'SetupDeclarationStatus' '地址不一致'
    foreach ($action in @('InitializeNewSchool', 'RestoreSchoolTransfer', 'ResumeSchoolSetup')) {
        if (Get-Control $action -Onscreen -Enabled) { throw 'A mismatching API accepted initialization.' }
    }
    Save-ReleaseScreenshot '02-narrow-split-address-mismatch' 'SetupCheckConnection'
    Invoke-Control 'SetupConnectionBack'
    Open-Connection $fixture.same
    Invoke-Control 'SetupCheckConnection'
    Wait-Text 'SetupDeclarationStatus' '地址一致'
    Size-ReleaseWindow 1160 780
    Save-ReleaseScreenshot '03-wide-same-origin-checked' 'InitializeNewSchool'
    Open-AuthorizedSetup 'InitializeNewSchool' $fixture.same 'SetupSchoolName'
    Write-School $fixture.same '自包含同源学校' '同源发行验收学期'
    Complete-School $fixture.same
    Save-ReleaseScreenshot '04-wide-owner-ready' 'OpenFirstSchoolClass'
    Stop-ReleaseConsole

    Start-ReleaseConsole
    Size-ReleaseWindow 840 480
    Open-Connection $fixture.split
    Invoke-Control 'SetupCheckConnection'
    Wait-Text 'SetupDeclarationStatus' '地址一致'
    Open-AuthorizedSetup 'RestoreSchoolTransfer' $fixture.split 'SetupChoosePackage'
    Save-ReleaseScreenshot '05-narrow-migration-entry' 'SetupChoosePackage'
    Invoke-Control 'SetupClose'
    Invoke-Control 'SetupCheckConnection'
    Open-AuthorizedSetup 'InitializeNewSchool' $fixture.split 'SetupSchoolName'
    Write-School $fixture.split '自包含分域学校' '分域发行验收学期'
    Save-ReleaseScreenshot '06-narrow-pending-owner-before-restart' 'SetupPrimary'
    Stop-ReleaseConsole

    Start-ReleaseConsole
    Size-ReleaseWindow 840 480
    Open-Connection $fixture.split
    Invoke-Control 'SetupCheckConnection'
    Open-AuthorizedSetup 'ResumeSchoolSetup' $fixture.split 'SetupOwnerPin'
    Wait-Text 'SetupOwnerInfo' $fixture.split.code
    Save-ReleaseScreenshot '07-narrow-split-resumed-original-school' 'SetupPrimary'
    Complete-School $fixture.split
    Stop-ReleaseConsole

    Start-ReleaseConsole
    Size-ReleaseWindow 1160 780
    Open-Connection $fixture.split
    Invoke-Control 'SetupCheckConnection'
    Save-ReleaseScreenshot '08-wide-completed-ordinary-login-route' 'SetupLoginWithTarget'
    Invoke-Control 'SetupLoginWithTarget'
    Set-Field 'SchoolCode' $fixture.split.code
    Set-Field 'Username' 'founder'
    Set-Field 'Password' $fixture.split.pin -Secret
    Invoke-Control 'LoginButton'
    Save-ReleaseScreenshot '09-wide-ordinary-login-open-school' 'OpenSchoolOpeningButton'
    [ordered]@{ version = $manifest.version; zipInput = $zipItem.Name; zipSha256 = $actualHash; sizeBytes = $zipItem.Length;
        childPathExcludedDotnet = $true; externalRuntimeLookupDisabled = $true; runs = $moduleEvidence;
        screenshots = $screenEvidence; migration = 'authorized file-selection step only' } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'release-oobe-windows.json') -Encoding utf8
    Write-Output 'PASS: independently extracted release EXE, bundled runtime, same-origin creation, separate-API resume, migration entry and ordinary sign-in.'
}
finally {
    Stop-ReleaseConsole
    if (Test-Path -LiteralPath $preference -PathType Leaf) {
        $written = [IO.File]::ReadAllText($preference).TrimEnd('/')
        if ($written -in $origins) {
            if ($null -eq $previousPreference) { Remove-Item -LiteralPath $preference }
            else { [IO.File]::WriteAllText($preference, $previousPreference) }
        }
    }
    $actual = Get-Item -LiteralPath $expanded -ErrorAction SilentlyContinue
    if ($actual -and $actual.Parent.FullName -eq $evidence -and $actual.Name -match '^\.console-exe-[0-9a-f]{32}$' -and
        !($actual.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $actual.FullName -Recurse -Force
    }
}
