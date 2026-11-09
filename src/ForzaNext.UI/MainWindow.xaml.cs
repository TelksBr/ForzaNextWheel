using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ForzaNext.Core;
using ForzaNext.Core.Config;
using ForzaNext.Core.Devices;
using ForzaNext.Core.FFB;
using ForzaNext.Core.Virtual;

namespace ForzaNext.UI;

public partial class MainWindow : Window
{
    private static void LogTrace(string message) => DiagnosticLog.Write("UI", message);
    private string _configPath = AppPaths.Configuration;
    private List<ControllerProfile> _profiles = new();
    private WheelSession? _session;
    private readonly VJoyLifetime _virtualDeviceLifetime = new();
    private bool _isFeeding;
    private bool _isStarting;
    private bool _isDetecting;
    private bool _motorTestRunning;
    private bool _setupBusy;
    private bool _refreshingVjoy;
    private VJoyInspection? _vjoyInspection;
    private bool _closing;
    private bool _canClose;
    private Guid? _ffbTarget;
    private Task _detectionTask = Task.CompletedTask;

    private readonly DispatcherTimer _uiTimer = new();
    private JoystickPositionV2 _lastPosition;
    private int _currentFfbMagnitude = 0;

    private CancellationTokenSource? _detectCancel;
    private IntPtr _mainWindowHandle;

    public MainWindow(bool preview = false)
    {
        _preview = preview;
        InitializeComponent();
        DataContext = _viewModel;
        Nav0.IsChecked = true;
        if (preview) { InitializePreview(); return; }
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        _mainWindowHandle = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();

        LogTrace("[INIT] MainWindow Constructor");

        try { AppPaths.EnsureConfiguration(); }
        catch (Exception ex) { LogTrace(ex.ToString()); MessageBox.Show($"Não foi possível preparar o perfil: {ex.Message}"); }

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LogTrace("[INIT] MainWindow_Loaded start");
        LoadConfiguration();
        InitializeGameProfiles();
        await InitializeDevicesAsync();
        await EnsureVirtualDeviceAsync();

        if (_closing) return;

        _uiTimer.Interval = TimeSpan.FromMilliseconds(33); // ~30 fps UI refresh
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();
        LogTrace("[INIT] MainWindow_Loaded complete");
    }

    private void LoadConfiguration()
    {
        _loadingConfiguration = true;
        try
        {
            if (File.Exists(_configPath))
            {
                _profiles = ProfileManager.LoadFromFile(_configPath);
                TxtConsole.Text = $"Configuração carregada com {_profiles.Count} dispositivos.";
                UpdateMappingLabels();
                LoadFfbSettingsFromProfile();
            }
            else
            {
                TxtConsole.Text = "Nenhum arquivo configuration.json encontrado.";
            }
        }
        catch (Exception ex)
        {
            TxtConsole.Text = $"Erro ao carregar perfil: {ex.Message}";
        }
        finally
        {
            FfbSlider_ValueChanged(this, new());
            _loadingConfiguration = false;
            _viewModel.IsDirty = false;
            _mappingUndo = null; _viewModel.HasUndo = false;
        }
    }

    private void LoadFfbSettingsFromProfile()
    {
        var ffb = DirectInputFFBBridge.FromProfile(_profiles.FirstOrDefault(p => p.FFBParameters != null)?.FFBParameters);
        ChkInvertFFB.IsChecked = ffb.InvertConstantForce;
        SlMasterGain.Value = ffb.MasterGain; SlConstantForce.Value = ffb.ConstantForceScale;
        SlMinimumForce.Value = ffb.MinimumForce; SlFilterThreshold.Value = ffb.FilterThreshold;
        SlMinCoeff.Value = ffb.MinimumCoefficient; SlDamper.Value = ffb.DamperCoefficient;
        SlSpring.Value = ffb.SpringCoefficient; SlGearShift.Value = ffb.GearShiftStrength;
        SlEngineVib.Value = ffb.EngineVibrationStrength;
    }

    private void UpdateMappingLabels() { _viewModel.RefreshMappings(_profiles); UpdatePresentation(); }

    private async Task InitializeDevicesAsync()
    {
        try
        {
            var devices = await Task.Run(() =>
            {
                using var manager = new DirectInputDeviceManager();
                return manager.EnumerateDevices();
            });
            if (_closing) return;
            _availableDevices = devices;
            await RefreshUsbAsync();
            var wheel = devices.FirstOrDefault(d => d.HasFFB && !d.ProductName.Contains("vJoy", StringComparison.OrdinalIgnoreCase));
            TxtWheelStatus.Text = wheel?.InstanceName ?? "Volante FFB não detectado";
            TxtWheelStatus.Foreground = wheel == null ? System.Windows.Media.Brushes.Gray : System.Windows.Media.Brushes.SkyBlue;
            var extra = devices.FirstOrDefault(d => _profiles.Any(p => p.InstanceGuid == d.InstanceGuid && p.Buttons?.Count > 0));
            TxtShifterStatus.Text = extra?.InstanceName ?? "Câmbio/button box não detectado";
            TxtVirtualStatus.Text = "Verificado ao iniciar";
            TxtConsole.Text = "Dispositivos atualizados. Inicie uma sessão para verificar o vJoy.";
        }
        catch (Exception ex) { LogTrace(ex.ToString()); TxtConsole.Text = ex.Message; }
    }

    private async void VirtualStatus_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_calibrating || _isStarting || _isDetecting || _setupBusy || _refreshingVjoy || _closing) return;
        _isStarting = true;
        try { await StopFeedingAsync(); await InitializeDevicesAsync(); await RefreshVjoySetupAsync(); }
        finally { _isStarting = false; UpdateSetupControls(); }
    }

    private async Task RefreshVjoySetupAsync()
    {
        if (_refreshingVjoy || _closing) return;
        _refreshingVjoy = true;
        BtnRefreshVjoy.IsEnabled = false;
        BtnPrepareVjoy.IsEnabled = false;
        try
        {
            _vjoyInspection = await VJoySetupService.InspectAsync();
            if (_closing) return;
            TxtVjoySetup.Text = _vjoyInspection.Summary;
            TxtVirtualStatus.Text = _vjoyInspection.ConfigurationReady ? "Preparado" : "Preparação pendente";
            BtnPrepareVjoy.Content = !_vjoyInspection.Installed || !File.Exists(VJoySetupService.ConfigurationTool)
                ? "Instalar e preparar" : "Preparar volante virtual";
        }
        catch (Exception ex) { LogTrace(ex.ToString()); if (!_closing) TxtVjoySetup.Text = ex.Message; }
        finally
        {
            _refreshingVjoy = false;
            UpdateSetupControls();
        }
    }

    private void UpdateSetupControls()
    {
        bool available = !_closing && !_setupBusy && !_refreshingVjoy && !_isStarting && !_isFeeding && !_isDetecting;
        UpdatePresentation();
        BtnRefreshVjoy.IsEnabled = available;
        BtnPrepareVjoy.IsEnabled = available && _vjoyInspection is { ConfigurationReady: false, Busy: false };
    }

    private async void BtnRefreshVjoy_Click(object sender, RoutedEventArgs e)
    {
        if (_preview) { TxtConsole.Text = "Prévia: verificação simulada."; return; }
        if (_setupBusy || _isFeeding || _isStarting || _isDetecting || _closing) return;
        await RefreshVjoySetupAsync();
    }

    private async void BtnPrepareVjoy_Click(object sender, RoutedEventArgs e)
    {
        if (_preview) { TxtConsole.Text = "Prévia: nenhum driver será instalado."; return; }
        if (_setupBusy || _refreshingVjoy || _isFeeding || _isStarting || _isDetecting || _closing) return;
        await EnsureVirtualDeviceAsync(force: true);
    }

    private async Task<bool> EnsureVirtualDeviceAsync(bool force = false)
    {
        if (_closing) return false;
        _setupBusy = true;
        BtnToggleFeeder.IsEnabled = false;
        UpdateSetupControls();
        try
        {
            TxtVjoySetup.Text = "Ativando e preparando o volante virtual automaticamente. Autorize a operação no Windows...";
            await _virtualDeviceLifetime.EnsureAsync(force);
            await RefreshVjoySetupAsync();
            TxtVjoySetup.Text = "Volante virtual ativo e configurado. Será desativado ao fechar a aplicação.";
            _sessionError = null;
            return true;
        }
        catch (Exception ex)
        {
            LogTrace(ex.ToString());
            await RefreshVjoySetupAsync();
            TxtVjoySetup.Text = ex.Message;
            TxtConsole.Text = ex.Message;
            _sessionError = ex.Message;
            return false;
        }
        finally
        {
            _setupBusy = false;
            BtnToggleFeeder.IsEnabled = !_closing && !_isStarting;
            UpdateSetupControls();
        }
    }

    private FFBConfiguration CaptureFfbConfiguration()
    {
        var config = DirectInputFFBBridge.FromProfile(_profiles.FirstOrDefault(p => p.FFBParameters != null)?.FFBParameters);
        config.MasterGain = SlMasterGain.Value;
        config.ConstantForceScale = SlConstantForce.Value;
        config.MinimumForce = SlMinimumForce.Value;
        config.FilterThreshold = SlFilterThreshold.Value;
        config.MinimumCoefficient = SlMinCoeff.Value;
        config.DamperCoefficient = SlDamper.Value;
        config.SpringCoefficient = SlSpring.Value;
        config.GearShiftStrength = SlGearShift.Value;
        config.EngineVibrationStrength = SlEngineVib.Value;
        config.InvertConstantForce = ChkInvertFFB.IsChecked == true;
        return config;
    }

    private async void BtnToggleFeeder_Click(object sender, RoutedEventArgs e)
    {
        if (_preview) { _previewRunning = !_previewRunning; UpdatePresentation(); return; }
        if (_calibrating || _isStarting || _isDetecting || _setupBusy || _refreshingVjoy || _closing) return;
        if (_session == null && !_profiles.Any(p => p.Axes?.Count > 0 || p.Buttons?.Count > 0 || p.DPad != null)) { SelectPage(1); return; }
        _sessionError = null;
        _isStarting = true;
        UpdatePresentation();
        BtnToggleFeeder.IsEnabled = false;
        try
        {
            if (_session != null) { await StopFeedingAsync(); return; }
            if (!await EnsureVirtualDeviceAsync()) return;
            SyncAxisInversions();
            if (HidHideService.IsInstalled)
            {
                TxtConsole.Text = "Ocultando dispositivos físicos com HidHide...";
                await HidHideService.ConfigureCloakingAsync(_profiles);
            }
            TxtConsole.Text = "Adquirindo dispositivos e verificando FFB...";
            var session = new WheelSession(_profiles, _mainWindowHandle, CaptureFfbConfiguration());
            _session = session;
            await session.Started;
            if (_closing) return;
            _profiles = session.ResolvedProfiles;
            _ffbTarget = session.FfbDevice;
            UpdateMappingLabels();
            _isFeeding = true;
            UpdateSetupControls();
            TxtVirtualStatus.Text = "Entradas ativas";
            TxtConsole.Text = session.Telemetry.Status;
            _ = ObserveSessionAsync(session);
        }
        catch (Exception ex)
        {
            LogTrace(ex.ToString());
            await StopFeedingAsync();
            if (!_closing) { _sessionError = "Falha ao iniciar: " + ex.Message; TxtConsole.Text = _sessionError; }
        }
        finally
        {
            _isStarting = false;
            BtnToggleFeeder.IsEnabled = !_closing;
            UpdateSetupControls();
        }
    }

    private async Task ObserveSessionAsync(WheelSession session)
    {
        await session.Completion;
        if (!ReferenceEquals(_session, session) || _closing) return;
        await StopFeedingAsync();
        TxtConsole.Text = session.Telemetry.Status;
        _sessionError = session.Telemetry.Status;
        TxtVirtualStatus.Text = "Sessão interrompida";
        UpdatePresentation();
    }

    private async Task StopFeedingAsync()
    {
        var session = _session;
        try
        {
            if (session != null) await session.StopAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("StopFeeding", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_session, session)) _session = null;
            if (HidHideService.IsInstalled)
            {
                try { await HidHideService.UncloakAsync(_profiles); } catch { }
            }
        }
        _isFeeding = false;
        TxtVirtualStatus.Text = _virtualDeviceLifetime.IsActive ? "Preparado (sem transmissão)" : "Parado";
        UpdateSetupControls();
        _lastPosition = InputAggregator.NeutralPosition();
        _currentFfbMagnitude = 0;
        PbFfbForce.Value = 0;
        TxtFfbForceValue.Text = "0%";
        ResetLiveMetrics();
        TxtConsole.Text = "Sessão parada. Controles neutros e dispositivos liberados.";
    }

    public async Task HandleFailureAsync(Exception exception)
    {
        LogTrace(exception.ToString());
        await StopFeedingAsync();
        TxtConsole.Text = "Sessão interrompida: " + exception.Message;
        _sessionError = TxtConsole.Text;
        UpdatePresentation();
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (_isStarting && _session != null)
        {
            TxtConsole.Text = _session.Telemetry.Status;
            return;
        }
        if (!_isFeeding || _session == null) return;
        UpdatePresentation();
        var telemetry = _session.Telemetry;
        UpdateFfbMonitor(telemetry.Ffb);
        _lastPosition = telemetry.Position;
        _currentFfbMagnitude = telemetry.Force;
        TxtConsole.Text = $"{telemetry.Status} | {telemetry.FrequencyHz:F0} Hz medidos | maior intervalo: {telemetry.MaxIntervalMs:F2} ms";

        PbSteering.Value = Math.Clamp(_lastPosition.AxisX, 1, 32768);
        PbThrottle.Value = Math.Clamp(_lastPosition.AxisZ, 1, 32768);
        PbBrake.Value = Math.Clamp(_lastPosition.AxisXRot, 1, 32768);
        PbClutch.Value = Math.Clamp(_lastPosition.AxisYRot, 1, 32768);
        PbHandbrake.Value = Math.Clamp(_lastPosition.AxisZRot, 1, 32768);

        UpdateLiveMetrics(telemetry.FrequencyHz, telemetry.MaxIntervalMs);

        // FFB live telemetry
        int absFfb = Math.Abs(_currentFfbMagnitude);
        PbFfbForce.Value = absFfb;
        TxtFfbForceValue.Text = $"{absFfb / 100}%";

        // Gear display calculation
        string gear = "N";
        uint btns = _lastPosition.Buttons;

        if ((btns & (1 << 17)) != 0) gear = "▲"; // Shift Up
        else if ((btns & (1 << 16)) != 0) gear = "▼"; // Shift Down
        else if ((btns & (1 << 18)) != 0) gear = "R";
        else if ((btns & (1 << 19)) != 0) gear = "1";
        else if ((btns & (1 << 20)) != 0) gear = "2";
        else if ((btns & (1 << 21)) != 0) gear = "3";
        else if ((btns & (1 << 22)) != 0) gear = "4";
        else if ((btns & (1 << 23)) != 0) gear = "5";
        else if ((btns & (1 << 24)) != 0) gear = "6";
        else if ((btns & (1 << 25)) != 0) gear = "7";

        TxtCurrentGear.Text = gear;

        if (btns != 0)
        {
            var activeNames = new List<string>();
            if ((btns & (1 << 26)) != 0) activeNames.Add("Handbrake");
            if ((btns & (1 << 17)) != 0) activeNames.Add("Shift Up");
            if ((btns & (1 << 16)) != 0) activeNames.Add("Shift Down");
            if ((btns & (1 << 2)) != 0) activeNames.Add("A");
            if ((btns & (1 << 3)) != 0) activeNames.Add("B");
            if ((btns & (1 << 4)) != 0) activeNames.Add("X");
            if ((btns & (1 << 5)) != 0) activeNames.Add("Y");
            if ((btns & (1 << 0)) != 0) activeNames.Add("View");
            if ((btns & (1 << 1)) != 0) activeNames.Add("Menu");
            if ((btns & (1 << 6)) != 0) activeNames.Add("LB");
            if ((btns & (1 << 8)) != 0) activeNames.Add("LSB");
            if ((btns & (1 << 9)) != 0) activeNames.Add("RSB");
            if ((btns & (1 << 10)) != 0) activeNames.Add("Buzina");
            if ((btns & (1 << 11)) != 0) activeNames.Add("Telemetria");
            if (activeNames.Count > 0)
                TxtActiveButtons.Text = string.Join(", ", activeNames);
            else
                TxtActiveButtons.Text = $"Botões ativos: 0x{btns:X8}";
        }
        else
        {
            TxtActiveButtons.Text = "Nenhum botão pressionado";
        }
    }

    private void FfbSlider_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (TxtMasterGainVal == null || TxtConstantForceVal == null || TxtMinimumForceVal == null ||
            TxtFilterThresholdVal == null || TxtMinCoeffVal == null || TxtDamperVal == null ||
            TxtSpringVal == null || TxtGearShiftVal == null || TxtEngineVibVal == null)
            return;

        TxtMasterGainVal.Text = $"{(int)(SlMasterGain.Value * 100)}%";
        TxtConstantForceVal.Text = $"{SlConstantForce.Value:F1}x";
        TxtMinimumForceVal.Text = $"{(int)(SlMinimumForce.Value * 100)}%";
        TxtFilterThresholdVal.Text = $"{(int)(SlFilterThreshold.Value * 100)}%";
        TxtMinCoeffVal.Text = $"{(int)(SlMinCoeff.Value * 100)}%";
        TxtDamperVal.Text = $"{(int)(SlDamper.Value * 100)}%";
        TxtSpringVal.Text = $"{(int)(SlSpring.Value * 100)}%";
        TxtGearShiftVal.Text = $"{(int)(SlGearShift.Value * 100)}%";
        TxtEngineVibVal.Text = $"{(int)(SlEngineVib.Value * 100)}%";

        _session?.UpdateConfiguration(CaptureFfbConfiguration());
        if (IsLoaded && !_loadingConfiguration) _viewModel.IsDirty = true;
    }

    private async Task RunMotorTestAsync(MotorTest type)
    {
        if (_motorTestRunning) { TxtConsole.Text = "Aguarde o teste atual terminar."; return; }
        if (_session == null || !_isFeeding || !_session.HasFfb)
        {
            TxtConsole.Text = "Inicie uma sessão com FFB disponível antes do teste.";
            return;
        }
        _motorTestRunning = true; UpdatePresentation();
        try { await _session.TestAsync(type); TxtConsole.Text = "Teste concluído."; }
        catch (OperationCanceledException) { TxtConsole.Text = "Teste cancelado; sessão parada."; }
        catch (Exception ex) { await HandleFailureAsync(ex); }
        finally { _motorTestRunning = false; UpdatePresentation(); }
    }
    private async void BtnTestMotorFfb_Click(object sender, RoutedEventArgs e) => await RunMotorTestAsync(MotorTest.Rack);
    private async void BtnTestShiftFfb_Click(object sender, RoutedEventArgs e) => await RunMotorTestAsync(MotorTest.Shift);
    private async void BtnTestEngineFfb_Click(object sender, RoutedEventArgs e) => await RunMotorTestAsync(MotorTest.Engine);

    #region Detecção Interativa de Eixos e Botões (Estilo EmuWheel Original)

    private string GetFriendlyAxisName(string axisId) => axisId switch
    {
        "Steering" => "Volante (Direção)",
        "Throttle" => "Acelerador",
        "Brake" => "Freio",
        "Clutch" => "Embreagem",
        "Handbrake" => "Freio de Mão",
        _ => axisId
    };

    private string GetButtonFriendlyName(int buttonId) => buttonId switch
    {
        0 => "View / Mapa (Alternar Câmera)",
        1 => "Menu / Pausar Jogo",
        2 => "Botão A (Confirmar)",
        3 => "Botão B (Voltar)",
        4 => "Botão X (Embreagem)",
        5 => "Botão Y (Rebobinar / Rewind)",
        6 => "LB",
        7 => "RB",
        8 => "LSB / L3 (Analógico Esq)",
        9 => "RSB / R3 (Analógico Dir)",
        10 => "Buzina (Horn)",
        11 => "Telemetria (Telemetry)",
        12 => "Olhar para Esquerda",
        13 => "Olhar para Direita",
        14 => "Forza LINK / Anna",
        15 => "Modo Fotografia (Photo Mode)",
        16 => "Reduzir Marcha (Shift Down)",
        17 => "Subir Marcha (Shift Up)",
        18 => "Marcha Ré (R)",
        19 => "1ª Marcha",
        20 => "2ª Marcha",
        21 => "3ª Marcha",
        22 => "4ª Marcha",
        23 => "5ª Marcha",
        24 => "6ª Marcha",
        25 => "7ª Marcha",
        26 => "Freio de Mão (Botão Digital)",
        27 => "Botão Extra 28",
        28 => "Botão Extra 29",
        29 => "Botão Extra 30",
        30 => "Botão Extra 31",
        31 => "Botão Extra 32",
        32 => "D-Pad Cima (Up)",
        33 => "D-Pad Baixo (Down)",
        34 => "D-Pad Esquerda (Left)",
        35 => "D-Pad Direita (Right)",
        _ => $"Botão {buttonId}"
    };

    private bool CanEdit()
    {
        if (!_isFeeding && !_isStarting && !_isDetecting && !_setupBusy && !_calibrating && !_closing) return true;
        TxtMappingStatus.Text = "Pare a sessão e aguarde a operação atual antes de editar o perfil.";
        return false;
    }
    private sealed record Detection(Guid Guid, Guid ProductGuid, string Name, string ProductName, int Index);

    private async Task DetectAsync(string? axisId, int buttonId = -1)
    {
        if (!CanEdit()) return;
        _isDetecting = true;
        _detectCancel = new CancellationTokenSource();
        var token = _detectCancel.Token;
        TxtMappingTitle.Text = axisId == null ? $"Detectando {GetButtonFriendlyName(buttonId)}" : $"Detectando {GetFriendlyAxisName(axisId)}";
        TxtMappingStatus.Text = "Mova o eixo ou pressione e solte o botão físico.";
        BtnCancelDetect.Visibility = Visibility.Visible;
        UpdatePresentation();
        var hwnd = _mainWindowHandle;
        var work = Task.Run(() =>
        {
            using var manager = new DirectInputDeviceManager();
            var devices = manager.EnumerateDevices().Where(d => !d.ProductName.Contains("vJoy", StringComparison.OrdinalIgnoreCase)).ToList();
            var baselines = new Dictionary<Guid, PolledInputState>();
            foreach (var d in devices)
            {
                token.ThrowIfCancellationRequested();
                if (manager.AcquireDevice(d.InstanceGuid, hwnd) == null)
                    throw new IOException($"Não foi possível abrir {d.InstanceName}. Feche outros feeders.");
            }
            if (token.WaitHandle.WaitOne(150)) token.ThrowIfCancellationRequested();
            foreach (var d in devices)
            {
                var state = new PolledInputState();
                if (!manager.PollDevice(d.InstanceGuid, state)) throw new IOException($"Sem leitura de {d.InstanceName}.");
                baselines[d.InstanceGuid] = state;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var current = new PolledInputState();
            (Guid Guid, int Index) candidate = default;
            int confirmations = 0;
            while (watch.Elapsed < TimeSpan.FromSeconds(30))
            {
                token.ThrowIfCancellationRequested();
                bool motion = false;
                foreach (var d in devices)
                {
                    if (!manager.PollDevice(d.InstanceGuid, current)) throw new IOException($"Leitura perdida: {d.InstanceName}.");
                    var baseline = baselines[d.InstanceGuid];
                    int found = -1;
                    if (axisId != null)
                    {
                        for (int i = 0; i < 8; i++)
                            if (Math.Abs(current.Axes[i] - baseline.Axes[i]) > 12000) { found = i; break; }
                        if (found >= 0)
                        {
                            motion = true;
                            if (candidate == (d.InstanceGuid, found)) confirmations++;
                            else { candidate = (d.InstanceGuid, found); confirmations = 1; }
                            if (confirmations < 3) break;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < 128; i++)
                            if (current.Buttons[i] && !baseline.Buttons[i]) { found = i; break; }
                        Array.Copy(current.Buttons, baseline.Buttons, 128);
                    }
                    if (found >= 0)
                        return new Detection(d.InstanceGuid, d.ProductGuid, d.InstanceName, d.ProductName, found);
                }
                if (axisId != null && !motion) confirmations = 0;
                if (token.WaitHandle.WaitOne(10)) token.ThrowIfCancellationRequested();
            }
            return null;
        }, token);
        // Track the entire operation including the UI continuation before closing.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _detectionTask = finished.Task;
        try
        {
            var detected = await work;
            token.ThrowIfCancellationRequested();
            if (_closing) return;
            if (detected == null) { TxtMappingStatus.Text = "Tempo esgotado sem detectar entrada."; return; }
            SyncAxisInversions();
            RememberMappings();
            var profile = _profiles.FirstOrDefault(p => p.InstanceGuid == detected.Guid);
            if (profile == null)
            {
                profile = new ControllerProfile { InstanceGuid = detected.Guid, ProductGuid = detected.ProductGuid,
                    InstanceName = detected.Name, ProductName = detected.ProductName };
                _profiles.Add(profile);
            }
            if (axisId != null)
            {
                foreach (var p in _profiles) p.Axes?.RemoveAll(a => a.Id == axisId);
                (profile.Axes ??= new()).Add(new AxisMapping { Id = axisId, AxisIndex = detected.Index });
            }
            else
            {
                foreach (var p in _profiles) p.Buttons?.RemoveAll(b => b.Id == buttonId);
                (profile.Buttons ??= new()).Add(new ButtonMapping { Id = buttonId, Index = detected.Index });
            }
            ProfileManager.SaveToFile(_configPath, _profiles);
            UpdateMappingLabels();
            TxtMappingStatus.Text = $"Mapeado: {detected.Name}, entrada {detected.Index}.";
        }
        catch (OperationCanceledException) { if (!_closing) TxtMappingStatus.Text = "Detecção cancelada."; }
        catch (Exception ex) { LogTrace(ex.ToString()); if (!_closing) TxtMappingStatus.Text = ex.Message; }
        finally
        {
            _detectCancel.Dispose(); _detectCancel = null;
            _isDetecting = false;
            BtnCancelDetect.Visibility = Visibility.Collapsed;
            UpdatePresentation();
            finished.TrySetResult();
        }
    }
    private void BtnCancelDetect_Click(object sender, RoutedEventArgs e) => _detectCancel?.Cancel();

    #endregion

    private void BtnReloadConfig_Click(object sender, RoutedEventArgs e)
    {
        if (_preview) return;
        if (CanEdit()) { LoadConfiguration(); _viewModel.IsDirty = false; }
    }

    private void SyncAxisInversions()
    {
        foreach (var row in _viewModel.Mappings.Where(r => r.IsAxis))
            foreach (var p in _profiles)
                foreach (var axis in p.Axes ?? new())
                    if (axis.Id == row.AxisId) axis.Inverted = row.Inverted ? 1 : 0;
    }

    private void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        if (_preview) { TxtConsole.Text = "Prévia: alterações simuladas, sem gravar o perfil."; _viewModel.IsDirty = false; return; }
        if (_setupBusy || _isStarting || _isDetecting || _closing) return;
        try
        {
            SyncAxisInversions();
            var wheel = _profiles.FirstOrDefault(p => p.InstanceGuid == _ffbTarget)
                ?? _profiles.FirstOrDefault(p => p.FFBParameters != null)
                ?? _profiles.FirstOrDefault(p => p.Axes?.Any(a => a.Id == "Steering") == true);
            if (wheel != null)
            {
                var f = wheel.FFBParameters ??= new();
                f.InvertConstantForce = ChkInvertFFB.IsChecked == true;
                var c = f.Const ??= new();
                c.MaximumForce = SlMasterGain.Value; c.Magnitude = SlConstantForce.Value;
                c.MinimumForce = SlMinimumForce.Value; c.FilterThreshold = SlFilterThreshold.Value; c.MinimumCoefficient = SlMinCoeff.Value;
                (f.Damper ??= new()).Coefficient = SlDamper.Value;
                (f.Spring ??= new()).Coefficient = SlSpring.Value;
                f.Sine ??= new();
                (f.Sine.GearShiftVibrations ??= new() { Frequency = 1 }).Strength = SlGearShift.Value;
                (f.Sine.EngineVibrations ??= new() { Frequency = 0.3 }).Strength = SlEngineVib.Value;
            }
            ProfileManager.SaveToFile(_configPath, _profiles);
            _viewModel.IsDirty = false;
            TxtConsole.Text = "Perfil salvo. Seus ajustes estarão disponíveis na próxima sessão.";
        }
        catch (Exception ex) { LogTrace(ex.ToString()); MessageBox.Show(ex.Message, "Erro ao salvar"); }
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_canClose) return;
        e.Cancel = true;
        if (_calibrating) { TxtConsole.Text = "Conclua ou feche a operação atual antes de sair."; return; }
        if (_setupBusy)
        {
            TxtConsole.Text = "Preparação do driver em andamento. Aguarde a conclusão ou cancele a autorização do Windows.";
            return;
        }
        if (_closing) return;
        if (_viewModel.IsDirty)
        {
            var choice = MessageBox.Show("Deseja salvar as alterações no seu perfil antes de sair?", "Alterações não salvas", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes)
            {
                BtnSaveConfig_Click(this, new());
                if (_viewModel.IsDirty) return;
            }
        }
        _closing = true;
        _uiTimer.Stop();
        _detectCancel?.Cancel();
        await _detectionTask;
        await StopFeedingAsync();
        if (HidHideService.IsInstalled)
        {
            try { await HidHideService.UncloakAsync(_profiles); } catch { }
        }
        var stopped = await _virtualDeviceLifetime.StopAsync();
        if (!stopped.Success)
        {
            LogTrace(stopped.Message);
            MessageBox.Show(stopped.Message, "Parada do volante virtual", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _canClose = true;
        LaunchPendingUpdate();
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
