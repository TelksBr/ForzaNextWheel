using System.Security.Cryptography;
using System.Text.Json;

namespace ForzaNext.Core.Config;

public sealed record ReleaseManifest(string Product, string Version, string Package, string Sha256, long Size);
public sealed record PreparedUpdate(string Version, string Installer);

// Offline packages are explicitly selected by the user. The checksum detects damaged or
// mismatched files; it is not a publisher signature or an online trust channel.
public static class ReleasePackage
{
    public static async Task<PreparedUpdate> PrepareAsync(string manifestPath, Version current, string stagingDirectory, CancellationToken token = default)
    {
        if (new FileInfo(manifestPath).Length > 65536) throw new InvalidDataException("Manifesto de atualização muito grande.");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(await File.ReadAllTextAsync(manifestPath, token))
            ?? throw new InvalidDataException("Manifesto inválido.");
        if (manifest.Product != "ForzaNextWheel" || !System.Version.TryParse(manifest.Version, out var version) || version <= current)
            throw new InvalidDataException("O pacote não contém uma versão mais recente do ForzaNext.");
        if (string.IsNullOrWhiteSpace(manifest.Package) || manifest.Package != Path.GetFileName(manifest.Package) ||
            manifest.Package.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || !manifest.Package.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            manifest.Size is < 1 or > 536870912 || manifest.Sha256 == null || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Nome, tamanho ou checksum do instalador inválido.");
        string source = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, manifest.Package);
        Directory.CreateDirectory(stagingDirectory);
        string destination = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            {
                if (input.Length != manifest.Size) throw new InvalidDataException("Tamanho do instalador não corresponde ao manifesto.");
                await input.CopyToAsync(output, token);
                output.Position = 0;
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(output, token));
                if (!actual.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Checksum do instalador não corresponde ao manifesto.");
                output.Flush(true);
            }
            return new(manifest.Version, destination);
        }
        catch { if (File.Exists(destination)) File.Delete(destination); throw; }
    }
}
