using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ForzaNext.Core;
using ForzaNext.Core.Config;
using ForzaNext.Core.Devices;
using ForzaNext.Core.FFB;
using ForzaNext.Core.Virtual;

namespace ForzaNext.UI;

public partial class MainWindow : Window
{
    private readonly string _configPath;
    private List<ControllerProfile> _profiles = new();
    private DirectInputDeviceManager? _deviceManager;
    private VirtualWheelDriver? _virtualDriver;
    private InputAggregator? _aggregator;
    private DirectInputFFBBridge? _ffbBridge;

    private Thread? _feedThread;
    private CancellationTokenSource? _feedCancel;
    private bool _isFeeding = false;

    private readonly DispatcherTimer _uiTimer = new();
    private JoystickPositionV2 _lastPosition;

    public MainWindow()
    {
        InitializeComponent();

        _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configuration.json");
        if (!File.Exists(_configPath))
        {
            _configPath = @"C:\forza-wheel-next\configuration.json";
        }
        if (!File.Exists(_configPath))
        {
            _configPath = @"C:\forza_emuwheel_v1.4a\configuration.json";
        }

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadConfiguration();
        InitializeDevices();

        _uiTimer.Interval = TimeSpan.FromMilliseconds(33); // ~30 fps UI refresh
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();
    }

    private void LoadConfiguration()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                _profiles = ProfileManager.LoadFromFile(_configPath);
                TxtConsole.Text = $"Configuração carregada com {_profiles.Count} dispositivos.";

                // Set initial checkboxes from profile
                var wheel = _profiles.FirstOrDefault(p => p.ProductName.Contains("ODrive", StringComparison.OrdinalIgnoreCase));
                if (wheel?.Axes != null)
                {
                    var steerAxis = wheel.Axes.FirstOrDefault(a => a.Id == "Steering");
                    if (steerAxis != null) ChkInvertSteering.IsChecked = (steerAxis.Inverted == 1);

                    var thrAxis = wheel.Axes.FirstOrDefault(a => a.Id == "Throttle");
                    if (thrAxis != null) ChkInvertThrottle.IsChecked = (thrAxis.Inverted == 1);

                    var brkAxis = wheel.Axes.FirstOrDefault(a => a.Id == "Brake");
                    if (brkAxis != null) ChkInvertBrake.IsChecked = (brkAxis.Inverted == 1);
                }
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
    }

    private void InitializeDevices()
    {
        try
        {
            _deviceManager = new DirectInputDeviceManager();
            var devices = _deviceManager.EnumerateDevices();

            var odrive = devices.FirstOrDefault(d => d.ProductName.Contains("ODrive", StringComparison.OrdinalIgnoreCase));
            if (odrive != null)
                TxtWheelStatus.Text = $"{odrive.InstanceName} (Conectado)";
            else
                TxtWheelStatus.Text = "ODrive (Não detectado)";

            var shifter = devices.FirstOrDefault(d => d.ProductName.Contains("Shifter", StringComparison.OrdinalIgnoreCase));
            if (shifter != null)
                TxtShifterStatus.Text = $"{shifter.InstanceName} (Conectado)";
            else
                TxtShifterStatus.Text = "Shifter (Não detectado)";

            _virtualDriver = new VirtualWheelDriver();
            if (_virtualDriver.Initialize())
            {
                TxtVirtualStatus.Text = "Driver Virtual Ativo (Pronto)";
                TxtVirtualStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            }
            else
            {
                TxtVirtualStatus.Text = "Driver Virtual Inativo";
                TxtVirtualStatus.Foreground = System.Windows.Media.Brushes.IndianRed;
            }
        }
        catch (Exception ex)
        {
            TxtConsole.Text = $"Erro na inicialização de dispositivos: {ex.Message}";
        }
    }

    private void BtnToggleFeeder_Click(object sender, RoutedEventArgs e)
    {
        if (!_isFeeding)
            StartFeeding();
        else
            StopFeeding();
    }

    private void StartFeeding()
    {
        if (_deviceManager == null || _virtualDriver == null)
        {
            MessageBox.Show("Dispositivos não inicializados.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Acquire controllers
        Vortice.DirectInput.IDirectInputDevice8? odriveDev = null;
        foreach (var p in _profiles)
        {
            var dev = _deviceManager.AcquireDevice(p.InstanceGuid, IntPtr.Zero);
            if (p.ProductName.Contains("ODrive", StringComparison.OrdinalIgnoreCase) && dev != null)
            {
                odriveDev = dev;
            }
        }

        // Initialize FFB Bridge if ODrive is available
        if (odriveDev != null)
        {
            _ffbBridge = new DirectInputFFBBridge(odriveDev);
            if (_ffbBridge.Initialize())
            {
                TxtConsole.Text = "[FFB] Force Feedback Bridge ativo no ODrive!";
            }
        }

        // Hook virtual driver FFB packets
        if (_virtualDriver != null)
        {
            _virtualDriver.FfbDataReceived += (packetType, dataPtr, size) =>
            {
                if (packetType == FfbPacketType.PT_CONSTREP && _ffbBridge != null && dataPtr != IntPtr.Zero)
                {
                    try
                    {
                        // Constant force magnitude: short (-10000..10000)
                        short mag = System.Runtime.InteropServices.Marshal.ReadInt16(dataPtr);
                        double norm = mag / 10000.0;
                        _ffbBridge.ApplyForce(norm);
                    }
                    catch { }
                }
            };
        }

        _aggregator = new InputAggregator(_profiles);
        _feedCancel = new CancellationTokenSource();
        var token = _feedCancel.Token;

        _feedThread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                // Poll each physical device
                foreach (var p in _profiles)
                {
                    var buffer = _aggregator.GetStateBuffer(p.InstanceGuid);
                    _deviceManager.PollDevice(p.InstanceGuid, buffer);
                }

                // Aggregate into vJoy position
                var pos = _aggregator.Aggregate();
                _lastPosition = pos;

                // Send to virtual wheel
                _virtualDriver.Feed(ref pos);

                Thread.Sleep(1); // 1000 Hz loop
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };

        _feedThread.Start();
        _isFeeding = true;

        BtnToggleFeeder.Background = System.Windows.Media.Brushes.IndianRed;
        TxtFeederBtn.Text = "■ PARAR FEEDER";
        TxtConsole.Text = "[EM EXECUÇÃO] Feeder ativo a 1000 Hz. Abra o Forza e jogue!";
    }

    private void StopFeeding()
    {
        _feedCancel?.Cancel();
        _feedThread?.Join(500);
        _isFeeding = false;

        BtnToggleFeeder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
        TxtFeederBtn.Text = "▶ INICIAR FEEDER";
        TxtConsole.Text = "[PARADO] Feeder pausado.";
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isFeeding) return;

        PbSteering.Value = _lastPosition.AxisX;
        PbThrottle.Value = _lastPosition.AxisY;
        PbBrake.Value = _lastPosition.AxisZ;
        PbClutch.Value = _lastPosition.AxisXRot;

        // Gear display calculation
        string gear = "N";
        uint btns = _lastPosition.Buttons;
        if ((btns & (1 << 19)) != 0) gear = "1";
        else if ((btns & (1 << 20)) != 0) gear = "2";
        else if ((btns & (1 << 21)) != 0) gear = "3";
        else if ((btns & (1 << 22)) != 0) gear = "4";
        else if ((btns & (1 << 23)) != 0) gear = "5";
        else if ((btns & (1 << 24)) != 0) gear = "6";
        else if ((btns & (1 << 25)) != 0) gear = "7";
        else if ((btns & (1 << 18)) != 0) gear = "R";

        TxtCurrentGear.Text = gear;

        if (btns != 0)
            TxtActiveButtons.Text = $"Botões ativos: 0x{btns:X8}";
        else
            TxtActiveButtons.Text = "Nenhum botão pressionado";
    }

    private void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var wheel = _profiles.FirstOrDefault(p => p.ProductName.Contains("ODrive", StringComparison.OrdinalIgnoreCase));
            if (wheel?.Axes != null)
            {
                var steerAxis = wheel.Axes.FirstOrDefault(a => a.Id == "Steering");
                if (steerAxis != null) steerAxis.Inverted = ChkInvertSteering.IsChecked == true ? 1 : 0;

                var thrAxis = wheel.Axes.FirstOrDefault(a => a.Id == "Throttle");
                if (thrAxis != null) thrAxis.Inverted = ChkInvertThrottle.IsChecked == true ? 1 : 0;

                var brkAxis = wheel.Axes.FirstOrDefault(a => a.Id == "Brake");
                if (brkAxis != null) brkAxis.Inverted = ChkInvertBrake.IsChecked == true ? 1 : 0;
            }

            ProfileManager.SaveToFile(_configPath, _profiles);
            MessageBox.Show("Configurações salvas com sucesso!", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
            TxtConsole.Text = "Configurações gravadas em configuration.json";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao salvar: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnReloadConfig_Click(object sender, RoutedEventArgs e)
    {
        LoadConfiguration();
        MessageBox.Show("Configurações recarregadas com sucesso!", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnRegisterGameInput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool ok = GameInputConfigurator.RegisterForzaRacingWheel(900);
            if (ok)
            {
                MessageBox.Show("Perfil canônico do volante (VID 1234, PID BEAD) registrado com sucesso no Windows!\n\nAgora o Forza reconhecerá seu volante e pedais como volante oficial.", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtConsole.Text = "[REGISTRO] Perfil do volante aplicado no GameInput com sucesso.";
            }
            else
            {
                MessageBox.Show("Não foi possível gravar a chave de registro. Execute o ForzaNextWheel como Administrador.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao registrar: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        StopFeeding();
        _ffbBridge?.Dispose();
        _deviceManager?.Dispose();
        _virtualDriver?.Dispose();
    }
}
