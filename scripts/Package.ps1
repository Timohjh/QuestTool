[CmdletBinding()]
param([switch] $IncludeApp)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $projectRoot 'artifacts'
$sourceZip = Join-Path $outputRoot 'QuestTool-source.zip'
$appZip = Join-Path $outputRoot 'QuestTool-windows.zip'
$checksumFile = Join-Path $outputRoot 'SHA256SUMS.txt'
foreach ($output in @($sourceZip, $appZip, $checksumFile)) {
    if (Test-Path -LiteralPath $output) { throw "Publication output already exists. Move it aside first: $output" }
}
$stagingRoot = Join-Path $projectRoot ('.dotnet/publication/' + [Guid]::NewGuid().ToString('N'))
$sourceRoot = Join-Path $stagingRoot 'source'
New-Item -ItemType Directory -Path $outputRoot, $sourceRoot -Force | Out-Null

# An explicit file list keeps local settings, tool caches, binaries, logs,
# credentials, and future unreviewed files out of the source archive.
$sourceFiles = @(
    '.gitattributes', '.gitignore', 'LICENSE', 'README.md',
    'QuestTool.csproj', 'App.xaml', 'App.xaml.cs', 'GlobalUsings.cs',
    'MainWindow.xaml', 'MainWindow.xaml.cs', 'MainWindow.Files.cs',
    'MainWindow.Media.cs', 'MainWindow.Monitoring.cs', 'MainWindow.Refresh.cs', 'MainWindow.Apps.cs', 'Start-QuestTool.cmd',
    'Assets/QuestTool.svg', 'Assets/QuestTool.png', 'Assets/QuestTool.ico',
    'Core/AdbClient.cs', 'Core/ApkInfo.cs', 'Core/AppService.cs', 'Core/DeploymentService.cs',
    'Core/DeviceDetails.cs', 'Core/DeviceService.cs', 'Core/FileService.cs',
    'Core/MediaService.cs', 'Core/MirrorOptions.cs', 'Core/Models.cs',
    'Core/MonitoringService.cs', 'Core/WifiService.cs',
    'Tests/QuestTool.Tests.csproj', 'Tests/Program.cs', 'Tests/FileChecks.cs', 'Tests/AppChecks.cs',
    'Tests/MediaChecks.cs', 'Tests/MonitoringChecks.cs', 'Tests/NetworkDetailsChecks.cs',
    'Tests/RefreshChecks/QuestTool.RefreshChecks.csproj', 'Tests/RefreshChecks/Program.cs',
    'scripts/Package.ps1', 'docs/screenshots/headsets.png',
    'docs/screenshots/deploy.png', 'docs/screenshots/monitoring.png',
    'docs/screenshots/files.png', 'docs/screenshots/video.png'
)

foreach ($relative in $sourceFiles) {
    $source = Join-Path $projectRoot $relative
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing publication file: $relative" }
    $destination = Join-Path $sourceRoot $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archives = @()

if ($IncludeApp) {
    $appRoot = Join-Path $stagingRoot 'windows'
    # Framework-dependent: no SDK, runtime, ADB, or Meta executables are redistributed.
    & dotnet publish (Join-Path $projectRoot 'QuestTool.csproj') -c Release --self-contained false `
        '-p:DebugType=None' '-p:DebugSymbols=false' "-p:PathMap=$projectRoot=/_/QuestTool" -o $appRoot
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
    foreach ($file in Get-ChildItem -LiteralPath $appRoot -File) {
        if ($file.Name -notin @('QuestTool.exe', 'QuestTool.dll', 'QuestTool.deps.json', 'QuestTool.runtimeconfig.json')) {
            throw "Unexpected application output: $($file.Name)"
        }
    }
    if ((Get-ChildItem -LiteralPath $appRoot -File).Count -ne 4 -or (Get-ChildItem -LiteralPath $appRoot -Directory)) {
        throw 'Application output did not match the four-file release manifest.'
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $appRoot
    @'
QuestTool - Windows desktop preview

Extract this entire folder, then open QuestTool.exe. Keep its files together.
Requires the .NET 10 Windows Desktop Runtime for your PC architecture:
https://dotnet.microsoft.com/download/dotnet/10.0

This is a framework-dependent Windows build; the runtime is not included.
The executable is unsigned. It has not been tested on a clean Windows PC.
Do not disable Windows protections to run it.

Install Android SDK Platform-Tools separately and select adb.exe in Setup:
https://developer.android.com/tools/releases/platform-tools
Meta VR CLI and Meta casting/Link software are optional, separate installs.
No Android or Meta executables, app APKs, or headset data are bundled.

Use only headsets, networks, and APKs you own or are authorized to manage.
The app can change files, install APKs, and change device settings.
Wi-Fi passwords are not saved by QuestTool. Device details and paths are
stored in %LOCALAPPDATA%/QuestTool/settings.json; exported logs can be private.

Instructions: https://github.com/Timohjh/QuestTool#readme
Independent project; not affiliated with or endorsed by Meta.
Licensed under the MIT License. See LICENSE.
'@ | Set-Content -LiteralPath (Join-Path $appRoot 'START-HERE.txt') -Encoding UTF8
    [IO.Compression.ZipFile]::CreateFromDirectory($appRoot, $appZip)
    $archives += $appZip
}

[IO.Compression.ZipFile]::CreateFromDirectory($sourceRoot, $sourceZip)
$archives += $sourceZip
$checksums = foreach ($archive in $archives) {
    $hash = Get-FileHash -LiteralPath $archive -Algorithm SHA256
    '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($archive)
}
$checksums | Set-Content -LiteralPath $checksumFile -Encoding ASCII
Write-Output "Prepared $($archives.Count) archive(s) in $outputRoot"
Write-Output 'Review each archive before upload. This allowlist does not replace a secret or rights review.'
