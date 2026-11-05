using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.IO.Pipes;
using ForzaNext.Core.Virtual;
using Xunit;

namespace ForzaNext.Tests;

public class VJoyLifetimeTests
{
    private sealed class Lease : IVJoyDeviceLease
    {
        public bool IsActive { get; private set; } = true;
        public int Stops;
        public Task<VJoySetupResult> StopAsync()
        {
            Stops++;
            IsActive = false;
            return Task.FromResult(new VJoySetupResult(true, false, false, "Disabled"));
        }
    }

    [Fact]
    public async Task StartReusesDeviceLeaseAndClosingStopsExactlyOnce()
    {
        var lease = new Lease();
        int opens = 0;
        var lifetime = new VJoyLifetime(() => { opens++; return Task.FromResult<IVJoyDeviceLease>(lease); });
        await lifetime.EnsureAsync();
        await lifetime.EnsureAsync();
        Assert.Equal(1, opens);
        Assert.True(lifetime.IsActive);
        Assert.True((await lifetime.StopAsync()).Success);
        Assert.True((await lifetime.StopAsync()).Success);
        Assert.Equal(1, lease.Stops);
        Assert.False(lifetime.IsActive);
    }

    [Fact]
    public async Task ClosingDuringPreparationReleasesLeaseAsSoonAsItArrives()
    {
        var ready = new TaskCompletionSource<IVJoyDeviceLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new Lease();
        var lifetime = new VJoyLifetime(() => ready.Task);
        var start = lifetime.EnsureAsync();
        var stop = lifetime.StopAsync();
        Assert.False(stop.IsCompleted);
        ready.SetResult(lease);
        await start;
        await stop;
        Assert.Equal(1, lease.Stops);
        Assert.False(lifetime.IsActive);
    }

    [Fact]
    public async Task NewOpeningAndExplicitRepairCreateNewLeases()
    {
        var created = new List<Lease>();
        var lifetime = new VJoyLifetime(() =>
        {
            var lease = new Lease(); created.Add(lease);
            return Task.FromResult<IVJoyDeviceLease>(lease);
        });
        await lifetime.EnsureAsync();
        await lifetime.EnsureAsync(force: true);
        Assert.Equal(1, created[0].Stops);
        await lifetime.StopAsync();
        await lifetime.EnsureAsync();
        Assert.Equal(3, created.Count);
        await lifetime.StopAsync();
        Assert.All(created, lease => Assert.Equal(1, lease.Stops));
    }

    [Fact]
    public async Task CanceledWindowsAuthorizationDoesNotCreateAnOwnedDevice()
    {
        var lifetime = new VJoyLifetime(() => Task.FromException<IVJoyDeviceLease>(new IOException("Authorization canceled")));
        await Assert.ThrowsAsync<IOException>(() => lifetime.EnsureAsync());
        Assert.False(lifetime.IsActive);
        Assert.True((await lifetime.StopAsync()).Success);
    }

    [Theory]
    [InlineData(0x1234, 0xBEAD, 1, true)]
    [InlineData(0x1234, 0x0FFB, 1, true)]
    [InlineData(0x1234, 0x0FFB, 2, false)]
    [InlineData(0x1209, 0x0D40, 1, false)]
    [InlineData(0x1209, 0xC001, 1, false)]
    public void LifecycleIdentityExcludesPhysicalControllersAndOtherVJoySlots(int vendor, int product, int report, bool allowed)
        => Assert.Equal(allowed, VJoyDeviceNode.IsSupportedIdentity((ushort)vendor, (ushort)product, (byte)report));

    [Theory]
    [InlineData(@"HID\VID_1234&PID_0FFB&COL01\8&123&0&0000", true)]
    [InlineData(@"HID\VID_1234&PID_BEAD&REV_0219&COL01\8&123&0&0000", true)]
    [InlineData(@"HID\HIDCLASS\1&2D595CA7&B&0000", true)]
    [InlineData(@"hid\hidclass\1&example&0&0000", true)]
    [InlineData(@"HID\HIDCLASS\", false)]
    [InlineData(@"HID\HIDCLASS\one\extra", false)]
    [InlineData(@"HID\VID_1209&PID_0D40\8&123&0&0000", false)]
    [InlineData(@"ROOT\HIDCLASS\0000", false)]
    public void DeviceInstanceMustBeKnownVJoyHidFamily(string instance, bool allowed)
        => Assert.Equal(allowed, VJoyDeviceNode.IsSupportedInstance(instance));

    [Fact]
    public void LifecyclePipeGrantsOnlyTheCurrentUserAndAdministrators()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var security = VJoyDeviceLease.CreatePipeSecurity(user);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.Contains(rules, r => r.IdentityReference.Equals(user) && r.PipeAccessRights == PipeAccessRights.FullControl);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        Assert.Contains(rules, r => r.IdentityReference.Equals(admins) && (r.PipeAccessRights & PipeAccessRights.Synchronize) != 0);
    }

    [Fact]
    public void DeviceDiscoveryStructsMatchWindowsX64Abi()
    {
        Assert.Equal(32, Marshal.SizeOf<VJoyDeviceNode.DeviceInfo>());
        Assert.Equal(32, Marshal.SizeOf<VJoyDeviceNode.DeviceInterface>());
        Assert.Equal(12, Marshal.SizeOf<VJoyDeviceNode.HidAttributes>());
        Assert.Equal(64, Marshal.SizeOf<VJoyDeviceNode.HidCaps>());
        Assert.Equal(48, Marshal.OffsetOf<VJoyDeviceNode.HidCaps>(nameof(VJoyDeviceNode.HidCaps.InputValues)).ToInt32());
        Assert.Equal(72, VJoyDeviceNode.ValueCapsSize);
        Assert.Equal(2, VJoyDeviceNode.ValueCapsReportIdOffset);
    }
}
