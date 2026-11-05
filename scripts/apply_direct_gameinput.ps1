#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateRange(180, 2520)][int]$MaxWheelAngle = 900,
    [ValidateRange(0, 7)][int]$WheelAxis = 0,
    [ValidateRange(0, 7)][int]$ThrottleAxis = 5,
    [ValidateRange(0, 7)][int]$BrakeAxis = 4,
    [ValidateRange(0, 7)][int]$ClutchAxis = 3,
    [ValidateRange(-1, 7)][int]$HandbrakeAxis = -1
)
$ErrorActionPreference = 'Stop'
$axes = @($WheelAxis, $ThrottleAxis, $BrakeAxis, $ClutchAxis)
if ($HandbrakeAxis -ge 0) { $axes += $HandbrakeAxis }
if (@($axes | Select-Object -Unique).Count -ne $axes.Count) { throw 'As funções devem usar eixos distintos.' }
$base = 'HKLM:\SYSTEM\CurrentControlSet\Control\GameInput\Devices'
$device = Join-Path $base '12090D4000010004'
$shifter = Join-Path $base '1209C001'
if (-not $PSCmdlet.ShouldProcess($device, 'Registrar perfil nativo ODrive e desmutar dispositivos')) { return }
$backupDirectory = Join-Path $env:LOCALAPPDATA 'ForzaNextWheel'
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
foreach ($key in @($device, $shifter)) {
    if (Test-Path -LiteralPath $key) {
        $backup = Join-Path $backupDirectory ("GameInput-" + [Guid]::NewGuid().ToString('N') + '.reg')
        & reg.exe export ($key -replace '^HKLM:', 'HKLM') $backup /y
        if ($LASTEXITCODE -ne 0) { throw 'Backup do registro falhou. Alteração cancelada.' }
    }
}
New-Item -Path $device -Force | Out-Null
foreach ($key in @($device, $shifter)) {
    if (Test-Path -LiteralPath $key) {
        $properties = Get-ItemProperty -LiteralPath $key
        if ($properties.PSObject.Properties.Name -contains 'Disabled') { Remove-ItemProperty -LiteralPath $key -Name 'Disabled' }
    }
}
Set-ItemProperty -LiteralPath $device -Name 'Description' -Value 'ODrive Direct Drive Wheel' -Type String
$rw = Join-Path $device 'RacingWheel'
New-Item -Path $rw -Force | Out-Null
Set-ItemProperty -LiteralPath $rw -Name 'MaxWheelAngle' -Value $MaxWheelAngle -Type DWord
$mapping = @{ Wheel = $WheelAxis; Throttle = $ThrottleAxis; Brake = $BrakeAxis; Clutch = $ClutchAxis }
if ($HandbrakeAxis -ge 0) { $mapping.Handbrake = $HandbrakeAxis }
elseif (Test-Path -LiteralPath (Join-Path $rw 'Handbrake')) { Remove-Item -LiteralPath (Join-Path $rw 'Handbrake') -Recurse }
foreach ($entry in $mapping.GetEnumerator()) {
    $path = Join-Path $rw $entry.Key
    New-Item -Path $path -Force | Out-Null
    Set-ItemProperty -LiteralPath $path -Name 'AxisIndex' -Value $entry.Value -Type DWord
    Set-ItemProperty -LiteralPath $path -Name 'Invert' -Value 0 -Type DWord
}
Write-Host "Perfil nativo aplicado. Backups em $backupDirectory. Confira os índices GameInput do seu firmware."
