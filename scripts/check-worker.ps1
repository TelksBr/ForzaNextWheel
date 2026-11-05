[CmdletBinding()]
param(
    [string]$UiExecutable = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\publish\ForzaNext.UI.exe'),
    [string]$ProfilePath,
    [switch]$Probe,
    [switch]$VerifyFfb,
    [switch]$Capture,
    [int]$SampleCount = 3
)
$ErrorActionPreference = 'Stop'
if ($Probe -and $VerifyFfb) { throw 'Use Probe ou VerifyFfb, separadamente.' }
if ($Capture -and ($Probe -or $VerifyFfb -or -not $ProfilePath)) { throw 'Capture exige apenas ProfilePath, sem Probe/VerifyFfb.' }
if ($SampleCount -lt 3 -or $SampleCount -gt 300) { throw 'SampleCount deve estar entre 3 e 300.' }
if ($VerifyFfb -and -not $ProfilePath) { throw 'VerifyFfb exige um perfil físico explícito.' }
$pipeName = 'ForzaNext-' + [Guid]::NewGuid().ToString('N')
$options = [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly
$server = [IO.Pipes.NamedPipeServerStream]::new($pipeName, [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte, $options)
$resolvedExecutable = (Resolve-Path -LiteralPath $UiExecutable).Path
$managedAssembly = [IO.Path]::GetExtension($resolvedExecutable) -eq '.dll'
$info = [Diagnostics.ProcessStartInfo]::new($(if ($managedAssembly) { (Get-Command dotnet).Source } else { $resolvedExecutable }))
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
if ($managedAssembly) { $info.ArgumentList.Add($resolvedExecutable) }
$info.ArgumentList.Add('--device-worker')
$info.ArgumentList.Add($pipeName)
$info.ArgumentList.Add($PID.ToString())
$workerProcess = [Diagnostics.Process]::Start($info)
try {
    $null = $server.WaitForConnectionAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
    $writer = [IO.StreamWriter]::new($server, [Text.UTF8Encoding]::new($false), 4096, $true)
    $reader = [IO.StreamReader]::new($server, [Text.Encoding]::UTF8, $false, 4096, $true)
    $profiles = @()
    if ($ProfilePath) { $profiles = @(Get-Content -LiteralPath $ProfilePath -Raw | ConvertFrom-Json) }
    # Default: input-only. Explicit FFB verification uses gain zero and never sends a motor-test command.
    $configuration = if ($VerifyFfb) { @{ MasterGain = 0.0 } } else { @{} }
    $request = if ($Capture) { @{ Kind = 'capture'; Value = @{ InstanceGuid = $profiles[0].InstanceGuid; ProductGuid = $profiles[0].ProductGuid } } | ConvertTo-Json -Compress }
        elseif ($Probe) { @{ Kind = 'probe'; Value = $true } | ConvertTo-Json -Compress }
        else { @{ Kind = 'start'; Value = @{ Profiles = $profiles; Configuration = $configuration; EnableFfb = [bool]$VerifyFfb } } | ConvertTo-Json -Depth 30 -Compress }
    $writer.WriteLine($request)
    $writer.Flush()
    $ready = $false
    $frames = 0
    $expectedError = $false
    $probeCompleted = $false
    $stage = ''
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt 15 -and $frames -lt $SampleCount) {
        $line = $reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
        if ($null -eq $line) { throw 'Worker desconectou antes de concluir o teste.' }
        $message = $line | ConvertFrom-Json
        if ($Capture -and $message.Kind -eq 'input-sample') {
            if (@($message.Value.Axes).Count -ne 8) { throw 'Amostra de calibração inválida.' }
            $ready = $true; $frames++; Write-Output "INPUT: $($message.Value.Sequence) · $($message.Value.Name)"; continue
        }
        if ($Probe -and $message.Kind -eq 'probe-result') {
            $probeCompleted = $true
            Write-Output "PROBE: $($message.Value.Summary)"
            break
        }
        if ($message.Kind -eq 'ready') {
            if ($VerifyFfb -and -not $message.Value.HasFfb) { throw 'A sessão iniciou sem FFB: ' + $message.Value.Telemetry.Status }
            if (-not $VerifyFfb -and $message.Value.HasFfb) { throw 'O teste de entradas não pode habilitar FFB.' }
            $ready = $true
            Write-Output $(if ($VerifyFfb) { 'READY: FFB inicializado; ganho zero, sem teste de força.' } else { 'READY: somente entradas, FFB desabilitado.' })
        }
        if ($message.Kind -eq 'error') {
            if (-not $ProfilePath -and $message.Value -like '*Mapeie pelo menos*') { $expectedError = $true; break }
            throw $message.Value
        }
        if ($message.Kind -eq 'telemetry') {
            if ($message.Value.Sequence -gt 0) {
                if ($VerifyFfb -and $message.Value.Force -ne 0) { throw 'A verificação com ganho zero reportou força diferente de zero.' }
                $frames++
                Write-Output "FRAME: $($message.Value.Sequence) · $($message.Value.FrequencyHz) Hz · máximo $($message.Value.MaxIntervalMs) ms · força $($message.Value.Force)"
            } elseif ($message.Value.Status -ne $stage) {
                $stage = $message.Value.Status
                Write-Output "STAGE: $stage"
            }
        }
    }
    if ($Probe -and -not $probeCompleted) { throw 'Verificação do driver não concluiu no prazo.' }
    if (-not $Probe -and -not $expectedError -and (-not $ready -or $frames -lt $SampleCount)) { throw 'Teste de entradas não concluiu no prazo.' }
    if (-not $Probe -and -not $expectedError) {
        $writer.WriteLine('{"Kind":"stop","Value":true}')
        $writer.Flush()
    }
    $null = $workerProcess.WaitForExitAsync().WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult()
    Write-Output "STOP: exit=$($workerProcess.ExitCode)"
    if ($expectedError) { Write-Output 'IPC sem hardware: inicialização recusada e processo encerrado corretamente.' }
}
finally {
    $server.Dispose()
    if (-not $workerProcess.HasExited) { $workerProcess.Kill($true) }
    $workerProcess.Dispose()
}
