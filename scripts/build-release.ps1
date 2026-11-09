[CmdletBinding()]
param(
    [switch]$SkipExe,
    [string]$Compiler
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$output = Join-Path $root "artifacts\release\$version"
$app = Join-Path $output 'app'
$null = New-Item -ItemType Directory -Force -Path $app

# 1. Build MSI Installer
& (Join-Path $PSScriptRoot 'build-msi.ps1') -OutputPath $output
$msiInstaller = Join-Path $output "ForzaNextWheel-$version-Setup.msi"

# 2. Build Inno Setup EXE if compiler is present and not skipped
$exeInstaller = Join-Path $output "ForzaNextWheel-$version-Setup.exe"
if (-not $SkipExe) {
    if (-not $Compiler) { $Compiler = Join-Path $root 'tools\InnoSetup\tools\ISCC.exe' }
    if (Test-Path -LiteralPath $Compiler) {
        Write-Output "==> Compilando instalador EXE com Inno Setup..."
        & $Compiler "/DAppVersion=$version" "/DPublishDir=$app" "/DOutputPath=$output" (Join-Path $root 'installer\ForzaNext.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Compilação do instalador Inno Setup falhou.' }
    } else {
        Write-Output "==> Inno Setup não encontrado; o instalador MSI será o principal."
    }
}

# 3. Create Portable ZIP
$portable = Join-Path $output "ForzaNextWheel-$version-Portable.zip"
if (Test-Path -LiteralPath $portable) { Remove-Item -LiteralPath $portable }
Write-Output "==> Criando pacote portátil ZIP..."
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $portable -CompressionLevel Optimal

# 4. Create release.json manifest for in-app updater (prioritizing MSI)
$primaryInstaller = if (Test-Path -LiteralPath $msiInstaller) { $msiInstaller } else { $exeInstaller }
$manifest = [ordered]@{
    Product = 'ForzaNextWheel'
    Version = $version
    Package = [IO.Path]::GetFileName($primaryInstaller)
    Sha256  = (Get-FileHash -LiteralPath $primaryInstaller -Algorithm SHA256).Hash
    Size    = (Get-Item -LiteralPath $primaryInstaller).Length
}
[IO.File]::WriteAllText((Join-Path $output 'release.json'), ($manifest | ConvertTo-Json))

# 5. Create checksums.txt (SHA256 for all artifacts)
$checksums = [System.Text.StringBuilder]::new()
Get-ChildItem -Path $output -File | Where-Object { $_.Name -ne 'checksums.txt' } | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    $null = $checksums.AppendLine("$hash  $($_.Name)")
}
[IO.File]::WriteAllText((Join-Path $output 'checksums.txt'), $checksums.ToString())

Write-Output "Release completa pronta em: $output"
