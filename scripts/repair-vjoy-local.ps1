[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = 'C:\forza-wheel-next'
$report = Join-Path $root 'artifacts\vjoy-repair'
$null = New-Item -ItemType Directory -Force -Path $report
$statusPath = Join-Path $report 'status.json'
function Write-State([string]$stage, [string]$message) {
    @{ Stage=$stage; Message=$message; Time=(Get-Date).ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8
}
function Run-Installer([string]$file, [string]$arguments, [int]$seconds) {
    $process = Start-Process -FilePath $file -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($seconds * 1000)) {
        throw "Operação ainda em andamento após $seconds segundos (PID $($process.Id)); não foi interrompida. Não execute outro instalador."
    }
    return $process.ExitCode
}
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'É necessário executar como administrador.' }
    Start-Transcript -LiteralPath (Join-Path $report 'repair.log') -Append | Out-Null
    Write-State 'Backup' 'Verificando instalador e salvando configuração anterior.'
    $installer = Join-Path $root 'tools\vJoySetup-2.2.2.0.exe'
    if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne 'EF569A3105CD301B89580F18F60C66B339E95296ACF2C0DFCAF4B4BBF8AB68FE') { throw 'Hash inesperado do instalador.' }
    if ((Get-AuthenticodeSignature -LiteralPath $installer).Status -ne 'Valid') { throw 'Assinatura inválida do instalador.' }
    if (Get-Process -Name ForzaNext.UI,vJoyInstall,vJoySetup-2.2.2.0 -ErrorAction SilentlyContinue) { throw 'Feche o ForzaNext e aguarde qualquer instalação anterior terminar.' }
    if (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\vjoy') {
        & reg.exe export 'HKLM\SYSTEM\CurrentControlSet\Services\vjoy' (Join-Path $report 'service-before.reg') /y
        if ($LASTEXITCODE -ne 0) { throw 'Backup do registro falhou.' }
    }
    Get-PnpDevice | Where-Object FriendlyName -Match 'vJoy' | Select-Object Status,Class,FriendlyName,InstanceId | ConvertTo-Json | Set-Content (Join-Path $report 'devices-before.json')
    if (Test-Path 'C:\Program Files\vJoy\vJoyInstall.log') { Copy-Item -LiteralPath 'C:\Program Files\vJoy\vJoyInstall.log' -Destination (Join-Path $report 'previous-install.log') }
    Write-State 'Uninstall' 'Removendo a instalação registrada do vJoy.'
    $entries = @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object DisplayName -Match '^vJoy')
    foreach ($entry in $entries) {
        $exe = $entry.UninstallString.Trim('"')
        if ($exe -notmatch '^C:\\Program Files( \(x86\))?\\vJoy\\unins\d+\.exe$' -or -not (Test-Path -LiteralPath $exe)) { throw "Desinstalador não reconhecido: $exe" }
        $code = Run-Installer $exe ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + (Join-Path $report 'uninstall.log') + '"') 180
        Write-Output "Uninstaller exit: $code"
        if ($code -notin @(0,3010)) { throw "Desinstalação falhou: $code" }
    }
    Write-State 'DriverRemoval' 'Removendo dispositivos e pacotes antigos identificados como vJoy.'
    foreach ($device in @(Get-PnpDevice | Where-Object { $_.FriendlyName -eq 'vJoy Device' })) {
        $service = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName DEVPKEY_Device_Service -ErrorAction SilentlyContinue).Data
        if ($service -eq 'vjoy') { & pnputil.exe /remove-device $device.InstanceId /subtree }
    }
    foreach ($driver in @(Get-WindowsDriver -Online | Where-Object { [IO.Path]::GetFileName($_.OriginalFileName) -ieq 'vjoy.inf' })) {
        if ($driver.Driver -notmatch '^oem\d+\.inf$') { throw 'Identificador de pacote inesperado.' }
        & pnputil.exe /delete-driver $driver.Driver /uninstall /force
        if ($LASTEXITCODE -notin @(0,3010)) { throw "Não foi possível remover $($driver.Driver)." }
    }
    $remaining = @(Get-WindowsDriver -Online | Where-Object { [IO.Path]::GetFileName($_.OriginalFileName) -ieq 'vjoy.inf' })
    if ($remaining.Count) { throw 'O pacote vJoy continua presente. Pode ser necessário reiniciar o Windows.' }
    foreach ($ghost in @(Get-PnpDevice | Where-Object { $_.FriendlyName -eq 'vJoy Driver' -and $_.InstanceId -like '{D6E55CA0-1A2E-4234-AAF3-3852170B492F}\VJOYRAWPDO\*' })) {
        & pnputil.exe /remove-device $ghost.InstanceId
    }
    $service = Get-CimInstance Win32_SystemDriver -Filter "Name='vjoy'"
    if ($service -and $service.State -eq 'Stopped') {
        & sc.exe delete vjoy
        if ($LASTEXITCODE -notin @(0,1060)) { throw 'Não foi possível remover o registro do serviço antigo.' }
        $service = $null
    }
    & sc.exe query vjoy
    if ($LASTEXITCODE -ne 1060) {
        Write-State 'RestartRequired' 'Pacote removido, mas o serviço do driver continua carregado. Reinicie o Windows antes de reinstalar.'
        return
    }
    $installDirectory = 'C:\Program Files\vJoy'
    if (Test-Path -LiteralPath $installDirectory) {
        $resolved = (Resolve-Path -LiteralPath $installDirectory).Path.TrimEnd('\')
        if ($resolved -ine 'C:\Program Files\vJoy' -or ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Diretório de limpeza inesperado.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    Write-State 'Installing' 'Instalando novamente o pacote vJoy 2.2.2.0 assinado.'
    $code = Run-Installer $installer ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010 /SP- /LOG="' + (Join-Path $report 'install.log') + '"') 300
    Write-Output "Installer exit: $code"
    if ($code -eq 3010) { Write-State 'RestartRequired' 'Instalação concluída; o Windows exige reinicialização.'; return }
    if ($code -ne 0) { throw "Instalação falhou: $code" }
    Write-State 'Configuring' 'Configurando o vJoy 1 para o ForzaNext.'
    $config = 'C:\Program Files\vJoy\x64\vJoyConfig.exe'
    $code = Run-Installer $config '1 -f -a X Y Z RX RY RZ SL0 SL1 -b 128 -p 1 -e all' 45
    if ($code -ne 0) { throw "Configuração falhou: $code" }
    Get-PnpDevice | Where-Object FriendlyName -Match 'vJoy' | Select-Object Status,Class,FriendlyName,InstanceId | ConvertTo-Json | Set-Content (Join-Path $report 'devices-after.json')
    Write-State 'Complete' 'vJoy removido e reinstalado; configuração do dispositivo 1 aplicada. Falta verificar as capacidades na aplicação.'
}
catch { Write-State 'Failed' $_.Exception.Message; Write-Output $_.Exception.ToString() }
finally { try { Stop-Transcript | Out-Null } catch {} }
