using ForzaNext.Core.Virtual;
using Xunit;

namespace ForzaNext.Tests;

public class VJoySetupTests
{
    private static VJoyInspection Ready() => new(true, true, true, VjdStatus.VJD_STAT_FREE,
        128, 1, VJoyInspection.RecommendedAxes.ToArray(), true, null);

    [Fact]
    public void RecommendedStateRequiresAllCapabilities()
    {
        var state = Ready();
        Assert.True(state.ConfigurationReady);
        Assert.False((state with { Buttons = 8 }).ConfigurationReady);
        Assert.False((state with { ContinuousPovs = 0 }).ConfigurationReady);
        Assert.False((state with { Axes = new[] { "X", "Y" } }).ConfigurationReady);
        Assert.False((state with { HasFfb = false }).ConfigurationReady);
        Assert.False((state with { VersionMatches = false }).ConfigurationReady);
        Assert.False((state with { Enabled = false }).ConfigurationReady);
        Assert.False((state with { Installed = false }).ConfigurationReady);
        Assert.False((state with { Error = "Inspection failed" }).ConfigurationReady);
    }

    [Theory]
    [InlineData(VjdStatus.VJD_STAT_BUSY)]
    [InlineData(VjdStatus.VJD_STAT_OWN)]
    public void BusyDeviceCannotBePresentedAsAvailableForReconfiguration(VjdStatus status)
    {
        var state = Ready() with { Status = status };
        Assert.True(state.Busy);
        Assert.Contains("ocupado", state.Summary);
    }

    [Fact]
    public void ConfigurationOperationTargetsOnlyDeviceOneWithoutGlobalResetOrDelete()
    {
        var args = VJoySetupService.ConfigurationArguments;
        Assert.Equal("1", args[0]);
        Assert.DoesNotContain("-r", args);
        Assert.DoesNotContain("-d", args);
        Assert.Equal("128", args[args.ToList().IndexOf("-b") + 1]);
        Assert.Equal("1", args[args.ToList().IndexOf("-p") + 1]);
        Assert.Equal("all", args[args.ToList().IndexOf("-e") + 1]);
    }

    [Fact]
    public async Task UntrustedInstallerBytesAreRejected()
    {
        var file = Path.Combine(Path.GetTempPath(), "ForzaNext-installer-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(file, "not the pinned signed vJoy installer");
            Assert.False(await VJoySetupService.InstallerMatchesAsync(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void DiagnosticExplainsMissingDriverAndMismatchedVersions()
    {
        Assert.Contains("não instalado", (Ready() with { Installed = false }).Summary);
        Assert.Contains("versões diferentes", (Ready() with { VersionMatches = false }).Summary);
        Assert.Contains("desativado ou inacessível", (Ready() with { VersionMatches = false, DllVersion = 0x222, DriverVersion = 0 }).Summary);
    }
}
