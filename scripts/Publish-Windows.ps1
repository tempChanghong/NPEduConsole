[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')]
    [string] $Version,

    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
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
    dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $published `
        "-p:Version=$Version" -p:PublishTrimmed=false -p:DebugSymbols=false -p:DebugType=None
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
