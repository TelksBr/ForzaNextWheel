using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ForzaNext.Core;
using ForzaNext.Core.Config;
using ForzaNext.Core.FFB;
using ForzaNext.Core.Virtual;

namespace ForzaNext.Cli;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private static int Main(string[] args)
    {
        // Elevated lifecycle guardian invoked by VJoyDeviceLease
        if (args.Length == 3 && args[0] == "--vjoy-lifecycle" &&
            args[1].StartsWith(VJoyDeviceLease.PipePrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(args[1][VJoyDeviceLease.PipePrefix.Length..], "N", out _) &&
            int.TryParse(args[2], out int lifecycleParent))
        {
            int result = VJoyDeviceLease.RunGuardianAsync(args[1], lifecycleParent).GetAwaiter().GetResult();
            Environment.Exit(result);
            return result;
        }

        // Auxiliary worker process invoked by DeviceWorkerProcess / VJoySetupService.InspectAsync
        if (args.Length == 3 && args[0] == "--device-worker" &&
            args[1].StartsWith("ForzaNext-", StringComparison.Ordinal) &&
            int.TryParse(args[2], out int workerParent))
        {
            int result = RunDeviceWorkerAsync(args[1], workerParent).GetAwaiter().GetResult();
            Environment.Exit(result);
            return result;
        }

        if (args.Length >= 1 && args[0] == "inspect")
        {
            Console.WriteLine("=== vJoy & DirectInput System Diagnostic ===");
            var insp = VirtualWheelDriver.Inspect();
            Console.WriteLine($"VirtualWheelDriver.Inspect():");
            Console.WriteLine($" - Installed: {insp.Installed}");
            Console.WriteLine($" - Enabled: {insp.Enabled}");
            Console.WriteLine($" - VersionMatches: {insp.VersionMatches} (DLL: 0x{insp.DllVersion:X}, Driver: 0x{insp.DriverVersion:X})");
            Console.WriteLine($" - Status: {insp.Status}");
            Console.WriteLine($" - Buttons: {insp.Buttons}");
            Console.WriteLine($" - ContinuousPovs: {insp.ContinuousPovs}");
            Console.WriteLine($" - Axes ({insp.Axes.Length}): {string.Join(", ", insp.Axes)}");
            Console.WriteLine($" - HasFfb: {insp.HasFfb}");
            Console.WriteLine($" - ConfigurationReady: {insp.ConfigurationReady}");
            Console.WriteLine($" - Error: {insp.Error}");

            Console.WriteLine("\n--- DirectInput Devices ---");
            using var dinput = new SharpDX.DirectInput.DirectInput();
            SharpDX.DirectInput.DeviceInstance? vjoyDev = null;
            foreach (var dev in dinput.GetDevices())
            {
                if (dev.Type == SharpDX.DirectInput.DeviceType.Mouse || dev.Type == SharpDX.DirectInput.DeviceType.Keyboard) continue;
                Console.WriteLine($"Device: {dev.InstanceName} ({dev.ProductName}) - Type: {dev.Type}");
                Console.WriteLine($" - InstanceGuid: {dev.InstanceGuid}");
                Console.WriteLine($" - ProductGuid:  {dev.ProductGuid}");
                if (dev.InstanceName.Contains("vJoy", StringComparison.OrdinalIgnoreCase)) vjoyDev = dev;
                try
                {
                    using var joy = new SharpDX.DirectInput.Joystick(dinput, dev.InstanceGuid);
                    var caps = joy.Capabilities;
                    bool hasFfb = (caps.Flags & SharpDX.DirectInput.DeviceFlags.ForceFeedback) != 0;
                    Console.WriteLine($" - Axes: {caps.AxeCount}, Buttons: {caps.ButtonCount}, POVs: {caps.PovCount}");
                    Console.WriteLine($" - Flags: {caps.Flags} (HasFFB: {hasFfb})");
                    var effects = joy.GetEffects();
                    Console.WriteLine($" - Supported Effects ({effects.Count}): {string.Join(", ", effects.Select(e => e.Name))}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" - Error querying joystick: {ex.Message}");
                }
            }

            if (vjoyDev != null)
            {
                Console.WriteLine("\n--- Testing DirectInput -> vJoy FFB Loopback ---");
                using var hostWin = new ForzaNext.Core.Devices.NativeHiddenWindow("Test vJoy FFB");
                var vjoyDriver = new VirtualWheelDriver();
                int packetCount = 0;
                vjoyDriver.FfbPacketReceived += (data, type) =>
                {
                    packetCount++;
                    Console.WriteLine($"[VJOY-RECV-CALLBACK] Type={type} Data={data}");
                };
                if (!vjoyDriver.Initialize())
                {
                    Console.WriteLine($"Failed to acquire vJoy driver: {vjoyDriver.LastError}");
                }
                else
                {
                    Console.WriteLine("vJoyDriver acquired successfully. HasFfb: " + vjoyDriver.HasFfb);
                    try
                    {
                        using var vjoyJoy = new SharpDX.DirectInput.Joystick(dinput, vjoyDev.InstanceGuid);
                        vjoyJoy.SetCooperativeLevel(hostWin.Handle, SharpDX.DirectInput.CooperativeLevel.Background | SharpDX.DirectInput.CooperativeLevel.Exclusive);
                        vjoyJoy.Acquire();
                        Console.WriteLine("Acquired vJoy DirectInput handle in Exclusive mode.");

                        var effectParams = new SharpDX.DirectInput.EffectParameters
                        {
                            Flags = SharpDX.DirectInput.EffectFlags.Cartesian | SharpDX.DirectInput.EffectFlags.ObjectOffsets,
                            Duration = 500000,
                            SamplePeriod = 0,
                            Gain = 10000,
                            TriggerButton = -1,
                            TriggerRepeatInterval = 0,
                            Axes = new[] { 0 },
                            Directions = new[] { 0 },
                            StartDelay = 0
                        };
                        var cf = new SharpDX.DirectInput.ConstantForce { Magnitude = 5000 };
                        effectParams.Parameters = cf;

                        Console.WriteLine("Creating Spring effect on vJoy...");
                        var springParams = new SharpDX.DirectInput.EffectParameters
                        {
                            Flags = SharpDX.DirectInput.EffectFlags.Cartesian | SharpDX.DirectInput.EffectFlags.ObjectOffsets,
                            Duration = -1,
                            SamplePeriod = 0,
                            Gain = 10000,
                            TriggerButton = -1,
                            TriggerRepeatInterval = 0,
                            Axes = new[] { 0 },
                            Directions = new[] { 0 },
                            StartDelay = 0,
                            Parameters = new SharpDX.DirectInput.ConditionSet
                            {
                                Conditions = new[]
                                {
                                    new SharpDX.DirectInput.Condition
                                    {
                                        Offset = 0,
                                        PositiveCoefficient = 2000,
                                        NegativeCoefficient = 2000,
                                        PositiveSaturation = 10000,
                                        NegativeSaturation = 10000,
                                        DeadBand = 0
                                    }
                                }
                            }
                        };
                        using var springEffect = new SharpDX.DirectInput.Effect(vjoyJoy, SharpDX.DirectInput.EffectGuid.Spring, springParams);
                        Console.WriteLine("Starting Spring effect on vJoy...");
                        springEffect.Start(1);
                        Thread.Sleep(300);
                        Console.WriteLine($"Total packets after Spring: {packetCount}");
                        springEffect.Stop();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"DirectInput FFB test error: {ex}");
                    }
                    finally
                    {
                        vjoyDriver.Dispose();
                    }
                }
            }
            return 0;
        }

        if (args.Length >= 1 && args[0] == "run-debug")
        {
            string configPath = args.Length > 1 ? args[1] : (File.Exists("configuration.json") ? "configuration.json" : ForzaNext.Core.AppPaths.Configuration);
            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"Arquivo de configuração não encontrado: {configPath}");
                return 1;
            }
            try
            {
                Console.WriteLine("=== ForzaNext FFB Live Debug Session ===");
                Console.WriteLine("Preparando e ativando o dispositivo vJoy 1 via gerenciador com elevação...");
                var vjoyLifetime = new VJoyLifetime();
                vjoyLifetime.EnsureAsync().GetAwaiter().GetResult();
                Console.WriteLine("[vJoy] Volante virtual 1 preparado e ativo com sucesso.");

                Console.WriteLine($"Carregando perfil de: {configPath}");
                var profiles = ProfileManager.LoadFromFile(configPath);
                Console.WriteLine($"Dispositivos configurados: {profiles.Count}");
                foreach (var p in profiles) Console.WriteLine($" - {p.InstanceName} (FFB: {p.FFBParameters != null})");

                FfbDebugLogger.EnableConsoleOutput = true;
                Console.WriteLine($"Gravando trace detalhado em: {FfbDebugLogger.LogPath}");

                var ffbTarget = profiles.FirstOrDefault(p => p.FFBParameters != null);
                var ffbConfig = DirectInputFFBBridge.FromProfile(ffbTarget?.FFBParameters);

                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    if (ForzaNext.Core.Devices.HidHideService.IsInstalled)
                    {
                        ForzaNext.Core.Devices.HidHideService.UncloakSync(profiles);
                    }
                };

                using var hostWindow = new ForzaNext.Core.Devices.NativeHiddenWindow("ForzaNext CLI Session");
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("\nEncerrando sessão de debug e liberando dispositivos...");
                    cts.Cancel();
                };

                try
                {
                    if (ForzaNext.Core.Devices.HidHideService.IsInstalled)
                    {
                        Console.WriteLine("[HidHide] Ocultando dispositivos físicos para que o Forza veja apenas o vJoy 1...");
                        bool hidSuccess = ForzaNext.Core.Devices.HidHideService.ConfigureCloakingAsync(profiles, cts.Token).GetAwaiter().GetResult();
                        Console.WriteLine(hidSuccess
                            ? "[HidHide] Dispositivos físicos ocultados com sucesso."
                            : "[HidHide] Falha ao configurar HidHide; prosseguindo.");
                    }

                    Console.WriteLine("\nIniciando sessão do volante... Abra o Forza e pilote!");
                    Console.WriteLine("Pressione Ctrl+C para encerrar e liberar os dispositivos.\n");

                    var session = new ForzaNext.Core.LocalWheelSession(profiles, hostWindow.Handle, ffbConfig, enableFfb: true);
                    session.Started.Wait();
                    Console.WriteLine($"[STATUS] Sessão iniciada com sucesso. HasFFB: {session.HasFfb}");

                    while (!cts.IsCancellationRequested && !session.Completion.IsCompleted)
                    {
                        Thread.Sleep(500);
                    }

                    session.StopAsync().Wait(TimeSpan.FromSeconds(3));
                    vjoyLifetime.StopAsync().GetAwaiter().GetResult();
                    Console.WriteLine("Sessão finalizada limpa.");
                }
                finally
                {
                    if (ForzaNext.Core.Devices.HidHideService.IsInstalled)
                    {
                        Console.WriteLine("[HidHide] Restaurando visibilidade dos dispositivos físicos...");
                        ForzaNext.Core.Devices.HidHideService.UncloakSync(profiles);
                        Console.WriteLine("[HidHide] Dispositivos liberados no Windows.");
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Erro na sessão de debug: {ex}");
                return 1;
            }
        }

        if (args.Length >= 1 && (args[0] == "uncloak" || args[0] == "restore-devices"))
        {
            if (ForzaNext.Core.Devices.HidHideService.IsInstalled)
            {
                Console.WriteLine("[HidHide] Restaurando visibilidade dos dispositivos físicos...");
                string cfg = args.Length > 1 ? args[1] : "configuration.json";
                var profiles = File.Exists(cfg) ? ProfileManager.LoadFromFile(cfg) : null;
                bool ok = ForzaNext.Core.Devices.HidHideService.UncloakSync(profiles);
                Console.WriteLine(ok ? "[HidHide] Todos os dispositivos físicos foram liberados com sucesso!" : "[HidHide] Falha ao executar Uncloak.");
                return ok ? 0 : 1;
            }
            Console.WriteLine("[HidHide] HidHide não está instalado.");
            return 0;
        }

        if (args.Length == 1 && args[0] == "probe-vjoy")
        {
            try { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(VirtualWheelDriver.Inspect())); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }

        if (args.Length == 1 && args[0] == "verify-vjoy-abi")
        {
            try { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(VirtualWheelDriver.VerifyFfbAbi())); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }

        if (args.Length is 2 or 3 && args[0] == "benchmark-loop")
        {
            try
            {
                int seconds = int.Parse(args[1]);
                if (args.Length == 3 && args[2] != "--compare") throw new ArgumentException("Opção inválida.");
                if (args.Length == 3) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(ForzaNext.Core.Timing.LoopBenchmark.Run(seconds, legacy: true)));
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(ForzaNext.Core.Timing.LoopBenchmark.Run(seconds)));
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }

        if (args.Length != 2 || args[0] != "validate")
        {
            Console.Error.WriteLine("Uso: ForzaNext.Cli validate <configuration.json> | benchmark-loop <1..30 segundos> [--compare] | verify-vjoy-abi | probe-vjoy | run-debug [configuration.json]");
            return 2;
        }

        try
        {
            var profiles = ProfileManager.LoadFromFile(args[1]);
            Console.WriteLine($"Perfil válido: {profiles.Count} dispositivos.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static async Task<int> RunDeviceWorkerAsync(string pipeName, int parentId)
    {
        try
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, lifetime.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            var line = await reader.ReadLineAsync(lifetime.Token) ?? throw new IOException("Supervisor desconectado.");
            var message = JsonSerializer.Deserialize<WorkerMessage>(line, WorkerMessage.Json) ?? throw new IOException("Mensagem inválida.");
            if (message.Kind == "probe")
            {
                var result = await Task.Run(VirtualWheelDriver.Inspect);
                await writer.WriteLineAsync(WorkerMessage.Encode("probe-result", result));
                await writer.FlushAsync(lifetime.Token);
                return 0;
            }
            return 0;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("CLI device worker", ex.ToString());
            return 1;
        }
    }
}
