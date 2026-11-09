using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ForzaNext.Core.Config;

public sealed record UpdateCheckResult(bool UpdateAvailable, ReleaseManifest? Manifest, string? DownloadUrl, string? Error = null);

public static class GitHubUpdateService
{
    public const string DefaultManifestUrl = "https://github.com/TelksBr/ForzaNextWheel/releases/latest/download/release.json";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<UpdateCheckResult> CheckForUpdateAsync(
        Version currentVersion,
        string manifestUrl = DefaultManifestUrl,
        CancellationToken token = default)
    {
        try
        {
            string json;
            if (manifestUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                manifestUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
                request.Headers.Add("User-Agent", "ForzaNextWheel-Updater");

                using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode)
                {
                    return new(false, null, null, $"Não foi possível consultar atualizações (HTTP {(int)response.StatusCode}).");
                }
                json = await response.Content.ReadAsStringAsync(token);
            }
            else
            {
                string localPath = manifestUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(manifestUrl).LocalPath
                    : manifestUrl;
                json = await File.ReadAllTextAsync(localPath, token);
            }
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json);
            if (manifest == null || manifest.Product != "ForzaNextWheel")
            {
                return new(false, null, null, "Manifesto de atualização remoto inválido.");
            }

            if (!Version.TryParse(manifest.Version, out var remoteVersion))
            {
                return new(false, null, null, "Versão inválida no manifesto remoto.");
            }

            if (remoteVersion > currentVersion)
            {
                int lastSlash = manifestUrl.LastIndexOf('/');
                string baseDownloadUrl = lastSlash >= 0 ? manifestUrl[..(lastSlash + 1)] : manifestUrl + "/";
                string installerUrl = baseDownloadUrl + manifest.Package;
                return new(true, manifest, installerUrl);
            }

            return new(false, manifest, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(false, null, null, ex.Message);
        }
    }

    public static async Task<PreparedUpdate> DownloadAndPrepareUpdateAsync(
        UpdateCheckResult check,
        Version currentVersion,
        string stagingDirectory,
        IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        if (!check.UpdateAvailable || check.Manifest == null || string.IsNullOrEmpty(check.DownloadUrl))
            throw new InvalidOperationException("Nenhuma atualização disponível para download.");

        Directory.CreateDirectory(stagingDirectory);
        string tempDir = Path.Combine(stagingDirectory, "temp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Save release.json locally
            string localManifestPath = Path.Combine(tempDir, "release.json");
            await File.WriteAllTextAsync(localManifestPath, JsonSerializer.Serialize(check.Manifest), token);

            // 2. Download installer with progress
            string localInstallerPath = Path.Combine(tempDir, check.Manifest.Package);
            using (var request = new HttpRequestMessage(HttpMethod.Get, check.DownloadUrl))
            {
                request.Headers.Add("User-Agent", "ForzaNextWheel-Updater");
                using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? check.Manifest.Size;
                await using var contentStream = await response.Content.ReadAsStreamAsync(token);
                await using var fileStream = new FileStream(localInstallerPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), token);
                    totalRead += read;
                    if (totalBytes > 0)
                    {
                        progress?.Report((double)totalRead / totalBytes);
                    }
                }
                await fileStream.FlushAsync(token);
            }

            // 3. Prepare and verify checksum + size via ReleasePackage
            return await ReleasePackage.PrepareAsync(localManifestPath, currentVersion, stagingDirectory, token);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }
}
