using System;
using Microsoft.Win32;

namespace ForzaNext.Core.Config;

public static class GameInputConfigurator
{
    private const string BaseKeyPath = @"SYSTEM\CurrentControlSet\Control\GameInput\Devices\1234BEAD00010004";

    public static bool IsConfigured()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(BaseKeyPath);
            return key != null && (key.GetValue("Description")?.ToString() == "Forza EmuWheel");
        }
        catch
        {
            return false;
        }
    }

    public static bool RegisterForzaRacingWheel(int maxWheelAngleDegrees = 900)
    {
        try
        {
            using var baseKey = Registry.LocalMachine.CreateSubKey(BaseKeyPath);
            if (baseKey == null) return false;

            baseKey.SetValue("Description", "Forza EmuWheel", RegistryValueKind.String);

            // Labels
            using (var labelsButtons = baseKey.CreateSubKey(@"Labels\Buttons"))
            {
                labelsButtons.SetValue("Button0", "Menu", RegistryValueKind.String);
                labelsButtons.SetValue("Button1", "View", RegistryValueKind.String);
                labelsButtons.SetValue("Button3", "XboxA", RegistryValueKind.String);
                labelsButtons.SetValue("Button4", "XboxB", RegistryValueKind.String);
                labelsButtons.SetValue("Button5", "XboxX", RegistryValueKind.String);
                labelsButtons.SetValue("Button6", "XboxY", RegistryValueKind.String);
            }

            using (var labelsSwitches = baseKey.CreateSubKey(@"Labels\Switches"))
            {
                labelsSwitches.SetValue("Switch0Down", "Down", RegistryValueKind.String);
                labelsSwitches.SetValue("Switch0Left", "Left", RegistryValueKind.String);
                labelsSwitches.SetValue("Switch0Right", "Right", RegistryValueKind.String);
                labelsSwitches.SetValue("Switch0Up", "Up", RegistryValueKind.String);
            }

            // RacingWheel root
            using (var rw = baseKey.CreateSubKey("RacingWheel"))
            {
                rw.SetValue("MaxWheelAngle", maxWheelAngleDegrees, RegistryValueKind.DWord);

                // Wheel Axis (Index 0 = X)
                using (var sub = rw.CreateSubKey("Wheel"))
                {
                    sub.SetValue("AxisIndex", 0, RegistryValueKind.DWord);
                    sub.SetValue("Invert", 0, RegistryValueKind.DWord);
                }

                // Throttle (Index 2 = Y)
                using (var sub = rw.CreateSubKey("Throttle"))
                {
                    sub.SetValue("AxisIndex", 2, RegistryValueKind.DWord);
                    sub.SetValue("Invert", 1, RegistryValueKind.DWord);
                }

                // Brake (Index 3 = Z)
                using (var sub = rw.CreateSubKey("Brake"))
                {
                    sub.SetValue("AxisIndex", 3, RegistryValueKind.DWord);
                    sub.SetValue("Invert", 1, RegistryValueKind.DWord);
                }

                // Clutch (Index 4 = Rx)
                using (var sub = rw.CreateSubKey("Clutch"))
                {
                    sub.SetValue("AxisIndex", 4, RegistryValueKind.DWord);
                    sub.SetValue("Invert", 1, RegistryValueKind.DWord);
                }

                // Handbrake (Index 5 = Ry)
                using (var sub = rw.CreateSubKey("Handbrake"))
                {
                    sub.SetValue("AxisIndex", 5, RegistryValueKind.DWord);
                    sub.SetValue("Invert", 0, RegistryValueKind.DWord);
                }

                // Gears
                int[] gearButtonIndices = { 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19 };
                string[] gearNames = { "FirstGear", "SecondGear", "ThirdGear", "FourthGear", "FifthGear", "SixthGear", "SeventhGear" };
                for (int i = 0; i < gearNames.Length; i++)
                {
                    using var sub = rw.CreateSubKey(gearNames[i]);
                    sub.SetValue("ButtonIndex", gearButtonIndices[i], RegistryValueKind.DWord);
                }

                using (var sub = rw.CreateSubKey("ReverseGear"))
                {
                    sub.SetValue("ButtonIndex", 0x12, RegistryValueKind.DWord);
                }

                using (var sub = rw.CreateSubKey("PreviousGear"))
                {
                    sub.SetValue("ButtonIndex", 0x10, RegistryValueKind.DWord);
                }

                using (var sub = rw.CreateSubKey("NextGear"))
                {
                    sub.SetValue("ButtonIndex", 0x11, RegistryValueKind.DWord);
                }

                // Buttons 1..16
                for (int b = 1; b <= 16; b++)
                {
                    using var sub = rw.CreateSubKey($"Button{b}");
                    sub.SetValue("ButtonIndex", b - 1, RegistryValueKind.DWord);
                }

                // DPad
                string[] dpadDirs = { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" };
                string[] switchPositions = { "Up", "Down", "Left", "Right" };
                for (int i = 0; i < dpadDirs.Length; i++)
                {
                    using var sub = rw.CreateSubKey(dpadDirs[i]);
                    sub.SetValue("IncludeAdjacent", 1, RegistryValueKind.DWord);
                    sub.SetValue("SwitchIndex", 0, RegistryValueKind.DWord);
                    sub.SetValue("SwitchPosition", switchPositions[i], RegistryValueKind.String);
                }
            }

            // UI Navigation
            using (var ui = baseKey.CreateSubKey("UINavigation"))
            {
                using (var s = ui.CreateSubKey("Menu")) s.SetValue("ButtonIndex", 0, RegistryValueKind.DWord);
                using (var s = ui.CreateSubKey("View")) s.SetValue("ButtonIndex", 1, RegistryValueKind.DWord);
                using (var s = ui.CreateSubKey("Accept")) s.SetValue("ButtonIndex", 2, RegistryValueKind.DWord);
                using (var s = ui.CreateSubKey("Cancel")) s.SetValue("ButtonIndex", 3, RegistryValueKind.DWord);

                string[] navDirs = { "PrimaryUp", "PrimaryDown", "PrimaryLeft", "PrimaryRight" };
                string[] navPositions = { "Up", "Down", "Left", "Right" };
                for (int i = 0; i < navDirs.Length; i++)
                {
                    using var sub = ui.CreateSubKey(navDirs[i]);
                    sub.SetValue("IncludeAdjacent", 1, RegistryValueKind.DWord);
                    sub.SetValue("SwitchIndex", 0, RegistryValueKind.DWord);
                    sub.SetValue("SwitchPosition", navPositions[i], RegistryValueKind.String);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to write GameInput registry key: {ex.Message}");
            return false;
        }
    }
}
