[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
        $tokens = $null
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors) | Out-Null
        if ($errors.Count -gt 0) { throw ($errors | Out-String) }
    }
    & dotnet restore ForzaNextWheel.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore falhou.' }
    & dotnet build ForzaNextWheel.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build falhou.' }
    & dotnet test ForzaNextWheel.sln -c Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Testes falharam.' }
    & dotnet run --project src/ForzaNext.Cli -c Release --no-build -- validate configuration.json
    if ($LASTEXITCODE -ne 0) { throw 'Perfil inicial inválido.' }
}
finally { Pop-Location }
