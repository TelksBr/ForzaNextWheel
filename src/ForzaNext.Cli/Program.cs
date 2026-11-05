using ForzaNext.Core.Config;

namespace ForzaNext.Cli;
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "verify-vjoy-abi")
        {
            try { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(ForzaNext.Core.Virtual.VirtualWheelDriver.VerifyFfbAbi())); return 0; }
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
            Console.Error.WriteLine("Uso: ForzaNext.Cli validate <configuration.json> | benchmark-loop <1..30 segundos> [--compare] | verify-vjoy-abi");
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
}
