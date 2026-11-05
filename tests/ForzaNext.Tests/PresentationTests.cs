using ForzaNext.Core.Config;
using ForzaNext.UI.Presentation;
using Xunit;

namespace ForzaNext.Tests;

public class PresentationTests
{
    [Fact]
    public void MappingEditorCoversEverySupportedActionExactlyOnce()
    {
        var vm = new ShellViewModel();
        Assert.Equal(new[] { "Brake", "Clutch", "Handbrake", "Steering", "Throttle" }, vm.Mappings.Where(r => r.IsAxis).Select(r => r.AxisId).OrderBy(x => x));
        Assert.Equal(Enumerable.Range(0, 36), vm.Mappings.Where(r => !r.IsAxis).Select(r => r.ButtonId!.Value).OrderBy(x => x));
    }

    [Fact]
    public void ReadyWithoutMappingsGuidesUserToMapAndNotToStart()
    {
        var vm = new ShellViewModel();
        vm.SetPhase(ShellPhase.Ready, false, "");
        Assert.Equal("Mapear controles", vm.PrimaryAction);
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        vm.RefreshMappings(new() { new() { InstanceName = "Wheel", Axes = new() { new() { Id = "Steering" } } } });
        Assert.Equal("Iniciar sessão", vm.PrimaryAction);
        Assert.Contains(nameof(vm.PrimaryAction), changes);
    }

    [Fact]
    public void MappingRefreshResetsDisconnectedSourceAndInversion()
    {
        var vm = new ShellViewModel();
        var row = vm.Mappings.Single(r => r.AxisId == "Brake");
        vm.RefreshMappings(new() { new() { InstanceName = "Pedals", Axes = new() { new() { Id = "Brake", AxisIndex = 3, Inverted = 1 } } } });
        Assert.Equal("Pedals", row.Source);
        Assert.True(row.Inverted);
        Assert.Equal("Eixo Rx", row.SourceDetail);
        vm.RefreshMappings(new());
        Assert.False(row.IsMapped);
        Assert.False(row.Inverted);
        Assert.Equal("Não mapeado", row.Source);
    }

    [Theory]
    [InlineData(ShellPhase.Preparing, "Preparando…")]
    [InlineData(ShellPhase.Starting, "Conectando…")]
    [InlineData(ShellPhase.Mapping, "Mapeando…")]
    [InlineData(ShellPhase.Running, "Parar sessão")]
    [InlineData(ShellPhase.InputOnly, "Parar sessão")]
    [InlineData(ShellPhase.Attention, "Preparar e iniciar")]
    public void PrimaryActionReflectsActualSessionPhase(ShellPhase phase, string expected)
    {
        var vm = new ShellViewModel();
        vm.SetPhase(phase, true, "Reinicie o Windows.");
        Assert.Equal(expected, vm.PrimaryAction);
        if (phase == ShellPhase.Attention) Assert.Equal("Reinicie o Windows.", vm.Description);
        if (phase == ShellPhase.InputOnly) Assert.Equal("Somente entradas", vm.StatusLabel);
    }

    [Fact]
    public void InputOnlyShowsTheActualFfbFailureInOverviewAndFfbPage()
    {
        var vm = new ShellViewModel();
        const string reason = "O volante não concluiu a abertura do FFB: Adquirindo acesso DirectInput.";
        vm.SetPhase(ShellPhase.InputOnly, true, reason);
        Assert.Equal(reason, vm.Description);
        Assert.Equal(reason, vm.FfbDescription);
        vm.SetPhase(ShellPhase.Running, true, "Entradas e FFB ativos");
        Assert.DoesNotContain("não concluiu", vm.FfbDescription);
    }
}
