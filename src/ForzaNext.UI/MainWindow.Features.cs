using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using ForzaNext.Core;
using ForzaNext.Core.Config;
using ForzaNext.Core.Devices;
using ForzaNext.Core.Timing;
using ForzaNext.UI.Presentation;

namespace ForzaNext.UI;

public partial class MainWindow
{
    private GameProfileStore? _profileStore;
    private bool _loadingProfiles, _calibrating;
    private string? _pendingUpdate;
    private UpdateCheckResult? _latestUpdateCheck;
    private CancellationTokenSource? _updateDownloadCts;

    public async Task CheckForUpdatesAsync(bool userInitiated = true)
    {
        if (BtnCheckUpdates != null) BtnCheckUpdates.IsEnabled = false;
        if (userInitiated) TxtUpdateStatus.Text = "Consultando atualizações no GitHub Releases...";

        try
        {
            var current = typeof(MainWindow).Assembly.GetName().Version ?? new Version(2, 1, 0);
            _latestUpdateCheck = await GitHubUpdateService.CheckForUpdateAsync(current);

            if (_latestUpdateCheck.UpdateAvailable && _latestUpdateCheck.Manifest != null)
            {
                TxtUpdateStatus.Text = $"Nova versão v{_latestUpdateCheck.Manifest.Version} disponível! ({(_latestUpdateCheck.Manifest.Size / 1024.0 / 1024.0):F1} MB)";
                if (BtnApplyUpdate != null) BtnApplyUpdate.Visibility = Visibility.Visible;
                TxtConsole.Text = $"Atualização disponível: v{_latestUpdateCheck.Manifest.Version}. Acesse Ferramentas para atualizar.";
            }
            else if (_latestUpdateCheck.Error != null)
            {
                if (userInitiated) TxtUpdateStatus.Text = "Não foi possível verificar: " + _latestUpdateCheck.Error;
            }
            else
            {
                if (userInitiated) TxtUpdateStatus.Text = $"O ForzaNext Wheel já está atualizado (v{current.ToString(3)}).";
                if (BtnApplyUpdate != null) BtnApplyUpdate.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            if (userInitiated) TxtUpdateStatus.Text = "Falha ao consultar atualizações: " + ex.Message;
        }
        finally
        {
            if (BtnCheckUpdates != null) BtnCheckUpdates.IsEnabled = true;
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(userInitiated: true);
    }

    private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        // Se uma atualização já foi baixada e está pendente, executa diretamente
        if (_pendingUpdate != null && File.Exists(_pendingUpdate))
        {
            TxtUpdateStatus.Text = "Iniciando instalação da atualização...";
            _calibrating = false;
            Close();
            return;
        }

        if (_latestUpdateCheck == null || !_latestUpdateCheck.UpdateAvailable || _latestUpdateCheck.Manifest == null)
            return;

        if (!CanEdit() || !ConfirmProfileChanges()) return;

        var manifest = _latestUpdateCheck.Manifest;
        if (MessageBox.Show(this,
            $"Deseja baixar e instalar o ForzaNext Wheel v{manifest.Version}?\n\nO aplicativo fará o download oficial diretamente do GitHub Releases e será fechado para executar a instalação.\nTodos os seus perfis e configurações serão preservados.",
            "Confirmar Atualização",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _calibrating = true;
        UpdatePresentation();
        if (BtnApplyUpdate != null) BtnApplyUpdate.IsEnabled = false;
        if (BtnCheckUpdates != null) BtnCheckUpdates.IsEnabled = false;
        if (PbUpdateProgress != null)
        {
            PbUpdateProgress.Visibility = Visibility.Visible;
            PbUpdateProgress.Value = 0;
        }

        _updateDownloadCts?.Dispose();
        _updateDownloadCts = new CancellationTokenSource();

        try
        {
            TxtUpdateStatus.Text = $"Baixando ForzaNext Wheel v{manifest.Version}...";
            var current = typeof(MainWindow).Assembly.GetName().Version ?? new Version(2, 1, 0);

            var progress = new Progress<double>(percent =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (PbUpdateProgress != null) PbUpdateProgress.Value = percent * 100;
                    TxtUpdateStatus.Text = $"Baixando ForzaNext Wheel v{manifest.Version}... {(int)(percent * 100)}%";
                });
            });

            var prepared = await GitHubUpdateService.DownloadAndPrepareUpdateAsync(
                _latestUpdateCheck,
                current,
                Path.Combine(AppPaths.DataDirectory, "Updates"),
                progress,
                _updateDownloadCts.Token);

            if (PbUpdateProgress != null) PbUpdateProgress.Value = 100;
            TxtUpdateStatus.Text = "Download concluído! Fechando o ForzaNext Wheel para iniciar o instalador...";
            TxtConsole.Text = "Atualização pronta. Fechando o ForzaNext Wheel...";
            _pendingUpdate = prepared.Installer;

            if (BtnApplyUpdate != null)
            {
                BtnApplyUpdate.Content = "Instalar Agora";
                BtnApplyUpdate.IsEnabled = true;
            }

            // Libera calibrating antes de fechar para não barrar o fechamento no MainWindow_Closing
            _calibrating = false;
            UpdatePresentation();

            // Breve intervalo para visualização dos 100%
            await Task.Delay(350);

            Close();
        }
        catch (OperationCanceledException)
        {
            TxtUpdateStatus.Text = "Download da atualização cancelado.";
            if (BtnApplyUpdate != null) BtnApplyUpdate.IsEnabled = true;
            if (BtnCheckUpdates != null) BtnCheckUpdates.IsEnabled = true;
            if (PbUpdateProgress != null) PbUpdateProgress.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            TxtUpdateStatus.Text = "Falha no download da atualização: " + ex.Message;
            TxtConsole.Text = "Erro na atualização: " + ex.Message;
            LogTrace(ex.ToString());
            if (BtnApplyUpdate != null) BtnApplyUpdate.IsEnabled = true;
            if (BtnCheckUpdates != null) BtnCheckUpdates.IsEnabled = true;
            if (PbUpdateProgress != null) PbUpdateProgress.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _calibrating = false;
            UpdatePresentation();
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_preview) { TxtConsole.Text = "Prévia: nenhum instalador foi aberto."; return; }
        if (!CanEdit() || !ConfirmProfileChanges()) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Manifesto ForzaNext|release.json", Title = "Selecione release.json de um pacote de confiança" };
        if (picker.ShowDialog(this) != true) return;
        _calibrating = true; UpdatePresentation();
        PreparedUpdate? prepared = null;
        try
        {
            var current = typeof(MainWindow).Assembly.GetName().Version ?? new Version(2, 1, 0);
            prepared = await ReleasePackage.PrepareAsync(picker.FileName, current, Path.Combine(AppPaths.DataDirectory, "Updates"));
            if (MessageBox.Show(this, $"Instalar ForzaNext {prepared.Version}?\nO app será fechado para iniciar o instalador. Seus perfis serão mantidos.\n\nO checksum foi conferido. Use apenas pacotes de uma origem em que você confia.", "Atualizar ForzaNext", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            { File.Delete(prepared.Installer); return; }
            _pendingUpdate = prepared.Installer;
        }
        catch (Exception ex) { TxtConsole.Text = "Atualização: " + ex.Message; LogTrace(ex.ToString()); }
        finally { _calibrating = false; UpdatePresentation(); }
        if (_pendingUpdate != null) Close();
    }
    private void LaunchPendingUpdate()
    {
        if (_pendingUpdate == null) return;
        try
        {
            LogTrace($"Iniciando instalador da atualização: {_pendingUpdate}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_pendingUpdate) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogTrace("Erro ao iniciar instalador: " + ex);
            MessageBox.Show(this, "Não foi possível abrir o instalador: " + ex.Message, "Atualização");
        }
    }
    private IReadOnlyList<ConnectedDeviceInfo> _availableDevices = Array.Empty<ConnectedDeviceInfo>();
    private void InitializeGameProfiles()
    {
        _profileStore = new(Path.Combine(AppPaths.DataDirectory, "Profiles"));
        try
        {
            _profileStore.Initialize(_profiles);
            _configPath = _profileStore.PathFor(_profileStore.Active.Id);
            LoadConfiguration(); RefreshProfilePicker();
            if (_profileStore.RecoveryNotice is { } notice) TxtConsole.Text = notice;
        }
        catch (Exception ex) { _profileStore = null; TxtConsole.Text = "Perfis: " + ex.Message + " · O perfil anterior foi mantido."; LogTrace(ex.ToString()); }
    }
    private void RefreshProfilePicker()
    {
        if (_profileStore == null) return;
        _loadingProfiles = true;
        try { ProfilePicker.ItemsSource = _profileStore.Profiles.ToList(); ProfilePicker.SelectedItem = _profileStore.Active; }
        finally { _loadingProfiles = false; }
    }
    private bool ConfirmProfileChanges()
    {
        if (!_viewModel.IsDirty) return true;
        var result = MessageBox.Show(this, "Salvar as alterações antes de trocar de perfil?", "Alterações no perfil", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) { BtnSaveConfig_Click(this, new()); return !_viewModel.IsDirty; }
        return true;
    }
    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfiles || _profileStore == null || ProfilePicker.SelectedItem is not GameProfileEntry selected || selected.Id == _profileStore.Active.Id) return;
        if (!CanEdit() || !ConfirmProfileChanges()) { RefreshProfilePicker(); return; }
        try
        {
            _profileStore.Activate(selected.Id);
            _configPath = _profileStore.PathFor(selected.Id); LoadConfiguration();
            TxtConsole.Text = "Perfil ativo: " + selected.Label;
        }
        catch (Exception ex) { TxtConsole.Text = ex.Message; RefreshProfilePicker(); }
    }
    private void ManageProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (_preview) { TxtConsole.Text = "Prévia: os perfis reais não são acessados."; return; }
        if (!CanEdit() || _profileStore == null || !ConfirmProfileChanges()) return;
        var dialog = new ProfileManagerDialog(this, _profileStore);
        if (dialog.ShowDialog() == true && dialog.RequestedProfile is { } id)
        {
            try { _profileStore.Activate(id); _configPath = _profileStore.PathFor(id); LoadConfiguration(); }
            catch (Exception ex) { TxtConsole.Text = ex.Message; }
        }
        RefreshProfilePicker();
    }
    private void RestoreProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_preview || !CanEdit() || _profileStore == null) return;
        if (MessageBox.Show(this, "Restaurar o último backup deste perfil? A versão atual será preservada para recuperação.", "Recuperar perfil", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { _profileStore.Restore(_profileStore.Active.Id); LoadConfiguration(); TxtConsole.Text = "Perfil recuperado do backup."; }
        catch (Exception ex) { TxtConsole.Text = "Recuperação: " + ex.Message; }
    }
    private void CalibrateRow_Click(object sender, RoutedEventArgs e)
    {
        if (_preview) { TxtMappingStatus.Text = "A calibração real fica disponível com o dispositivo conectado no modo normal."; return; }
        if (sender is not Button { Tag: MappingRow row } || !CanEdit() || !row.IsAxis) return;
        var profile = _profiles.SingleOrDefault(p => p.Axes?.Any(a => a.Id == row.AxisId) == true);
        var axis = profile?.Axes?.SingleOrDefault(a => a.Id == row.AxisId);
        if (profile == null || axis == null) { TxtMappingStatus.Text = "Mapeie o eixo antes de calibrar."; return; }
        SyncAxisInversions();
        _calibrating = true; UpdatePresentation();
        try
        {
            var dialog = new CalibrationWindow(this, profile, axis, row.Title);
            if (dialog.ShowDialog() != true || dialog.Result == null) return;
            RememberMappings();
            axis.Calibration = dialog.Result; axis.Deadzone = dialog.ResultDeadzone; axis.Inverted = dialog.ResultInverted ? 1 : 0;
            ProfileManager.SaveToFile(_configPath, _profiles); UpdateMappingLabels();
            TxtMappingStatus.Text = $"Calibração salva: {row.Title}.";
        }
        catch (Exception ex) { TxtMappingStatus.Text = ex.Message; LogTrace(ex.ToString()); }
        finally { _calibrating = false; UpdatePresentation(); }
    }
    private async Task RefreshUsbAsync()
    {
        if (_preview) { TxtUsbStatus.Text = "ODrive-Wheel HID · porta direta no controlador USB (dados simulados)."; return; }
        try
        {
            var connections = await Task.Run(UsbTopology.Inspect);
            string Name(UsbConnectionInfo connection)
            {
                var match = Regex.Match(connection.InstanceId, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
                if (!match.Success) return connection.Device;
                string prefix = match.Groups[2].Value + match.Groups[1].Value;
                var names = _availableDevices.Where(d => d.ProductGuid.ToString("N").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(d => d.InstanceName).Distinct().ToList();
                return names.Count == 1 ? names[0] : connection.Device;
            }
            TxtUsbStatus.Text = connections.Count == 0 ? "Nenhum controlador de jogo USB detectado." : string.Join("\n\n", connections.Select(c => (c with { Device = Name(c) }).Summary));
            TxtUsbStatus.ToolTip = string.Join("\n\n", connections.Select(c => c.Route));
        }
        catch (Exception ex) { TxtUsbStatus.Text = "Não foi possível consultar o caminho USB: " + ex.Message; LogTrace(ex.ToString()); }
    }
    private async void RefreshUsb_Click(object sender, RoutedEventArgs e) => await RefreshUsbAsync();
    private async void Benchmark_Click(object sender, RoutedEventArgs e)
    {
        if (_preview) { TxtBenchmark.Text = "Prévia: medição não executada."; return; }
        if (!CanEdit()) return;
        BtnBenchmark.IsEnabled = false; TxtBenchmark.Text = "Medindo o agendador por 3 segundos…";
        try
        {
            var result = await Task.Run(() => LoopBenchmark.Run(3));
            TxtBenchmark.Text = $"{result.Mode} · {result.FrequencyHz:F0} Hz\nIntervalos: média {result.MeanMs:F2} ms · p95 {result.P95Ms:F2} ms · p99 {result.P99Ms:F2} ms · máximo {result.MaxMs:F2} ms";
        }
        catch (Exception ex) { TxtBenchmark.Text = ex.Message; }
        finally { BtnBenchmark.IsEnabled = true; }
    }
    private void UpdateFfbMonitor(FfbMonitor? monitor)
    {
        TxtFfbPackets.Text = monitor == null ? "—" : $"{monitor.PacketsPerSecond:F0}/s";
        TxtFfbEffects.Text = monitor?.ActiveEffects.ToString() ?? "0";
        TxtFfbTypes.Text = string.IsNullOrEmpty(monitor?.EffectTypes) ? "Nenhum efeito ativo" : monitor.EffectTypes;
        TxtFfbClipping.Text = monitor?.Saturated == true ? "No limite" : "Sem saturação";
        TxtFfbClipping.ToolTip = $"Eventos de saturação da força constante: {monitor?.SaturationEvents ?? 0}. Outros efeitos não são uma medição do torque físico.";
        TxtFfbLastPacket.Text = monitor?.LastPacketAgeMs is { } age ? $"Último pacote há {age / 1000.0:F1} s · total {monitor.TotalPackets}\nFila: {monitor.PendingPackets} · atualizações consolidadas: {monitor.CoalescedPackets}" : "Aguardando efeitos do jogo";
        TxtTimerMode.Text = monitor?.TimerMode ?? "Aguardando sessão";
        TxtFfbError.Text = monitor == null ? "Sem sessão ativa" : _session?.Telemetry.Status ?? "Sessão simulada";
    }
    private void InitializeFeaturePreview()
    {
        ProfilePicker.ItemsSource = new[] { new GameProfileEntry(Guid.Empty, "Meu setup", "Forza Horizon") };
        ProfilePicker.SelectedIndex = 0;
        TxtUsbStatus.Text = "ODrive-Wheel HID · porta direta no controlador USB (dados simulados).";
        TxtBenchmark.Text = "Meça o agendador sem acessar os dispositivos.";
        UpdateFfbMonitor(null);
    }
}
