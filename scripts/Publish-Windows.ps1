[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')]
    [string] $Version,

    [string] $OutputDirectory,

    [string] $IsccPath,

    [ValidateRange(1, 65534)]
    [int] $InstallerRevision
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$installer = $PSBoundParameters.ContainsKey('IsccPath')
$fileVersion = $null
if ($installer) {
    $compiler = Get-Item -LiteralPath $IsccPath
    if ($compiler -isnot [IO.FileInfo] -or $compiler.Name -ine 'ISCC.exe') {
        throw 'IsccPath 必须指向 Inno Setup 的 ISCC.exe。'
    }
    $coreText = $Version.Split('-')[0]
    $coreVersion = [version]$coreText
    if ($coreVersion.ToString(3) -cne $coreText -or
        $coreVersion.Major -gt 65535 -or $coreVersion.Minor -gt 65535 -or $coreVersion.Build -gt 65535) {
        throw '安装包版本的前三段必须是 0 至 65535 的规范数字。'
    }
    $isPreview = $Version.Contains('-')
    if ($isPreview -ne $PSBoundParameters.ContainsKey('InstallerRevision')) {
        throw '预发行安装包须传入递增的 InstallerRevision。正式版保留修订号 65535。'
    }
    $revision = if ($isPreview) { $InstallerRevision } else { 65535 }
    $fileVersion = "$coreText.$revision"
} elseif ($PSBoundParameters.ContainsKey('InstallerRevision')) {
    throw 'InstallerRevision 仅在构建安装包时使用。'
}
$project = Join-Path $repository 'src/NPEduConsole.App/NPEduConsole.App.csproj'
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $repository ".artifacts/windows-release/$Version"
}
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) {
    throw "发行目录已存在。请提供新的输出目录，保留已分发版本的原文件及校验值。"
}

$parent = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)).FullName
$stage = [IO.Directory]::CreateDirectory((Join-Path $parent ('.console-publish-' + [Guid]::NewGuid().ToString('N')))).FullName
try {
    $published = Join-Path $stage 'publish'
    $release = [IO.Directory]::CreateDirectory((Join-Path $stage 'release')).FullName
    $versionProperties = @("-p:Version=$Version")
    if ($installer) { $versionProperties += "-p:FileVersion=$fileVersion" }
    dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $published `
        @versionProperties -p:PublishTrimmed=false -p:DebugSymbols=false -p:DebugType=None
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）。" }

    foreach ($file in @('NPEduConsole.App.exe', 'NPEduConsole.App.runtimeconfig.json', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $published $file) -PathType Leaf)) {
            throw "自包含发行文件不完整，缺少 $file。"
        }
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $fileName = "NPEduConsole-$Version-win-x64.zip"
    $zip = Join-Path $release $fileName
    [IO.Compression.ZipFile]::CreateFromDirectory($published, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $manifest = [ordered]@{
        schemaVersion = 1
        platform = 'win-x64'
        version = $Version
        fileName = $fileName
        sizeBytes = (Get-Item -LiteralPath $zip).Length
        sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $json = $manifest | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $release 'console-release.json'), ($json + "`n"), [Text.UTF8Encoding]::new($false))
    if ($installer) {
        $app = Join-Path $published 'NPEduConsole.App.exe'
        if ([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -cne $fileVersion) {
            throw '自包含 EXE 的文件版本与安装器版本不符。'
        }
        $icon = Join-Path $repository 'images/branding/npeduconsole.ico'
        $definition = Join-Path $repository 'installer/NPEduConsole.iss'
        $defines = @("/DAppDisplayVersion=$Version", "/DAppFileVersion=$fileVersion", "/DAppIcon=$icon",
            "/DPackageRoot=$published", "/DOutputRoot=$release")
        & $compiler.FullName '/Qp' @defines $definition | Out-Host
        $setupName = "NPEduConsole-$Version-win-x64-setup.exe"
        $setup = Join-Path $release $setupName
        if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $setup -PathType Leaf)) {
            throw "Inno Setup 编译失败（退出码 $LASTEXITCODE）。"
        }
        $setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText("$setup.sha256", "$setupHash  $setupName`n", [Text.Encoding]::ASCII)
    }
    [IO.Directory]::Move($release, $destination)
    Write-Output $json
    Write-Host "发行目录 $destination"
}
finally {
    $actual = Get-Item -LiteralPath $stage -ErrorAction SilentlyContinue
    if ($actual -and $actual.Parent.FullName -eq $parent -and $actual.Name -match '^\.console-publish-[0-9a-f]{32}$' -and
        !($actual.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $actual.FullName -Recurse -Force
    }
}
