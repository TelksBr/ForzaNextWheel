using System.ComponentModel;
using System.Runtime.CompilerServices;
using ForzaNext.Core.Config;

namespace ForzaNext.UI.Presentation;

public abstract class ObservableState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public enum ShellPhase { NeedsSetup, Preparing, Ready, Starting, Running, InputOnly, Mapping, Attention }

public sealed class MappingRow : ObservableState
{
    public string Title { get; }
    public string? AxisId { get; }
    public int? ButtonId { get; }
    public bool IsAxis => AxisId != null;
    public string Kind => IsAxis ? "AX" : "BT";
    private string _source = "Não mapeado", _detail = "Selecionar entrada física";
    private bool _mapped, _inverted;
    public string Source { get => _source; private set => Set(ref _source, value); }
    public string SourceDetail { get => _detail; private set => Set(ref _detail, value); }
    public bool IsMapped { get => _mapped; private set { if (Set(ref _mapped, value)) Changed(nameof(SourceColor)); } }
    public string SourceColor => IsMapped ? "#DDE4EC" : "#858F9D";
    public bool Inverted { get => _inverted; set => Set(ref _inverted, value); }
    public MappingRow(string title, string? axis = null, int? button = null) { Title = title; AxisId = axis; ButtonId = button; }

    public void Refresh(IEnumerable<ControllerProfile> profiles)
    {
        foreach (var p in profiles)
        {
            var axis = IsAxis ? p.Axes?.FirstOrDefault(a => a.Id == AxisId) : null;
            var button = !IsAxis ? p.Buttons?.FirstOrDefault(b => b.Id == ButtonId) : null;
            if (axis == null && button == null) continue;
            Source = p.InstanceName;
            string[] names = { "X", "Y", "Z", "Rx", "Ry", "Rz", "Slider 1", "Slider 2" };
            SourceDetail = axis != null ? $"Eixo {names[Math.Clamp(axis.AxisIndex, 0, 7)]}" : $"Botão {button!.Index + 1}";
            if (axis?.Calibration != null) SourceDetail += $" · calibrado · {axis.Deadzone:P0}";
            IsMapped = true; Inverted = axis?.Inverted == 1; return;
        }
        Source = "Não mapeado"; SourceDetail = "Selecionar entrada física"; IsMapped = false; Inverted = false;
    }
}

public sealed record MappingSection(string Title, string Description, bool Expanded, IReadOnlyList<MappingRow> Rows)
{
    public string CountLabel => $"{Rows.Count} controles";
}

public sealed class ShellViewModel : ObservableState
{
    public IReadOnlyList<MappingSection> MappingSections { get; } = CreateMappings();
    public IEnumerable<MappingRow> Mappings => MappingSections.SelectMany(s => s.Rows);
    private ShellPhase _phase = ShellPhase.NeedsSetup;
    private string _detail = "O volante virtual é preparado automaticamente na abertura.", _notification = "";
    private bool _dirty, _hasMappings, _canEdit = true, _canSave = true, _canTest, _undo, _preview;
    private int _axisCount, _buttonCount;
    public ShellPhase Phase => _phase;
    public string Detail => _detail;
    public bool IsDirty { get => _dirty; set { if (Set(ref _dirty, value)) Changed(nameof(SaveLabel)); } }
    public string SaveLabel => IsDirty ? "Salvar alterações" : "Tudo salvo";
    public bool CanEdit { get => _canEdit; set => Set(ref _canEdit, value); }
    public bool CanSave { get => _canSave; set => Set(ref _canSave, value); }
    public bool CanTest { get => _canTest; set => Set(ref _canTest, value); }
    public bool HasUndo { get => _undo; set => Set(ref _undo, value); }
    public bool IsPreview { get => _preview; set => Set(ref _preview, value); }
    public string Notification { get => _notification; set { if (Set(ref _notification, value)) Changed(nameof(HasNotification)); } }
    public bool HasNotification => !string.IsNullOrWhiteSpace(Notification);
    public string MappingSummary => $"{_axisCount} eixos · {_buttonCount} botões configurados";
    public string MappingStatus => _hasMappings ? "Controles configurados" : "Mapeie seus controles";
    public string MappingDescription => _hasMappings ? MappingSummary : "Associe o volante, os pedais e o câmbio.";
    public string Headline => _phase switch
    {
        ShellPhase.Preparing => "Preparando seu volante virtual.",
        ShellPhase.Starting => "Conectando ao seu setup.",
        ShellPhase.Running => "Você está no controle.",
        ShellPhase.InputOnly => "Seus controles estão conectados.",
        ShellPhase.Mapping => "Aguardando seu controle.",
        ShellPhase.Attention => "Seu setup precisa de atenção.",
        ShellPhase.Ready when !_hasMappings => "Vamos mapear seus controles.",
        ShellPhase.Ready => "Seu setup está pronto.",
        _ => "Sua próxima sessão começa aqui."
    };
    public string Description => _phase switch
    {
        ShellPhase.Preparing => "O Windows está preparando o dispositivo. Autorize a operação quando solicitado.",
        ShellPhase.Starting => "Estamos conectando os dispositivos e verificando o retorno de força.",
        ShellPhase.Running => "Entradas e retorno de força ativos. Abra o jogo e aproveite a pista.",
        ShellPhase.InputOnly => _detail,
        ShellPhase.Mapping => "Acione apenas o eixo ou botão que deseja associar. Você pode cancelar a qualquer momento.",
        ShellPhase.Attention => _detail,
        ShellPhase.Ready when !_hasMappings => "Associe suas entradas físicas antes de iniciar uma sessão.",
        ShellPhase.Ready => "Volante virtual preparado. Inicie uma sessão para transmitir seus controles ao jogo.",
        _ => "Reúna volante, pedais e câmbio em uma experiência de pilotagem."
    };
    public string StatusLabel => _phase switch
    {
        ShellPhase.Preparing => "Preparando", ShellPhase.Starting => "Conectando",
        ShellPhase.Running => "Sessão ativa", ShellPhase.InputOnly => "Somente entradas",
        ShellPhase.Mapping => "Mapeando", ShellPhase.Attention => "Requer atenção",
        ShellPhase.Ready => "Pronto para iniciar", _ => "Aguardando preparação"
    };
    public string StatusColor => _phase is ShellPhase.Attention ? "#FFB294"
        : _phase is ShellPhase.Running or ShellPhase.Ready ? "#C3F66A" : "#A8B5C7";
    public string StatusBackground => _phase == ShellPhase.Attention ? "#30251F" : "#252B22";
    public string PrimaryAction => _phase switch
    {
        ShellPhase.Running or ShellPhase.InputOnly => "Parar sessão",
        ShellPhase.Preparing => "Preparando…", ShellPhase.Starting => "Conectando…",
        ShellPhase.Mapping => "Mapeando…", ShellPhase.Ready when !_hasMappings => "Mapear controles",
        ShellPhase.Attention or ShellPhase.NeedsSetup => "Preparar e iniciar", _ => "Iniciar sessão"
    };
    public string PrimaryBackground => _phase is ShellPhase.Running or ShellPhase.InputOnly ? "#402B2C" : "#C3F66A";
    public string PrimaryForeground => _phase is ShellPhase.Running or ShellPhase.InputOnly ? "#FFD4D0" : "#151B0F";
    public string PreviewLabel => "PRÉVIA · DADOS SIMULADOS";
    public string FfbDescription => _phase is ShellPhase.InputOnly or ShellPhase.Attention ? _detail
        : _phase == ShellPhase.Running ? "Retorno de força conectado ao volante. Os testes estão disponíveis."
        : "Inicie uma sessão com FFB para conectar o volante. Os testes ficam disponíveis após a abertura dos efeitos.";
    public string MonitorDescription => _phase is ShellPhase.Running or ShellPhase.InputOnly
        ? "Acompanhe as entradas transmitidas ao volante virtual."
        : "Inicie uma sessão para acompanhar seus controles em tempo real.";

    public void SetPhase(ShellPhase phase, bool hasMappings, string detail)
    {
        bool changed = _phase != phase || _hasMappings != hasMappings || _detail != detail;
        _phase = phase; _hasMappings = hasMappings; _detail = detail;
        if (!changed) return;
        foreach (var name in new[] { nameof(Phase), nameof(Detail), nameof(Headline), nameof(Description), nameof(StatusLabel),
            nameof(StatusColor), nameof(StatusBackground), nameof(PrimaryAction), nameof(PrimaryBackground), nameof(PrimaryForeground), nameof(MonitorDescription), nameof(FfbDescription) }) Changed(name);
    }

    public void RefreshMappings(List<ControllerProfile> profiles)
    {
        foreach (var row in Mappings) row.Refresh(profiles);
        _axisCount = profiles.Sum(p => p.Axes?.Count ?? 0);
        _buttonCount = profiles.Sum(p => p.Buttons?.Count ?? 0);
        _hasMappings = _axisCount + _buttonCount > 0 || profiles.Any(p => p.DPad != null);
        Changed(nameof(MappingSummary)); Changed(nameof(MappingStatus)); Changed(nameof(MappingDescription));
        Changed(nameof(Headline)); Changed(nameof(Description)); Changed(nameof(PrimaryAction));
    }

    private static IReadOnlyList<MappingSection> CreateMappings() => new MappingSection[]
    {
        new("Volante e pedais", "As entradas essenciais da sua pilotagem.", true, new MappingRow[]
        { new("Volante", "Steering"), new("Acelerador", "Throttle"), new("Freio", "Brake"), new("Embreagem", "Clutch"), new("Freio de mão", "Handbrake") }),
        new("Câmbio sequencial", "Borboletas, alavanca e freio de mão digital.", true, new MappingRow[]
        { new("Subir marcha", button:17), new("Reduzir marcha", button:16), new("Freio de mão digital", button:26) }),
        new("Câmbio em H", "Associe cada posição do seu câmbio.", false, new MappingRow[]
        { new("1ª marcha", button:19), new("2ª marcha", button:20), new("3ª marcha", button:21), new("4ª marcha", button:22),
          new("5ª marcha", button:23), new("6ª marcha", button:24), new("7ª marcha", button:25), new("Marcha ré", button:18) }),
        new("Navegação e botões", "Comandos de menu e botões do volante.", false, new MappingRow[]
        { new("Confirmar · A", button:2), new("Voltar · B", button:3), new("Botão X", button:4), new("Botão Y", button:5),
          new("Menu", button:1), new("View", button:0), new("LB", button:6), new("RB", button:7), new("L3", button:8), new("R3", button:9) }),
        new("Direcional", "Use quatro botões físicos como um D-pad.", false, new MappingRow[]
        { new("Cima", button:32), new("Baixo", button:33), new("Esquerda", button:34), new("Direita", button:35) }),
        new("Ações adicionais", "Personalize as funções extras do seu setup.", false, new MappingRow[]
        { new("Buzina", button:10), new("Telemetria", button:11), new("Olhar à esquerda", button:12), new("Olhar à direita", button:13),
          new("Forza LINK / Anna", button:14), new("Modo fotografia", button:15), new("Botão extra 28", button:27),
          new("Botão extra 29", button:28), new("Botão extra 30", button:29), new("Botão extra 31", button:30), new("Botão extra 32", button:31) })
    };
}
