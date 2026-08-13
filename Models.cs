using System.Text.Json.Serialization;

namespace TFGLauncher;

internal sealed class LauncherSettings
{
    public string Nickname { get; set; } = "";
}

internal sealed class InstallationState
{
    public int PackageRevision { get; set; }
    public string PackVersion { get; set; } = "";
    public string MinecraftVersion { get; set; } = "";
    public string ForgeVersion { get; set; } = "";
    public string ForgeVersionName { get; set; } = "";
    public List<string> ManagedFiles { get; set; } = [];
    public Dictionary<string, string> ManagedHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed record ServerStatus(
    bool Online,
    int Players,
    int MaxPlayers,
    string? PackVersion,
    string State = "offline",
    string Stage = "unknown",
    DateTimeOffset? ExpectedUntil = null);
internal sealed record ReleaseAsset(string Name, long Size, string DownloadUrl, string Sha256);
internal sealed record LauncherProgress(int Percent, string Message);
internal sealed record RepairResult(bool Healthy, IReadOnlyList<string> DamagedFiles);
internal sealed record LauncherUpdate(string Version, string InstallerUrl, long Size, string Sha256, string SignerThumbprint);

internal sealed class GitHubRelease
{
    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = [];
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("digest")]
    public string Digest { get; set; } = "";
}
