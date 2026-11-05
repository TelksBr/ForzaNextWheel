$asm = [System.Reflection.Assembly]::LoadFile('C:\Program Files\vJoy\x64\vJoyInterfaceWrap.dll')
$vjoy = $asm.GetType('vJoyInterfaceWrap.vJoy')
$m = $vjoy.GetMethod('FfbRegisterGenCB', [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Instance, $null, @([Type]::GetType('vJoyInterfaceWrap.vJoy+WrapFfbCbFunc'), [IntPtr]), $null)
if (-not $m) {
    $m = $vjoy.GetMethods() | Where-Object { $_.Name -eq 'FfbRegisterGenCB' }
}
foreach ($method in $m) {
    Write-Output "METHOD: $($method.ToString())"
    $body = $method.GetMethodBody()
    if ($body) {
        $il = $body.GetILAsByteArray()
        Write-Output "IL: $([BitConverter]::ToString($il))"
    }
}

