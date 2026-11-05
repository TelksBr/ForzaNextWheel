[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $projectRoot 'tools'
$target = Join-Path $directory 'vJoySetup-2.2.2.0.exe'
$expectedHash = 'EF569A3105CD301B89580F18F60C66B339E95296ACF2C0DFCAF4B4BBF8AB68FE'
if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $expectedHash) {
    Write-Output 'Instalador vJoy 2.2.2.0 verificado.'
    return
}
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$temporary = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    Invoke-WebRequest -Uri 'https://github.com/BrunnerInnovation/vJoy/releases/download/v2.2.2.0/vJoySetup_v2.2.2.0_Win10_Win11.exe' -OutFile $temporary -TimeoutSec 120
    if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $expectedHash) { throw 'SHA-256 do instalador não corresponde à distribuição fixada.' }
    Move-Item -LiteralPath $temporary -Destination $target -Force
    Write-Output 'Instalador vJoy 2.2.2.0 baixado e verificado. Nenhum instalador foi executado.'
}
finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
