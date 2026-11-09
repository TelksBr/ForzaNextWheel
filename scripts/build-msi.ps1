[CmdletBinding()]
param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version

if (-not $OutputPath) {
    $OutputPath = Join-Path $root "artifacts\release\$version"
}
$app = Join-Path $OutputPath 'app'
$null = New-Item -ItemType Directory -Force -Path $app

Write-Output "==> Garantindo dependência do vJoy..."
if (-not (Test-Path -LiteralPath (Join-Path $root 'tools\vJoySetup-2.2.2.0.exe'))) {
    & (Join-Path $PSScriptRoot 'fetch-vjoy.ps1')
}

Write-Output "==> Restaurando ferramentas e extensões WiX v5..."
& dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore falhou.' }
& dotnet wix extension add WixToolset.UI.wixext/5.0.2
& dotnet wix extension add WixToolset.Util.wixext/5.0.2

Write-Output "==> Publicando ForzaNext.UI (win-x64 self-contained)..."
& dotnet publish (Join-Path $root 'src\ForzaNext.UI') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $app
if ($LASTEXITCODE -ne 0) { throw 'Publish do ForzaNext.UI falhou.' }

Write-Output "==> Publicando ForzaNext.Cli..."
& dotnet publish (Join-Path $root 'src\ForzaNext.Cli') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $app
if ($LASTEXITCODE -ne 0) { throw 'Publish do ForzaNext.Cli falhou.' }

Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $app -Force
if (-not (Test-Path -LiteralPath (Join-Path $app 'configuration.json'))) {
    [IO.File]::WriteAllText((Join-Path $app 'configuration.json'), '[]')
}

$msi = Join-Path $OutputPath "ForzaNextWheel-$version-Setup.msi"
$sourceDir = Join-Path $root 'installer'
$wxs = Join-Path $sourceDir 'ForzaNext.wxs'

Write-Output "==> Compilando instalador MSI com WiX v5..."
& dotnet wix build $wxs -arch x64 -ext WixToolset.UI.wixext -d "Version=$version" -d "PublishDir=$app" -d "SourceDir=$sourceDir" -o $msi
if ($LASTEXITCODE -ne 0) { throw 'Compilação do MSI com WiX falhou.' }

Write-Output "MSI gerado com sucesso: $msi"

