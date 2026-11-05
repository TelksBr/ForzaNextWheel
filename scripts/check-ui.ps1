[CmdletBinding()]
param(
    [string]$UiAssembly = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\ForzaNext.UI\bin\Release\net8.0-windows\ForzaNext.UI.dll')
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$outputDirectory = Join-Path $projectRoot 'artifacts\ui-preview'
$null = New-Item -ItemType Directory -Force -Path $outputDirectory
$assembly = (Resolve-Path -LiteralPath $UiAssembly).Path
$reportPath = Join-Path $outputDirectory 'checks.json'
# These entry points bypass profile initialization, elevation and every native device call.
& dotnet $assembly --preview-check $reportPath
if ($LASTEXITCODE -ne 0) { throw 'Verificação das interações da interface falhou.' }
$checks = @(Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json)
if ($checks.Count -ne 6) { throw 'Relatório de verificação incompleto.' }
foreach ($size in @(@(1360, 880), @(1080, 720))) {
    foreach ($page in 0..3) {
        $imagePath = Join-Path $outputDirectory "page-$page-$($size[0])x$($size[1]).png"
        & dotnet $assembly --preview-render $imagePath $page $size[0] $size[1]
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $imagePath)) { throw "Renderização falhou: $imagePath" }
    }
}
foreach ($page in @(-1, -2)) {
    $imagePath = Join-Path $outputDirectory "dialog-$page.png"
    & dotnet $assembly --preview-render $imagePath $page 660 530
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $imagePath)) { throw "Renderização falhou: $imagePath" }
}
Write-Output 'UI: 6 verificações de interação aprovadas e 10 telas renderizadas sem acessar hardware.'
