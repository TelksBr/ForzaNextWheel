using System.Diagnostics;
using System.Text.Json;
using ForzaNext.Core;
using ForzaNext.Core.Config;
using ForzaNext.Core.Devices;
using ForzaNext.Core.Timing;
using Xunit;

namespace ForzaNext.Tests;

public sealed class FeatureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ForzaNext-tests-" + Guid.NewGuid().ToString("N"));
    private List<ControllerProfile> Controllers() => new() { new() { InstanceGuid = Guid.NewGuid(), InstanceName = "Wheel", ProductName = "Wheel", Axes = new() { new() { Id = "Steering", AxisIndex = 0 } } } };
    private GameProfileStore Store() { var store = new GameProfileStore(_root); store.Initialize(Controllers()); return store; }
    [Fact] public void ProfilesKeepMappingsCalibrationAndFfbIndependent()
    {
        var store = Store(); var original = store.Active.Id;
        var controllers = store.Open(original);
        controllers[0].Axes![0].Calibration = new() { Minimum = 1000, Center = 30000, Maximum = 60000 };
        controllers[0].FFBParameters = new() { Const = new() { Magnitude = .35 } };
        var game = store.Create("Race", "Forza", controllers);
        store.Activate(game.Id);
        var reopened = new GameProfileStore(_root); reopened.Initialize(new());
        Assert.Equal(game.Id, reopened.Active.Id);
        Assert.Equal(1000, reopened.Open(game.Id)[0].Axes![0].Calibration!.Minimum);
        Assert.Equal(.35, reopened.Open(game.Id)[0].FFBParameters!.Const!.Magnitude);
        Assert.Null(reopened.Open(original)[0].Axes![0].Calibration);
    }
    [Fact] public void CorruptProfileRecoversBackupAndPreservesDamagedFile()
    {
        var store = Store(); string path = store.PathFor(store.Active.Id);
        var controllers = store.Open(store.Active.Id); controllers[0].InstanceName = "Changed";
        ProfileManager.SaveToFile(path, controllers); File.WriteAllText(path, "broken");
        Assert.Equal("Wheel", store.Open(store.Active.Id)[0].InstanceName);
        Assert.NotNull(store.RecoveryNotice);
        Assert.Single(Directory.GetFiles(_root, "*.recovered-*"));
    }
    [Fact] public void ExportImportAndDeletePreserveArchive()
    {
        var store = Store(); string exported = Path.Combine(_root, "export.json");
        store.Export(store.Active.Id, exported); var imported = store.Import(exported);
        Assert.NotEqual(store.Active.Id, imported.Id);
        Assert.Equal(store.Open(store.Active.Id)[0].InstanceGuid, store.Open(imported.Id)[0].InstanceGuid);
        Assert.Throws<InvalidOperationException>(() => store.Delete(store.Active.Id));
        store.Delete(imported.Id);
        Assert.True(File.Exists(Path.Combine(_root, "Archive", imported.Id.ToString("N") + ".json")));
        Assert.Single(store.Profiles);
    }
    [Theory] [InlineData("{\"Profiles\":null}")] [InlineData("{\"Profiles\":[null]}")]
    public void InvalidCatalogIsRejectedWithoutOverwritingProfiles(string invalid)
    {
        var store = Store(); string path = store.PathFor(store.Active.Id);
        File.WriteAllText(Path.Combine(_root, "index.json"), invalid);
        Assert.Throws<InvalidDataException>(() => new GameProfileStore(_root).Initialize(new()));
        Assert.True(File.Exists(path));
    }
    [Fact] public void CorruptCatalogRecoversLastValidMetadata()
    {
        var store = Store(); var original = store.Active.Id;
        store.Create("Other", "Game", Controllers());
        File.WriteAllText(Path.Combine(_root, "index.json"), "broken");
        var reopened = new GameProfileStore(_root); reopened.Initialize(new());
        Assert.Equal(original, reopened.Active.Id); Assert.NotNull(reopened.RecoveryNotice);
    }
    [Fact] public void InvalidImportDoesNotChangeCatalog()
    {
        var store = Store(); string path = Path.Combine(_root, "bad.json");
        File.WriteAllText(path, "{\"Product\":\"OtherProduct\"}");
        Assert.Throws<InvalidDataException>(() => store.Import(path)); Assert.Single(store.Profiles);
    }
    [Theory] [InlineData(500, 0)] [InlineData(1000, 0)] [InlineData(25000, .5)] [InlineData(60000, 1)] [InlineData(65535, 1)]
    public void SteeringUsesMeasuredCenterAndClampsLimits(int raw, double expected)
    {
        var calibration = new AxisCalibration { Minimum = 1000, Center = 25000, Maximum = 60000 };
        Assert.Equal(expected, calibration.Normalize(raw, true), 6);
    }
    [Fact] public void PedalCalibrationAppliesInversionBeforeDeadzone()
    {
        var c = new AxisCalibration { Minimum = 1000, Maximum = 61000 };
        Assert.Equal(1, InputAggregator.ProcessAxisValue(61000, true, .05, false, c));
        Assert.Equal(32768, InputAggregator.ProcessAxisValue(1000, true, .05, false, c));
        Assert.Equal(1, InputAggregator.ProcessAxisValue(60000, true, .05, false, c));
    }
    [Fact] public void CalibrationCannotAcceptZeroRangeOrCenterOutsideLimits()
    {
        Assert.Throws<InvalidDataException>(() => new AxisCalibration { Minimum = 2000, Maximum = 2000 }.Validate(false));
        Assert.Throws<InvalidDataException>(() => new AxisCalibration { Center = 0 }.Validate(true));
        Assert.Throws<InvalidDataException>(() => new AxisCalibration { Minimum = int.MaxValue - 5000, Maximum = int.MinValue }.Validate(false));
    }
    [Fact] public void TimingStatisticsIncludeTailLatency()
    {
        var result = LoopBenchmark.Summarize("test", Enumerable.Repeat(1d, 99).Append(20d).ToArray());
        Assert.Equal(1, result.P95Ms); Assert.Equal(1, result.P99Ms); Assert.Equal(20, result.MaxMs);
        Assert.Equal(1000 / 1.19, result.FrequencyHz, 6);
    }
    [Fact] public void TimerHonorsCancellationWithDifferentTokens()
    {
        using var timer = new LoopTimer();
        timer.WaitUntil(Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000, CancellationToken.None);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => timer.WaitUntil(Stopwatch.GetTimestamp() + Stopwatch.Frequency, canceled.Token));
    }
    [Fact] public void CalibrationSampleSurvivesWorkerSerialization()
    {
        var input = new InputSample(Guid.NewGuid(), "Wheel", new[] { 1, 200, 300, 400, 500, 600, 700, 65535 }, 42);
        var message = JsonSerializer.Deserialize<WorkerMessage>(WorkerMessage.Encode("input-sample", input), WorkerMessage.Json)!;
        var decoded = message.Read<InputSample>();
        Assert.Equal(input.InstanceGuid, decoded.InstanceGuid); Assert.Equal(input.Axes, decoded.Axes); Assert.Equal(42, decoded.Sequence);
    }
    [Fact] public void FfbMonitoringSurvivesWorkerSerialization()
    {
        var monitor = new FfbMonitor(1234, 500, 2, "Constant, Sine", true, 3, 10, "High resolution");
        var telemetry = new WheelTelemetry(InputAggregator.NeutralPosition(), 999, 1.5, -10000, "Running", 42, monitor);
        var message = JsonSerializer.Deserialize<WorkerMessage>(WorkerMessage.Encode("telemetry", telemetry), WorkerMessage.Json)!;
        Assert.Equal(monitor, message.Read<WheelTelemetry>().Ffb);
    }
    private string Manifest(string package = "setup.exe", string hash = "", string version = "2.2.0", long size = 4)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "release.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ReleaseManifest("ForzaNextWheel", version, package, hash, size)));
        return path;
    }
    [Fact] public async Task UpdateChecksCopiedBytesAndKeepsVerifiedCopyIndependent()
    {
        string path = Manifest(hash: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3, 4 })));
        File.WriteAllBytes(Path.Combine(_root, "setup.exe"), new byte[] { 1, 2, 3, 4 });
        var prepared = await ReleasePackage.PrepareAsync(path, new(2, 1, 0), Path.Combine(_root, "Staging"));
        File.WriteAllBytes(Path.Combine(_root, "setup.exe"), new byte[] { 9 });
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(prepared.Installer));
    }
    [Theory] [InlineData("../outside.exe")] [InlineData("C:\\outside.exe")] [InlineData("setup.zip")]
    public async Task UpdateRejectsPathsOutsidePackage(string package)
    {
        string manifest = Manifest(package, new string('0', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleasePackage.PrepareAsync(manifest, new(2, 1, 0), Path.Combine(_root, "Staging")));
    }
    [Fact] public async Task UpdateRejectsDamagedInstallerAndRemovesStagingFile()
    {
        string path = Manifest(hash: new string('0', 64));
        File.WriteAllBytes(Path.Combine(_root, "setup.exe"), new byte[] { 1, 2, 3, 4 });
        string stage = Path.Combine(_root, "Staging");
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleasePackage.PrepareAsync(path, new(2, 1, 0), stage));
        Assert.Empty(Directory.GetFiles(stage));
    }
    [Theory] [InlineData("1.0.0")] [InlineData("2.1.0")]
    public async Task UpdateRefusesSameOrOlderVersion(string version)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleasePackage.PrepareAsync(Manifest(version: version), new(2, 1, 0), _root));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
