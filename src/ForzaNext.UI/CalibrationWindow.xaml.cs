using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ForzaNext.Core;
using ForzaNext.Core.Config;
using ForzaNext.Core.Devices;

namespace ForzaNext.UI;

public partial class CalibrationWindow : Window
{
    private readonly ControllerProfile _profile;
    private readonly AxisMapping _axis;
    private readonly InputCaptureSession _capture = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private int _step, _minimum = 65535, _maximum, _center = 32768, _raw;
    private bool _closing, _canClose, _accepted, _inverted;
    private long _sequence, _lastRead;
    public AxisCalibration? Result { get; private set; }
    public double ResultDeadzone => Deadzone.Value;
    public bool ResultInverted => _inverted;
    private bool Steering => _axis.Id == "Steering";
    public CalibrationWindow(Window? owner, ControllerProfile profile, AxisMapping axis, string title, bool preview = false)
    {
        if (owner != null) Owner = owner; _profile = profile; _axis = axis; InitializeComponent(); DialogTheme.Apply(this);
        Heading.Text = "Calibrar · " + title; Deadzone.Value = Math.Clamp(axis.Deadzone, 0, .2);
        if (preview)
        {
            Instruction.Text = "Gire o volante até os dois limites de uso."; Position.Value = 42000;
            Reading.Text = "Entrada física: 42000 · ODrive-Wheel HID"; Limits.Text = "Mínimo: 1024     Centro: —     Máximo: 62000";
            Status.Text = "Prévia · dados simulados · nenhum dispositivo foi aberto."; return;
        }
        _timer.Tick += Tick; Loaded += Start; Closing += Stop;
    }
    private async void Start(object sender, RoutedEventArgs e)
    {
        try
        {
            await _capture.StartAsync(new(_profile.InstanceGuid, _profile.ProductGuid));
            if (_closing) return;
            Instruction.Text = Steering ? "Gire o volante até os dois limites de uso." : "Solte e pressione o controle até o fim do curso.";
            Next.IsEnabled = true; _lastRead = Environment.TickCount64; _timer.Start();
        }
        catch (Exception ex) { Status.Text = ex.Message; Next.IsEnabled = false; }
    }
    private void Tick(object? sender, EventArgs e)
    {
        var sample = _capture.Latest;
        if (sample == null) return;
        if (sample.Sequence != _sequence) { _sequence = sample.Sequence; _lastRead = Environment.TickCount64; }
        bool fresh = _capture.IsConnected && Environment.TickCount64 - _lastRead < 2000;
        Next.IsEnabled = fresh;
        if (!fresh) { Status.Text = "Leitura interrompida. Reconecte o dispositivo e reinicie a calibração."; return; }
        _raw = sample.Axes[_axis.AxisIndex];
        if (_step == 0) { _minimum = Math.Min(_minimum, _raw); _maximum = Math.Max(_maximum, _raw); }
        Position.Value = _step == 2 ? (InputAggregator.ProcessAxisValue(_raw, _inverted, Deadzone.Value, Steering, Result) - 1) * 65535.0 / 32767 : _raw;
        Reading.Text = _step == 2 ? $"Saída calibrada: {Position.Value / 65535:P0}" : $"Entrada física: {_raw} · {sample.Name}";
        Limits.Text = $"Mínimo: {_minimum}     Centro: {(_step == 0 ? "—" : _center.ToString())}     Máximo: {_maximum}";
    }
    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 0)
        {
            if (_maximum - _minimum < 4096) { Status.Text = "Movimente o eixo por um curso maior antes de avançar."; return; }
            _step = 1; StepLabel.Text = "Etapa 2 de 3 · Posição de repouso";
            Instruction.Text = Steering ? "Centralize o volante e clique em Confirmar centro." : "Solte completamente o controle e confirme o repouso.";
            Next.Content = Steering ? "Confirmar centro" : "Confirmar repouso"; return;
        }
        if (_step == 1)
        {
            _center = Steering ? _raw : (_minimum + _maximum) / 2;
            _inverted = Steering ? _axis.Inverted == 1 : _raw > (_minimum + _maximum) / 2;
            Result = new() { Minimum = _minimum, Center = _center, Maximum = _maximum };
            try { Result.Validate(Steering); } catch (Exception ex) { Status.Text = ex.Message; return; }
            _step = 2; StepLabel.Text = "Etapa 3 de 3 · Conferência"; Instruction.Text = "Movimente o eixo e confira a saída calibrada antes de salvar.";
            Status.Text = "Os ajustes serão aplicados apenas ao perfil selecionado."; Next.Content = "Salvar calibração"; return;
        }
        _accepted = true; Close();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Deadzone_Changed(object sender, RoutedEventArgs e) { if (DeadzoneValue != null) DeadzoneValue.Text = $"{Deadzone.Value:P0}"; }
    private async void Stop(object? sender, CancelEventArgs e)
    {
        if (_canClose) return; e.Cancel = true;
        if (_closing) return; _closing = true; _timer.Stop(); Next.IsEnabled = false; Cancel.IsEnabled = false;
        try { await _capture.DisposeAsync(); }
        catch (Exception ex) { DiagnosticLog.Write("Calibration stop", ex.Message); _accepted = false; }
        _canClose = true;
        if (_accepted) DialogResult = true; else Close();
    }
}
