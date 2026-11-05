using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ForzaNext.Core;
using ForzaNext.Core.Config;
using ForzaNext.UI.Presentation;

namespace ForzaNext.UI;

public partial class MainWindow
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int enabled = 1;
        _ = DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
    }

    private readonly ShellViewModel _viewModel = new();
    private readonly bool _preview;
    private bool _previewRunning;
    private bool _loadingConfiguration;
    private string? _mappingUndo;
    private string? _sessionError;

    private void RememberMappings()
    {
        _mappingUndo = System.Text.Json.JsonSerializer.Serialize(_profiles);
        _viewModel.HasUndo = true;
    }

    private void UndoMapping_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit() || _mappingUndo == null) return;
        try
        {
            var restored = System.Text.Json.JsonSerializer.Deserialize<List<ControllerProfile>>(_mappingUndo)!;
            if (!_preview) ProfileManager.SaveToFile(_configPath, restored);
            _profiles = restored;
            _mappingUndo = null; _viewModel.HasUndo = false;
            UpdateMappingLabels();
            TxtMappingStatus.Text = "Última alteração de mapeamento desfeita.";
        }
        catch (Exception ex) { TxtMappingStatus.Text = ex.Message; LogTrace(ex.ToString()); }
    }

    public void SelectPage(int index)
    {
        if (MainTabs == null) return;
        MainTabs.SelectedIndex = Math.Clamp(index, 0, 3);
        string[] titles = { "Visão geral", "Controles", "Force Feedback", "Diagnóstico" };
        string[] descriptions = { "Seu setup. Uma conexão com a pista.", "Associe suas entradas físicas ao volante virtual.", "Ajuste a resposta do volante à sua pilotagem.", "Confira o dispositivo e resolva sua conexão." };
        PageTitle.Text = titles[MainTabs.SelectedIndex];
        PageDescription.Text = descriptions[MainTabs.SelectedIndex];
        var nav = new[] { Nav0, Nav1, Nav2, Nav3 }[MainTabs.SelectedIndex];
        if (nav.IsChecked != true) nav.IsChecked = true;
    }

    private void Navigate_Checked(object sender, RoutedEventArgs e)
    { if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out int index)) SelectPage(index); }
    private void GoControls_Click(object sender, RoutedEventArgs e) => SelectPage(1);
    private void Shell_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _detectCancel?.Cancel(); return; }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.S) { BtnSaveConfig_Click(this, new()); e.Handled = true; }
        if (e.Key >= Key.D1 && e.Key <= Key.D4) { SelectPage(e.Key - Key.D1); e.Handled = true; }
    }

    private void UpdatePresentation()
    {
        bool idle = !_calibrating && !_closing && !_setupBusy && !_refreshingVjoy && !_isStarting && !_isDetecting;
        _viewModel.CanEdit = idle && !_isFeeding && !_previewRunning;
        _viewModel.CanSave = idle;
        _viewModel.CanTest = !_preview && _isFeeding && _session?.HasFfb == true && !_motorTestRunning && !_closing;
        var phase = _setupBusy ? ShellPhase.Preparing : _isStarting ? ShellPhase.Starting : _isDetecting ? ShellPhase.Mapping
            : _isFeeding ? (_session?.HasFfb == true ? ShellPhase.Running : ShellPhase.InputOnly)
            : _previewRunning ? ShellPhase.Running
            : _sessionError != null ? ShellPhase.Attention
            : (_virtualDeviceLifetime.IsActive || _preview) ? ShellPhase.Ready
            : _vjoyInspection is { ConfigurationReady: false } ? ShellPhase.Attention : ShellPhase.NeedsSetup;
        string detail = _sessionError ?? (_isFeeding ? _session?.Telemetry.Status : null) ?? TxtVjoySetup.Text;
        _viewModel.SetPhase(phase, _profiles.Any(p => p.Axes?.Count > 0 || p.Buttons?.Count > 0 || p.DPad != null), detail);
        BtnToggleFeeder.IsEnabled = idle;
    }

    private async void MapRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MappingRow row }) return;
        if (_preview) { TxtMappingStatus.Text = "Prévia: conecte seus dispositivos no modo normal para mapear."; return; }
        await DetectAsync(row.AxisId, row.ButtonId ?? -1);
    }
    private void ClearRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MappingRow row } || !CanEdit()) return;
        try
        {
            SyncAxisInversions();
            RememberMappings();
            foreach (var p in _profiles)
            {
                if (row.IsAxis) p.Axes?.RemoveAll(a => a.Id == row.AxisId);
                else p.Buttons?.RemoveAll(b => b.Id == row.ButtonId);
            }
            if (!_preview) ProfileManager.SaveToFile(_configPath, _profiles);
            UpdateMappingLabels();
            TxtMappingStatus.Text = $"Associação removida: {row.Title}.";
        }
        catch (Exception ex) { TxtMappingStatus.Text = ex.Message; LogTrace(ex.ToString()); }
    }
    private void MappingInvert_Click(object sender, RoutedEventArgs e) => _viewModel.IsDirty = true;
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (_preview) { TxtConsole.Text = "Prévia: nenhuma pasta de diagnóstico foi criada."; return; }
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDirectory) { UseShellExecute = true });
    }

    private void UpdateLiveMetrics(double frequency, double interval)
    {
        TxtRate.Text = $"{frequency:F0} Hz";
        TxtInterval.Text = $"Maior intervalo: {interval:F2} ms";
        double steering = (_lastPosition.AxisX - 16384.0) / 16384.0;
        WheelRotation.Angle = Math.Clamp(steering, -1, 1) * 135;
        TxtSteeringPercent.Text = Math.Abs(steering) < .01 ? "Centralizado" : $"{Math.Abs(steering):P0} {(steering < 0 ? "à esquerda" : "à direita")}";
        TxtThrottlePercent.Text = $"{(PbThrottle.Value - 1) / 32767:P0}";
        TxtBrakePercent.Text = $"{(PbBrake.Value - 1) / 32767:P0}";
        TxtClutchPercent.Text = $"{(PbClutch.Value - 1) / 32767:P0}";
        TxtHandbrakePercent.Text = $"{(PbHandbrake.Value - 1) / 32767:P0}";
    }
    private void ResetLiveMetrics()
    {
        UpdateFfbMonitor(null);
        PbSteering.Value = 16384;
        PbThrottle.Value = PbBrake.Value = PbClutch.Value = PbHandbrake.Value = 1;
        WheelRotation.Angle = 0;
        TxtSteeringPercent.Text = "Centralizado";
        TxtThrottlePercent.Text = TxtBrakePercent.Text = TxtClutchPercent.Text = TxtHandbrakePercent.Text = "0%";
        TxtCurrentGear.Text = "N";
        TxtActiveButtons.Text = "Nenhum botão pressionado";
        TxtRate.Text = "— Hz"; TxtInterval.Text = "Aguardando sessão";
    }
    private void InitializePreview()
    {
        _viewModel.IsPreview = true;
        InitializeFeaturePreview();
        _profiles = new()
        {
            new() { InstanceName = "ODrive-Wheel HID", Axes = new() {
                new() { Id = "Steering", AxisIndex = 0 }, new() { Id = "Throttle", AxisIndex = 2 },
                new() { Id = "Brake", AxisIndex = 3 }, new() { Id = "Clutch", AxisIndex = 4 } } },
            new() { InstanceName = "Shifter Game Controller", Buttons = new() {
                new() { Id = 17, Index = 0 }, new() { Id = 16, Index = 1 }, new() { Id = 19, Index = 2 } } }
        };
        TxtVirtualStatus.Text = "Preparado"; TxtWheelStatus.Text = "ODrive-Wheel HID";
        TxtShifterStatus.Text = "Shifter Game Controller";
        TxtVjoySetup.Text = "Prévia do dispositivo preparado: 8 eixos, 128 botões, D-pad e retorno de força. Nenhum driver foi acessado.";
        TxtConsole.Text = "Prévia da interface · dados simulados · driver e dispositivos físicos não acessados.";
        UpdateMappingLabels();
        FfbSlider_ValueChanged(this, new());
    }

    internal string[] VerifyPreviewInteractions()
    {
        if (!_preview) throw new InvalidOperationException("Verification requires isolated preview mode.");
        var passed = new List<string>();
        for (int page = 0; page < 4; page++)
        {
            new[] { Nav0, Nav1, Nav2, Nav3 }[page].IsChecked = true;
            if (MainTabs.SelectedIndex != page) throw new InvalidOperationException("Navigation failed.");
        }
        passed.Add("Four navigation destinations");
        BtnToggleFeeder.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (_viewModel.Phase != ShellPhase.Running || _viewModel.CanEdit || _viewModel.CanTest)
            throw new InvalidOperationException("Preview running state failed.");
        BtnToggleFeeder.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (_viewModel.Phase != ShellPhase.Ready || !_viewModel.CanEdit)
            throw new InvalidOperationException("Preview stop state failed.");
        passed.Add("Start, stop and editing availability without native devices");
        var row = _viewModel.Mappings.Single(r => r.AxisId == "Steering");
        ClearRow_Click(new Button { Tag = row }, new());
        if (row.IsMapped || !_viewModel.HasUndo) throw new InvalidOperationException("Clear mapping failed.");
        UndoMapping_Click(this, new());
        if (!row.IsMapped || _viewModel.HasUndo) throw new InvalidOperationException("Undo mapping failed.");
        passed.Add("Remove and undo mappings without profile writes");
        _viewModel.IsDirty = true;
        BtnSaveConfig_Click(this, new());
        if (_viewModel.IsDirty) throw new InvalidOperationException("Save state failed.");
        passed.Add("Save feedback without profile writes");
        BtnPrepareVjoy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        BtnRefreshVjoy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        passed.Add("Preview preparation and inspection guards");
        ManageProfiles_Click(this, new()); CalibrateRow_Click(new Button { Tag = row }, new());
        RestoreProfile_Click(this, new()); InstallUpdate_Click(this, new()); Benchmark_Click(this, new());
        if (_profileStore != null || _calibrating || _pendingUpdate != null || ProfilePicker.SelectedIndex != 0)
            throw new InvalidOperationException("Feature preview isolation failed.");
        var previous = _profiles;
        _loadingConfiguration = true;
        _profiles = new() { new() { FFBParameters = new() { Const = new() { MaximumForce = .2 } } } };
        LoadFfbSettingsFromProfile();
        if (SlMasterGain.Value != .2) throw new InvalidOperationException("Profile FFB loading failed.");
        _profiles = new(); LoadFfbSettingsFromProfile();
        if (SlMasterGain.Value != 1 || SlDamper.Value != .15) throw new InvalidOperationException("Profile FFB defaults leaked.");
        _profiles = previous; _loadingConfiguration = false;
        passed.Add("Profile defaults, calibration, update and benchmark preview isolation");
        return passed.ToArray();
    }
}
