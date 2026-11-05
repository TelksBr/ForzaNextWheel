using System.Text.Json;

namespace ForzaNext.Core.Config;

public sealed record GameProfileEntry(Guid Id, string Name, string Game)
{
    public string Label => string.IsNullOrWhiteSpace(Game) ? Name : $"{Name} · {Game}";
}
public sealed class GameProfileCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public Guid ActiveId { get; set; }
    public List<GameProfileEntry> Profiles { get; set; } = new();
}
public sealed class GameProfileExport
{
    public int SchemaVersion { get; set; } = 1;
    public string Product { get; set; } = "ForzaNextWheel";
    public string Name { get; set; } = "Meu setup";
    public string Game { get; set; } = "";
    public List<ControllerProfile> Controllers { get; set; } = new();
}

// Metadata never supplies paths: profile filenames are derived exclusively from GUIDs.
public sealed class GameProfileStore
{
    private readonly string _directory;
    private GameProfileCatalog _catalog = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public IReadOnlyList<GameProfileEntry> Profiles => _catalog.Profiles.AsReadOnly();
    public GameProfileEntry Active => _catalog.Profiles.Single(p => p.Id == _catalog.ActiveId);
    public string? RecoveryNotice { get; private set; }
    private string IndexPath => Path.Combine(_directory, "index.json");
    public string PathFor(Guid id) => id != Guid.Empty ? Path.Combine(_directory, id.ToString("N") + ".json") : throw new ArgumentException("Perfil inválido.");
    public GameProfileStore(string directory) => _directory = Path.GetFullPath(directory);

    public void Initialize(List<ControllerProfile> legacy)
    {
        Directory.CreateDirectory(_directory);
        RecoveryNotice = null;
        if (File.Exists(IndexPath))
        {
            try { _catalog = ReadCatalog(IndexPath); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                if (!File.Exists(IndexPath + ".bak")) throw;
                _catalog = ReadCatalog(IndexPath + ".bak");
                PreserveAndRestore(IndexPath, IndexPath + ".bak");
                RecoveryNotice = "A lista de perfis foi recuperada do backup.";
            }
        }
        else if (File.Exists(IndexPath + ".bak"))
        {
            _catalog = ReadCatalog(IndexPath + ".bak");
            PreserveAndRestore(IndexPath, IndexPath + ".bak");
            RecoveryNotice = "A lista de perfis foi recuperada do backup.";
        }
        else
        {
            // A lost catalog must not cause existing profile files to be discarded.
            var recovered = Directory.EnumerateFiles(_directory, "*.json")
                .Where(f => Guid.TryParseExact(Path.GetFileNameWithoutExtension(f), "N", out _)).ToList();
            if (recovered.Count > 0)
            {
                foreach (var file in recovered)
                {
                    ProfileManager.LoadFromFile(file);
                    var id = Guid.ParseExact(Path.GetFileNameWithoutExtension(file), "N");
                    _catalog.Profiles.Add(new(id, "Recuperado " + id.ToString("N")[..8], ""));
                }
                _catalog.ActiveId = _catalog.Profiles[0].Id;
                SaveCatalog(); RecoveryNotice = "Os perfis foram recuperados; revise seus nomes.";
            }
            else
            {
                var entry = new GameProfileEntry(Guid.NewGuid(), "Meu setup", "Global");
                ProfileManager.SaveToFile(PathFor(entry.Id), ProfileManager.Snapshot(legacy));
                _catalog = new() { ActiveId = entry.Id, Profiles = new() { entry } };
                SaveCatalog();
            }
        }
        Open(Active.Id);
    }

    private static GameProfileCatalog ReadCatalog(string path)
    {
        var value = JsonSerializer.Deserialize<GameProfileCatalog>(File.ReadAllText(path)) ?? throw new InvalidDataException("Lista de perfis vazia.");
        if (value.SchemaVersion != 1 || value.Profiles == null || value.Profiles.Count == 0 || value.Profiles.Any(p => p == null || p.Id == Guid.Empty) ||
            value.Profiles.Select(p => p.Id).Distinct().Count() != value.Profiles.Count || !value.Profiles.Any(p => p.Id == value.ActiveId))
            throw new InvalidDataException("Lista de perfis inválida ou versão não suportada.");
        foreach (var profile in value.Profiles) ValidateName(profile.Name, profile.Game);
        return value;
    }
    public List<ControllerProfile> Open(Guid id)
    {
        Require(id);
        string path = PathFor(id);
        try { return ProfileManager.LoadFromFile(path); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FileNotFoundException)
        {
            var backup = ProfileManager.LoadFromFile(path + ".bak");
            PreserveAndRestore(path, path + ".bak");
            RecoveryNotice = "O perfil foi recuperado do último backup válido.";
            return backup;
        }
    }
    public void Activate(Guid id)
    {
        Open(id);
        var previous = _catalog.ActiveId;
        _catalog.ActiveId = id;
        try { SaveCatalog(); } catch { _catalog.ActiveId = previous; throw; }
    }
    public GameProfileEntry Create(string name, string game, List<ControllerProfile> controllers)
    {
        ValidateName(name, game);
        var entry = new GameProfileEntry(Guid.NewGuid(), name.Trim(), game.Trim());
        ProfileManager.SaveToFile(PathFor(entry.Id), ProfileManager.Snapshot(controllers));
        _catalog.Profiles.Add(entry);
        try { SaveCatalog(); } catch { _catalog.Profiles.Remove(entry); throw; }
        return entry;
    }
    public void Rename(Guid id, string name, string game)
    {
        ValidateName(name, game); var entry = Require(id);
        int index = _catalog.Profiles.IndexOf(entry);
        _catalog.Profiles[index] = entry with { Name = name.Trim(), Game = game.Trim() };
        try { SaveCatalog(); } catch { _catalog.Profiles[index] = entry; throw; }
    }
    public void Delete(Guid id)
    {
        var entry = Require(id);
        if (_catalog.Profiles.Count == 1 || _catalog.ActiveId == id) throw new InvalidOperationException("Selecione outro perfil antes de excluir este.");
        string archive = Path.Combine(_directory, "Archive"); Directory.CreateDirectory(archive);
        var export = new GameProfileExport { Name = entry.Name, Game = entry.Game, Controllers = Open(id) };
        WriteAtomic(Path.Combine(archive, id.ToString("N") + ".json"), export);
        int index = _catalog.Profiles.IndexOf(entry); _catalog.Profiles.RemoveAt(index);
        try { SaveCatalog(); } catch { _catalog.Profiles.Insert(index, entry); throw; }
        File.Delete(PathFor(id)); File.Delete(PathFor(id) + ".bak");
    }
    public void Export(Guid id, string destination)
    {
        var entry = Require(id);
        WriteAtomic(Path.GetFullPath(destination), new GameProfileExport { Name = entry.Name, Game = entry.Game, Controllers = Open(id) });
    }
    public GameProfileEntry Import(string source)
    {
        if (new FileInfo(source).Length > 2 * 1024 * 1024) throw new InvalidDataException("Arquivo de perfil excede 2 MB.");
        using var document = JsonDocument.Parse(File.ReadAllText(source));
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return Create(Path.GetFileNameWithoutExtension(source), "Importado", ProfileManager.LoadFromFile(source));
        var value = document.RootElement.Deserialize<GameProfileExport>() ?? throw new InvalidDataException("Perfil inválido.");
        if (value.SchemaVersion != 1 || value.Product != "ForzaNextWheel") throw new InvalidDataException("Formato de perfil não suportado.");
        return Create(value.Name, value.Game, value.Controllers);
    }
    public void Restore(Guid id)
    {
        Require(id); string path = PathFor(id);
        ProfileManager.LoadFromFile(path + ".bak");
        PreserveAndRestore(path, path + ".bak");
    }
    private GameProfileEntry Require(Guid id) => _catalog.Profiles.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("Perfil não encontrado.");
    private static void ValidateName(string name, string game)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64 || game == null || game.Length > 80)
            throw new InvalidDataException("Informe um nome de até 64 caracteres e um jogo de até 80 caracteres.");
    }
    private void SaveCatalog() => WriteAtomic(IndexPath, _catalog);
    private static void PreserveAndRestore(string path, string backup)
    {
        if (File.Exists(path)) File.Copy(path, path + ".recovered-" + Guid.NewGuid().ToString("N"));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(backup, temporary); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
