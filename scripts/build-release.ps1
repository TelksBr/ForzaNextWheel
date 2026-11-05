[CmdletBinding()]
param([string]$Compiler)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$output = Join-Path $root "artifacts\release\$version"
$app = Join-Path $output 'app'
$null = New-Item -ItemType Directory -Force -Path $app
if (-not $Compiler) { $Compiler = Join-Path $root 'tools\InnoSetup\tools\ISCC.exe' }
if (-not (Test-Path -LiteralPath $Compiler)) { throw 'Use Inno Setup 6.7.3 ou informe -Compiler. Veja installer/README.md.' }
if (-not (Test-Path -LiteralPath (Join-Path $root 'tools\vJoySetup-2.2.2.0.exe'))) { throw 'Execute scripts/fetch-vjoy.ps1 antes de publicar.' }
& dotnet publish (Join-Path $root 'src\ForzaNext.UI') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $app
if ($LASTEXITCODE -ne 0) { throw 'Publish falhou.' }
[IO.File]::WriteAllText((Join-Path $app 'configuration.json'), '[]')
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $app
& $Compiler "/DAppVersion=$version" "/DPublishDir=$app" "/DOutputPath=$output" (Join-Path $root 'installer\ForzaNext.iss')
if ($LASTEXITCODE -ne 0) { throw 'Compilação do instalador falhou.' }
$installer = Join-Path $output "ForzaNextWheel-$version-Setup.exe"
$manifest = [ordered]@{ Product = 'ForzaNextWheel'; Version = $version; Package = [IO.Path]::GetFileName($installer); Sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash; Size = (Get-Item -LiteralPath $installer).Length }
[IO.File]::WriteAllText((Join-Path $output 'release.json'), ($manifest | ConvertTo-Json))
$portable = Join-Path $output "ForzaNextWheel-$version-Portable.zip"
if (Test-Path -LiteralPath $portable) { Remove-Item -LiteralPath $portable }
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $portable -CompressionLevel Optimal
Write-Output "Release pronta: $output"
