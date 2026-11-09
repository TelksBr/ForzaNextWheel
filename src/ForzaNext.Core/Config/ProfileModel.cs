using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaNext.Core.Config;

public class ControllerProfile
{
    [JsonPropertyName("ProductGuid")]
    public Guid ProductGuid { get; set; }

    [JsonPropertyName("InstanceGuid")]
    public Guid InstanceGuid { get; set; }

    [JsonPropertyName("InstanceName")]
    public string InstanceName { get; set; } = string.Empty;

    [JsonPropertyName("ProductName")]
    public string ProductName { get; set; } = string.Empty;

    [JsonPropertyName("Axes")]
    public List<AxisMapping>? Axes { get; set; }

    [JsonPropertyName("Buttons")]
    public List<ButtonMapping>? Buttons { get; set; }

    [JsonPropertyName("DPad")]
    public DPadMapping? DPad { get; set; }

    [JsonPropertyName("FFBParameters")]
    public FFBParameters? FFBParameters { get; set; }
}

public class AxisMapping
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty; // "Steering", "Throttle", "Brake", "Clutch", "Handbrake"

    [JsonPropertyName("AxisIndex")]
    public int AxisIndex { get; set; }

    [JsonPropertyName("Inverted")]
    public int Inverted { get; set; }

    [JsonPropertyName("Deadzone")]
    public double Deadzone { get; set; }
    public AxisCalibration? Calibration { get; set; }
}

public sealed class AxisCalibration
{
    public int Minimum { get; set; }
    public int Center { get; set; } = 32768;
    public int Maximum { get; set; } = 65535;
    public void Validate(bool steering)
    {
        if (Minimum is < 0 or > 65535 || Maximum is < 0 or > 65535 || Maximum - Minimum < 1024 ||
            (steering && (Center <= Minimum || Center >= Maximum)))
            throw new InvalidDataException("Calibração inválida: confira limites e centro do eixo.");
    }
    public double Normalize(int raw, bool steering)
    {
        Validate(steering);
        double value = Math.Clamp(raw, Minimum, Maximum);
        return steering ? value <= Center ? (value - Minimum) / (Center - Minimum) * 0.5
            : 0.5 + (value - Center) / (Maximum - Center) * 0.5
            : (value - Minimum) / (Maximum - Minimum);
    }
}

public class ButtonMapping
{
    [JsonPropertyName("Id")]
    public int Id { get; set; } // Virtual Button ID in Forza (0..127)

    [JsonPropertyName("Index")]
    public int Index { get; set; } // Physical button index on controller
}

public class DPadMapping
{
    [JsonPropertyName("Index")]
    public int Index { get; set; }
}

public class FFBParameters
{
    [JsonPropertyName("InvertConstantForce")]
    public bool InvertConstantForce { get; set; } = true;
    [JsonPropertyName("Const")]
    public ConstantForceConfig? Const { get; set; }

    [JsonPropertyName("Sine")]
    public SineForceConfig? Sine { get; set; }

    [JsonPropertyName("Spring")]
    public ConditionConfig? Spring { get; set; }

    [JsonPropertyName("Damper")]
    public ConditionConfig? Damper { get; set; }
}

public class ConstantForceConfig
{
    [JsonPropertyName("Magnitude")]
    public double Magnitude { get; set; } = 1.0;

    [JsonPropertyName("MaximumForce")]
    public double MaximumForce { get; set; } = 1.0;

    [JsonPropertyName("MinimumForce")]
    public double MinimumForce { get; set; } = 0.0;

    [JsonPropertyName("FilterThreshold")]
    public double FilterThreshold { get; set; } = 0.1;

    [JsonPropertyName("MinimumCoefficient")]
    public double MinimumCoefficient { get; set; } = 1.0;
}

public class SineForceConfig
{
    [JsonPropertyName("Magnitude")]
    public double Magnitude { get; set; } = 1.0;

    [JsonPropertyName("Frequency")]
    public double Frequency { get; set; } = 0.375;

    [JsonPropertyName("MaximumForce")]
    public double MaximumForce { get; set; } = 0.5;

    [JsonPropertyName("MinimumForce")]
    public double MinimumForce { get; set; } = 0.0;

    [JsonPropertyName("Phase")]
    public double Phase { get; set; } = 0.375;

    [JsonPropertyName("EngineVibrations")]
    public VibrationConfig? EngineVibrations { get; set; }

    [JsonPropertyName("GearShiftVibrations")]
    public VibrationConfig? GearShiftVibrations { get; set; }
}

public class VibrationConfig
{
    [JsonPropertyName("Frequency")]
    public double Frequency { get; set; }

    [JsonPropertyName("Strength")]
    public double Strength { get; set; }
}

public class ConditionConfig
{
    [JsonPropertyName("Coefficient")]
    public double Coefficient { get; set; }

    [JsonPropertyName("Saturation")]
    public double Saturation { get; set; } = 1.0;
}

public static class ProfileManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static List<ControllerProfile> LoadFromFile(string path)
    {
        var profiles = JsonSerializer.Deserialize<List<ControllerProfile>>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("O perfil deve conter uma lista de dispositivos.");
        Validate(profiles);
        return profiles;
    }

    public static List<ControllerProfile> Snapshot(List<ControllerProfile> profiles)
    {
        Validate(profiles);
        return JsonSerializer.Deserialize<List<ControllerProfile>>(JsonSerializer.Serialize(profiles, JsonOptions), JsonOptions)!;
    }

    public static void Validate(List<ControllerProfile> profiles)
    {
        if (profiles == null) throw new InvalidDataException("Lista de dispositivos ausente.");
        var devices = new HashSet<Guid>();
        var axes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buttons = new HashSet<int>();
        var axisNames = new[] { "Steering", "Throttle", "Brake", "Clutch", "Handbrake", "Slider0", "Slider1" };
        foreach (var p in profiles)
        {
            if (p == null || p.InstanceGuid == Guid.Empty || !devices.Add(p.InstanceGuid))
                throw new InvalidDataException("Dispositivo sem InstanceGuid válido ou duplicado.");
            if (string.IsNullOrWhiteSpace(p.ProductName) || string.IsNullOrWhiteSpace(p.InstanceName))
                throw new InvalidDataException("Nome de dispositivo ausente.");
            foreach (var axis in p.Axes ?? new())
            {
                if (axis == null || !axisNames.Contains(axis.Id) || !axes.Add(axis.Id) || axis.AxisIndex is < 0 or > 7 || axis.Inverted is < 0 or > 1)
                    throw new InvalidDataException($"Mapeamento de eixo inválido/duplicado em {p.InstanceName}.");
                Range(axis.Deadzone, 0, 0.99, "Deadzone");
                axis.Calibration?.Validate(axis.Id == "Steering");
            }
            foreach (var b in p.Buttons ?? new())
                if (b == null || b.Id is < 0 or > 127 || b.Index is < 0 or > 127 || !buttons.Add(b.Id))
                    throw new InvalidDataException($"Botão inválido/duplicado em {p.InstanceName}.");
            if (p.DPad?.Index is < 0 or > 3) throw new InvalidDataException("POV deve estar entre 0 e 3.");
            if (p.FFBParameters is { } f)
            {
                if (f.Const is { } c)
                {
                    Range(c.MaximumForce, 0, 1, "MaximumForce"); Range(c.Magnitude, 0, 2.5, "Magnitude");
                    Range(c.MinimumForce, 0, 0.2, "MinimumForce"); Range(c.FilterThreshold, 0, 1, "FilterThreshold");
                    Range(c.MinimumCoefficient, 0, 1, "MinimumCoefficient");
                }
                foreach (var condition in new[] { f.Spring, f.Damper })
                    if (condition != null) { Range(condition.Coefficient, 0, 1, "Coefficient"); Range(condition.Saturation, 0, 1, "Saturation"); }
                if (f.Sine is { } sine)
                {
                    Range(sine.Magnitude, 0, 2.5, "Sine.Magnitude"); Range(sine.Frequency, 0.01, 10, "Sine.Frequency");
                    Range(sine.MaximumForce, 0, 1, "Sine.MaximumForce"); Range(sine.MinimumForce, 0, 1, "Sine.MinimumForce");
                    Range(sine.Phase, 0, 1, "Sine.Phase");
                    if (sine.MinimumForce > sine.MaximumForce) throw new InvalidDataException("Sine.MinimumForce não pode exceder MaximumForce.");
                    foreach (var v in new[] { sine.EngineVibrations, sine.GearShiftVibrations })
                        if (v != null) { Range(v.Strength, 0, 1, "Vibration.Strength"); Range(v.Frequency, 0.01, 10, "Vibration.Frequency"); }
                }
            }
        }
        if (profiles.Count(p => p.FFBParameters != null) > 1)
            throw new InvalidDataException("Selecione apenas um dispositivo de destino do FFB.");
    }

    private static void Range(double value, double min, double max, string name)
    {
        if (!double.IsFinite(value) || value < min || value > max)
            throw new InvalidDataException($"{name} deve estar entre {min} e {max}.");
    }

    public static void SaveToFile(string path, List<ControllerProfile> profiles)
    {
        Validate(profiles);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, profiles, JsonOptions);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
