namespace ForzaNext.Core.Virtual;

public sealed record VJoyInspection(bool Installed, bool Enabled, bool VersionMatches, VjdStatus Status,
    int Buttons, int ContinuousPovs, string[] Axes, bool HasFfb, string? Error, ushort DllVersion = 0, ushort DriverVersion = 0)
{
    public static readonly string[] RecommendedAxes = { "X", "Y", "Z", "RX", "RY", "RZ", "SL0", "SL1" };
    public bool ConfigurationReady => Error == null && Installed && Enabled && VersionMatches && Buttons >= 128 && ContinuousPovs >= 1
        && HasFfb && RecommendedAxes.All(axis => Axes.Contains(axis, StringComparer.OrdinalIgnoreCase));
    public bool Busy => Status is VjdStatus.VJD_STAT_BUSY or VjdStatus.VJD_STAT_OWN;
    public string Summary => Error ?? (!Installed ? "Driver ainda não instalado."
        : !Enabled ? "Driver instalado, mas desativado ou aguardando reinicialização."
        : DllVersion != 0 && DriverVersion == 0 ? "Driver instalado; versão indisponível enquanto o dispositivo está desativado ou inacessível. Prepare o volante virtual no app."
        : !VersionMatches ? "A DLL e o driver estão com versões diferentes. Reinicie o Windows."
        : Busy ? "Volante virtual ocupado por outra sessão."
        : ConfigurationReady ? "Pronto: 128 botões, 8 eixos, D-pad e FFB."
        : $"Configuração pendente: {Buttons}/128 botões; {Axes.Length}/8 eixos; {ContinuousPovs} POV contínuo.");
}
