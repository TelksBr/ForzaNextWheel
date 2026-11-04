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
        if (!File.Exists(path))
            throw new FileNotFoundException($"Profile not found: {path}");

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<ControllerProfile>>(json, JsonOptions) ?? new();
    }

    public static void SaveToFile(string path, List<ControllerProfile> profiles)
    {
        string json = JsonSerializer.Serialize(profiles, JsonOptions);
        File.WriteAllText(path, json);
    }
}
