# Builds the release downloads into artifacts/:
#   MistyStep-<version>-<arch>-setup.exe      installer (Inno Setup)
#   MistyStep-<version>-<arch>-portable.zip   portable zip
#   SHA256SUMS.txt                            checksums of the above
# Usage, from the repo root:
#   powershell -ExecutionPolicy Bypass -File tools/build-release.ps1 [-Version 1.2.3] [-Arch x64,arm64] [-SkipTests]
# The GitHub release workflow runs the same script.

param(
    [string]$Version,
    [string[]]$Arch = @('x64', 'arm64'),
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$artifacts = Join-Path $root 'artifacts'

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
    $Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }
Write-Output "Building Misty Step $Version for $($Arch -join ', ')"

function Find-Iscc {
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    throw 'Inno Setup 6 (ISCC.exe) was not found. Install it: winget install JRSoftware.InnoSetup'
}
$iscc = Find-Iscc

if (Test-Path $artifacts) { Remove-Item -Recurse -Force $artifacts }
New-Item -ItemType Directory -Force $artifacts | Out-Null

if (-not $SkipTests) {
    dotnet test --solution MistyStep.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

$readme = @"
Misty Step $Version (portable)

Run MistyStep.exe. Settings and logs are kept in this folder, because the file
MistyStep.portable is here. Delete that file to keep them in your user profile instead.

Windows may show "Windows protected your PC" because the app is not code-signed:
choose More info, then Run anyway. Windows Firewall may also ask whether to allow
Misty Step: allow it, then use Pair a device > Fix firewall to narrow the rules.

If you move this folder, run Fix firewall again and re-tick Start with Windows.
Before deleting the folder, untick Start with Windows in the tray menu.

Built entirely with AI, for the author's personal use. Provided as is, with no
warranty and no support. See LICENSE.txt and THIRD-PARTY-NOTICES.txt.
Project page: https://github.com/paulsonkantony/MistyStep
"@

foreach ($a in $Arch) {
    $rid = "win-$a"
    $publish = Join-Path $artifacts "publish\$rid"
    dotnet publish src/MistyStep.App -c Release -r $rid --self-contained `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version -o $publish
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid." }
    $exe = Join-Path $publish 'MistyStep.exe'

    & $iscc /Q "/DAppVersion=$Version" "/DArch=$a" "/DSourceExe=$exe" "/DOutputDir=$artifacts" (Join-Path $root 'installer\mistystep.iss')
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed for $a." }

    $stage = Join-Path $artifacts "stage\$a"
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item $exe $stage
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $stage 'THIRD-PARTY-NOTICES.txt')
    Set-Content -Path (Join-Path $stage 'MistyStep.portable') -Value 'This file makes Misty Step keep its settings and logs in this folder.' -Encoding ascii
    Set-Content -Path (Join-Path $stage 'README.txt') -Value $readme -Encoding utf8
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $artifacts "MistyStep-$Version-$a-portable.zip")
}

Remove-Item -Recurse -Force (Join-Path $artifacts 'publish'), (Join-Path $artifacts 'stage')

$sums = Get-ChildItem $artifacts -File | Where-Object { $_.Extension -in '.exe', '.zip' } | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
Set-Content -Path (Join-Path $artifacts 'SHA256SUMS.txt') -Value $sums -Encoding ascii

Get-ChildItem $artifacts -File | ForEach-Object { '{0,-45} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
