using System.Windows;
using System.Windows.Controls;
using ForzaNext.Core.Config;

namespace ForzaNext.UI;

internal static class DialogTheme
{
    public static void Apply(Window window)
    {
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ForzaNext.UI;component/Themes/DesignSystem.xaml") });
        window.SetResourceReference(Window.BackgroundProperty, "CanvasBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        window.FontFamily = new("Segoe UI"); window.FontSize = 13;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.SourceInitialized += (_, _) =>
        {
            int enabled = 1;
            _ = DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int));
        };
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

internal sealed class ProfileDetailsDialog : Window
{
    private readonly TextBox _name = new() { MaxLength = 64, Margin = new(0, 7, 0, 18) };
    private readonly TextBox _game = new() { MaxLength = 80, Margin = new(0, 7, 0, 20) };
    public string ProfileName => _name.Text.Trim();
    public string Game => _game.Text.Trim();
    public ProfileDetailsDialog(Window? owner, string title, string name = "", string game = "")
    {
        if (owner != null) Owner = owner; Title = title; Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        DialogTheme.Apply(this); _name.Text = name; _game.Text = game;
        var panel = new StackPanel { Margin = new(28) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 22) });
        panel.Children.Add(new TextBlock { Text = "Nome do perfil" }); panel.Children.Add(_name);
        panel.Children.Add(new TextBlock { Text = "Jogo ou categoria (opcional)" }); panel.Children.Add(_game);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Margin = new(0, 0, 10, 0) };
        var save = new Button { Content = "Salvar perfil", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(ProfileName)) DialogResult = true; else _name.Focus(); };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }
}

internal sealed class ProfileManagerDialog : Window
{
    private readonly GameProfileStore _store;
    private readonly ListBox _list = new() { DisplayMemberPath = "Label", MinHeight = 190, Margin = new(0, 18, 0, 16) };
    public Guid? RequestedProfile { get; private set; }
    public ProfileManagerDialog(Window owner, GameProfileStore store)
    {
        Owner = owner; _store = store; Title = "Perfis por jogo"; Width = 680; Height = 460; MinWidth = 560; MinHeight = 420;
        DialogTheme.Apply(this);
        var panel = new DockPanel { Margin = new(28) };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top);
        heading.Children.Add(new TextBlock { Text = "Um setup para cada jogo.", FontSize = 23, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Mapeamento, calibração e FFB são salvos juntos em cada perfil.", Style = (Style)FindResource("BodyMuted"), Margin = new(0, 8, 0, 0) });
        panel.Children.Add(heading);
        var actions = new WrapPanel { Margin = new(0, 0, 0, 12) }; DockPanel.SetDock(actions, Dock.Bottom);
        AddButton(actions, "Novo", Create);
        AddButton(actions, "Duplicar", Duplicate);
        AddButton(actions, "Renomear", Rename);
        AddButton(actions, "Importar", Import);
        AddButton(actions, "Exportar", Export);
        AddButton(actions, "Excluir", Delete);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(footer, Dock.Bottom);
        AddButton(footer, "Fechar", () => Close());
        AddButton(footer, "Usar perfil", () => { if (Selected is { } p) { RequestedProfile = p.Id; DialogResult = true; } });
        panel.Children.Add(footer); panel.Children.Add(actions); panel.Children.Add(_list); Content = panel;
        Reload(store.Active.Id);
    }
    private GameProfileEntry? Selected => _list.SelectedItem as GameProfileEntry;
    private static void AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Margin = new(0, 0, 8, 8) };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(ex.Message, "Perfis", MessageBoxButton.OK, MessageBoxImage.Information); } };
        panel.Children.Add(button);
    }
    private void Reload(Guid? selected = null)
    { _list.ItemsSource = _store.Profiles.ToList(); _list.SelectedItem = _store.Profiles.FirstOrDefault(p => p.Id == selected) ?? _store.Active; }
    private void Duplicate()
    {
        if (Selected is not { } p) return;
        var dialog = new ProfileDetailsDialog(this, "Duplicar perfil", p.Name + " · cópia", p.Game);
        if (dialog.ShowDialog() == true) Reload(_store.Create(dialog.ProfileName, dialog.Game, _store.Open(p.Id)).Id);
    }
    private void Create()
    {
        var dialog = new ProfileDetailsDialog(this, "Novo perfil", "", "");
        if (dialog.ShowDialog() == true) Reload(_store.Create(dialog.ProfileName, dialog.Game, _store.Open(_store.Active.Id)).Id);
    }
    private void Rename()
    {
        if (Selected is not { } p) return;
        var dialog = new ProfileDetailsDialog(this, "Renomear perfil", p.Name, p.Game);
        if (dialog.ShowDialog() == true) { _store.Rename(p.Id, dialog.ProfileName, dialog.Game); Reload(p.Id); }
    }
    private void Import()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Perfil ForzaNext ou configuração JSON|*.json" };
        if (picker.ShowDialog(this) == true) Reload(_store.Import(picker.FileName).Id);
    }
    private void Export()
    {
        if (Selected is not { } p) return;
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Perfil ForzaNext|*.forzanext.json", FileName = "perfil.forzanext.json" };
        if (picker.ShowDialog(this) == true) _store.Export(p.Id, picker.FileName);
    }
    private void Delete()
    {
        if (Selected is not { } p) return;
        if (p.Id == _store.Active.Id) throw new InvalidOperationException("Use outro perfil antes de excluir este.");
        if (MessageBox.Show(this, $"Excluir o perfil “{p.Name}”? Uma cópia será arquivada.", "Excluir perfil", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        { _store.Delete(p.Id); Reload(); }
    }
}
