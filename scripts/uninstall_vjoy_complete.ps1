#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess = $true)]
param()
$ErrorActionPreference = 'Stop'

# OEM numbers and root instance IDs are machine-specific. Use the official uninstaller.
$uninstaller = Join-Path $env:ProgramFiles 'vJoy\unins000.exe'
if (-not (Test-Path -LiteralPath $uninstaller)) {
    throw 'Desinstalador oficial não encontrado. Use Aplicativos instalados ou o instalador oficial do vJoy. Nenhum dispositivo foi removido.'
}
if ($PSCmdlet.ShouldProcess($uninstaller, 'Executar desinstalador oficial do vJoy')) {
    $process = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -notin @(0, 3010)) { throw "Desinstalador falhou: $($process.ExitCode)" }
    Write-Host 'Desinstalador concluído. Reinicie o Windows se solicitado. Nenhum arquivo do projeto foi removido.'
}
