using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace DreamWin.Services;

public class GitHubRelease
{
    public string TagName { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Prerelease { get; set; }
    public string DownloadUrl { get; set; } = "";
    public DateTime PublishedAt { get; set; }
}

public class UpdateService
{
    private const string GitHubOwner = "Ub1Catcrush";
    private const string GitHubRepo = "DreamWin";
    private const string GitHubApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases";

    private static readonly HttpClient _httpClient = new();
    private string _currentVersion;

    public event EventHandler<string>? UpdateAvailable;
    public event EventHandler<string>? UpdateProgressChanged;
    public event EventHandler<string>? UpdateCompleted;
    public event EventHandler<string>? UpdateError;

    public UpdateService(string currentVersion)
    {
        _currentVersion = currentVersion;
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "DreamWin-UpdateChecker");
    }

    public string CurrentVersion => _currentVersion;

    /// <summary>
    /// Checks GitHub for the latest release
    /// </summary>
    public async Task<GitHubRelease?> CheckForUpdatesAsync(bool includePrerelease = false)
    {
        try
        {
            UpdateProgressChanged?.Invoke(this, "Checking for updates...");

            var response = await _httpClient.GetAsync(GitHubApiUrl);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var releases = JArray.Parse(json);

            foreach (var releaseToken in releases)
            {
                var release = new GitHubRelease
                {
                    TagName = releaseToken["tag_name"]?.Value<string>() ?? "",
                    Name = releaseToken["name"]?.Value<string>() ?? "",
                    Prerelease = releaseToken["prerelease"]?.Value<bool>() ?? false,
                    PublishedAt = DateTime.Parse(releaseToken["published_at"]?.Value<string>() ?? DateTime.Now.ToString())
                };

                // Skip prerelease if not wanted
                if (release.Prerelease && !includePrerelease)
                    continue;

                // Look for installer asset (.exe or .msi)
                var assets = releaseToken["assets"]?? new JArray();
                foreach (var asset in assets)
                {
                    var assetName = asset["name"]?.Value<string>() ?? "";
                    if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        assetName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    {
                        release.DownloadUrl = asset["browser_download_url"]?.Value<string>() ?? "";
                        break;
                    }
                }

                if (string.IsNullOrEmpty(release.DownloadUrl))
                    continue;

                // Compare release tag vs current version
                Debug.WriteLine($"[UpdateService] tag='{release.TagName}' current='{_currentVersion}'");
                if (IsNewerVersion(release.TagName, _currentVersion))
                {
                    Debug.WriteLine($"[UpdateService] UPDATE AVAILABLE: {release.TagName}");
                    UpdateAvailable?.Invoke(this, $"New version available: {release.Name}");
                    return release;
                }

                Debug.WriteLine($"[UpdateService] Already up to date (latest={release.TagName})");
                return null;
            }

            return null;
        }
        catch (Exception ex)
        {
            UpdateError?.Invoke(this, $"Error checking for updates: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads the latest release installer
    /// </summary>
    public async Task<string?> DownloadUpdateAsync(GitHubRelease release, string? downloadPath = null)
    {
        try
        {
            downloadPath ??= Path.Combine(Path.GetTempPath(), "DreamWin");
            Directory.CreateDirectory(downloadPath);

            var fileName = Path.GetFileName(release.DownloadUrl.Split('?')[0]);
            var filePath = Path.Combine(downloadPath, fileName);

            UpdateProgressChanged?.Invoke(this, $"Downloading {fileName}...");

            using (var response = await _httpClient.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                var canReportProgress = totalBytes != -1 && totalBytes != 0;

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    var totalRead = 0L;
                    var buffer = new byte[8192];
                    var isMoreToRead = true;

                    while (isMoreToRead)
                    {
                        var read = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                        if (read == 0)
                        {
                            isMoreToRead = false;
                            continue;
                        }

                        await fileStream.WriteAsync(buffer, 0, read);
                        totalRead += read;

                        if (canReportProgress)
                        {
                            var progressPercentage = (totalRead * 100) / totalBytes;
                            UpdateProgressChanged?.Invoke(this, $"Downloading: {progressPercentage}%");
                        }
                    }
                }
            }

            UpdateProgressChanged?.Invoke(this, "Download complete");
            return filePath;
        }
        catch (Exception ex)
        {
            UpdateError?.Invoke(this, $"Error downloading update: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Installs the downloaded update by launching the installer
    /// </summary>
    public Task<bool> InstallUpdateAsync(string installerPath)
    {
        try
        {
            if (!File.Exists(installerPath))
            {
                UpdateError?.Invoke(this, "Installer file not found");
                return Task.FromResult(false);
            }

            UpdateProgressChanged?.Invoke(this, "Starting installer...");

            var processInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };

            // For .msi files, add standard installation parameters
            if (installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                processInfo.FileName = "msiexec.exe";
                processInfo.Arguments = $"/i \"{installerPath}\" /passive";
            }

            using (var process = Process.Start(processInfo))
            {
                if (process != null)
                {
                    // Don't wait for completion - let the installer run independently
                    // The app will be updated and restarted by the installer
                    UpdateCompleted?.Invoke(this, "Update installer started. Please complete the installation.");
                    return Task.FromResult(true);
                }
            }

            UpdateError?.Invoke(this, "Failed to start installer");
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            UpdateError?.Invoke(this, $"Error installing update: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Performs automatic update: checks, downloads, and installs
    /// </summary>
    public async Task<bool> PerformAutoUpdateAsync(string? downloadPath = null, bool includePrerelease = false)
    {
        var release = await CheckForUpdatesAsync(includePrerelease);
        if (release == null)
        {
            UpdateProgressChanged?.Invoke(this, "Already up to date");
            return false;
        }

        var installerPath = await DownloadUpdateAsync(release, downloadPath);
        if (installerPath == null)
            return false;

        return await InstallUpdateAsync(installerPath);
    }

    /// <summary>
    /// Compares two version strings (e.g., "v1.0.0" or "1.0.0")
    /// </summary>
    private bool IsNewerVersion(string tagVersion, string currentVersion)
    {
        try
        {
            // Remove 'v' prefix if present
            var tag = tagVersion.TrimStart('v', 'V');
            var current = currentVersion.TrimStart('v', 'V');

            var tagParts = tag.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            var currentParts = current.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();

            // Pad arrays to same length
            Array.Resize(ref tagParts, Math.Max(tagParts.Length, currentParts.Length));
            Array.Resize(ref currentParts, Math.Max(tagParts.Length, currentParts.Length));

            for (int i = 0; i < tagParts.Length; i++)
            {
                if (tagParts[i] > currentParts[i])
                    return true;
                if (tagParts[i] < currentParts[i])
                    return false;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
