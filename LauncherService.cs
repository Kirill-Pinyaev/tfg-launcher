using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.Forge.Versions;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.VersionLoader;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TFGLauncher;

internal sealed class LauncherService
{
    public const string ServerHost = "77.51.139.159";
    public const string LanServerHost = "192.168.1.78";
    public const ushort ServerPort = 25565;
    public const string InitialPackVersion = "0.13.7";
    public const int CurrentPackageRevision = 3;
    public const string ApiBaseUrl = "https://tfg.kirillkatya.crazedns.ru";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] ProtectedFiles = ["options.txt", "servers.dat"];
    private static readonly string[] ProtectedDirectories = ["saves/"];
    private readonly HttpClient http;

    public string RootDirectory { get; }
    public string GameDirectory => Path.Combine(RootDirectory, "game");
    public string ConnectionHost { get; private set; } = ServerHost;
    private string SettingsPath => Path.Combine(RootDirectory, "settings.json");
    private string StatePath => Path.Combine(RootDirectory, "installation.json");
    private string LogPath => Path.Combine(RootDirectory, "launcher.log");

    public LauncherService(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TFGLauncher");
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(GameDirectory);
        http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TFG-Launcher/1.0");
    }

    public LauncherSettings LoadSettings() => LoadJson(SettingsPath, new LauncherSettings());
    public InstallationState LoadState() => LoadJson(StatePath, new InstallationState());
    public void SaveSettings(LauncherSettings settings) => SaveJsonAtomic(SettingsPath, settings);

    public async Task<ServerStatus> GetServerStatusAsync(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var apiStatus = await CheckStatusApiAsync(timeout.Token);
        if (apiStatus.Online)
        {
            ConnectionHost = IsOnServerLan() ? LanServerHost : ServerHost;
            return apiStatus;
        }
        var checks = new List<Task<(ServerStatus Status, string Host)>>
        {
            CheckDirectAsync(ServerHost, timeout.Token),
            CheckApiAsync($"https://api.mcsrvstat.us/3/{ServerHost}:{ServerPort}", timeout.Token),
            CheckApiAsync($"https://api.mcstatus.io/v2/status/java/{ServerHost}:{ServerPort}", timeout.Token)
        };
        if (IsOnServerLan()) checks.Add(CheckDirectAsync(LanServerHost, timeout.Token));

        while (checks.Count > 0)
        {
            var completed = await Task.WhenAny(checks);
            checks.Remove(completed);
            var result = await completed;
            if (result.Status.Online)
            {
                ConnectionHost = result.Host;
                timeout.Cancel();
                return result.Status;
            }
        }
        token.ThrowIfCancellationRequested();
        ConnectionHost = ServerHost;
        return apiStatus;
    }

    private async Task<ServerStatus> CheckStatusApiAsync(CancellationToken token)
    {
        try
        {
            using var response = await http.GetAsync($"{ApiBaseUrl}/api/v1/status", token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = document.RootElement;
            var minecraft = root.GetProperty("minecraft");
            var state = root.TryGetProperty("state", out var stateValue) ? stateValue.GetString() ?? "offline" : "offline";
            var stage = root.TryGetProperty("stage", out var stageValue) ? stageValue.GetString() ?? "unknown" : "unknown";
            DateTimeOffset? expectedUntil = null;
            if (root.TryGetProperty("expected_until", out var expected) && expected.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(expected.GetString(), out var parsed)) expectedUntil = parsed;
            return new ServerStatus(
                minecraft.GetProperty("reachable").GetBoolean(),
                minecraft.GetProperty("players_online").GetInt32(),
                minecraft.GetProperty("players_max").GetInt32(),
                root.TryGetProperty("installed_version", out var version) ? version.GetString() : null,
                state, stage, expectedUntil);
        }
        catch { return new ServerStatus(false, 0, 0, null); }
    }

    public async Task<LauncherUpdate?> GetRequiredLauncherUpdateAsync(string currentVersion, CancellationToken token = default)
    {
        using var response = await http.GetAsync($"{ApiBaseUrl}/api/v1/launcher", token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var launcher = document.RootElement.GetProperty("launcher");
        var version = launcher.GetProperty("version").GetString() ?? "";
        if (!Version.TryParse(version, out var available) || !Version.TryParse(currentVersion, out var current) || available <= current)
            return null;
        var update = new LauncherUpdate(
            version,
            launcher.GetProperty("installer_url").GetString() ?? "",
            launcher.GetProperty("signature_url").GetString() ?? "",
            launcher.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
            launcher.GetProperty("sha256").GetString() ?? "");
        if (!IsHttps(update.InstallerUrl) || !IsHttps(update.SignatureUrl) || string.IsNullOrWhiteSpace(update.Sha256) ||
            update.Size <= 0)
            throw new InvalidDataException($"Для обязательного обновления лаунчера {version} не опубликован установщик.");
        return update;
    }

    public async Task<string> DownloadLauncherUpdateAsync(LauncherUpdate update, CancellationToken token = default)
    {
        var directory = Path.Combine(RootDirectory, "updates");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"TFG-Launcher-Setup-{update.Version}.exe");
        await DownloadAsync(update.InstallerUrl, path, ("sha256", update.Sha256), null, token);
        if (new FileInfo(path).Length != update.Size)
        {
            File.Delete(path);
            throw new InvalidDataException("Размер обновления лаунчера не совпал с манифестом.");
        }
        var signature = await DownloadSignatureAsync(update.SignatureUrl, token);
        if (signature.Length is < 64 or > 256 || !UpdateSignature.Verify(path, signature))
        {
            File.Delete(path);
            throw new InvalidDataException("Криптографическая подпись обновления лаунчера недействительна.");
        }
        return path;
    }

    private async Task<byte[]> DownloadSignatureAsync(string url, CancellationToken token)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 256)
            throw new InvalidDataException("Файл подписи обновления слишком большой.");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        using var target = new MemoryStream(256);
        var buffer = new byte[257];
        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            target.Write(buffer, 0, read);
            if (target.Length > 256) throw new InvalidDataException("Файл подписи обновления слишком большой.");
        }
        return target.ToArray();
    }

    private static bool IsHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps;

    public static void StartInstaller(string path) => Process.Start(new ProcessStartInfo
    {
        FileName = path,
        Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TFGSELFUPDATE=1",
        UseShellExecute = true
    });

    private static async Task<(ServerStatus Status, string Host)> CheckDirectAsync(
        string host, CancellationToken token)
    {
        try { return (await ServerPing.QueryAsync(host, ServerPort, token), host); }
        catch { return (new ServerStatus(false, 0, 0, null), host); }
    }

    private async Task<(ServerStatus Status, string Host)> CheckApiAsync(string url, CancellationToken token)
    {
        try
        {
            using var response = await http.GetAsync(url, token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = document.RootElement;
            if (!root.GetProperty("online").GetBoolean())
                return (new ServerStatus(false, 0, 0, null), ServerHost);
            var players = root.GetProperty("players");
            var clean = root.GetProperty("motd").GetProperty("clean");
            var motd = clean.ValueKind == JsonValueKind.Array
                ? string.Join(" ", clean.EnumerateArray().Select(x => x.GetString()))
                : clean.GetString() ?? "";
            return (new ServerStatus(true,
                players.GetProperty("online").GetInt32(),
                players.GetProperty("max").GetInt32(),
                ServerPing.ExtractPackVersion(motd)), ServerHost);
        }
        catch { return (new ServerStatus(false, 0, 0, null), ServerHost); }
    }

    private static bool IsOnServerLan() => NetworkInterface.GetAllNetworkInterfaces()
        .SelectMany(x => x.GetIPProperties().UnicastAddresses)
        .Any(x => x.Address.AddressFamily == AddressFamily.InterNetwork &&
            x.Address.GetAddressBytes() is [192, 168, 1, _]);

    public async Task<ReleaseAsset> GetReleaseAssetAsync(string version, CancellationToken token = default)
    {
        var url = $"https://api.github.com/repos/TerraFirmaGreg-Team/Modpack-Modern/releases/tags/{Uri.EscapeDataString(version)}";
        await using var stream = await http.GetStreamAsync(url, token);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, token)
            ?? throw new InvalidDataException("GitHub вернул пустой ответ.");
        var suffix = $"-{version}-multimc.zip";
        var asset = release.Assets.FirstOrDefault(x => x.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"В релизе {version} отсутствует полный клиентский архив.");
        const string digestPrefix = "sha256:";
        if (!asset.Digest.StartsWith(digestPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub не предоставил SHA-256 клиентского архива.");
        return new ReleaseAsset(asset.Name, asset.Size, asset.DownloadUrl, asset.Digest[digestPrefix.Length..]);
    }

    public async Task InstallPackAsync(
        string version,
        ReleaseAsset asset,
        IProgress<LauncherProgress> progress,
        CancellationToken token = default)
    {
        EnsureFreeSpace(12L * 1024 * 1024 * 1024);
        var work = Path.Combine(RootDirectory, $".install-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(work, "pack.zip");
        var stagingPath = Path.Combine(work, "staging");
        Directory.CreateDirectory(stagingPath);

        try
        {
            progress.Report(new LauncherProgress(1, "Загрузка сборки..."));
            await DownloadAsync(asset.DownloadUrl, archivePath, ("sha256", asset.Sha256),
                (done, total) => progress.Report(new LauncherProgress(
                    total > 0 ? 1 + (int)(done * 19 / total) : 5,
                    $"Загрузка сборки: {FormatBytes(done)} / {FormatBytes(total)}")), token);

            string minecraftVersion;
            string forgeVersion;
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                var entry = archive.GetEntry("mmc-pack.json")
                    ?? throw new InvalidDataException("В клиентском архиве отсутствует mmc-pack.json.");
                await using var manifestStream = entry.Open();
                using var manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: token);
                var components = manifest.RootElement.GetProperty("components").EnumerateArray().ToList();
                minecraftVersion = GetComponentVersion(components, "net.minecraft");
                forgeVersion = GetComponentVersion(components, "net.minecraftforge");
            }

            progress.Report(new LauncherProgress(21, $"Установка Minecraft {minecraftVersion}..."));
            var launcher = CreateMinecraftLauncher(progress);
            await launcher.InstallAsync(minecraftVersion, token);

            progress.Report(new LauncherProgress(32, $"Установка Forge {forgeVersion}..."));
            var forgeVersionName = await InstallForgeSilentlyAsync(
                launcher, minecraftVersion, forgeVersion, progress, token);
            await launcher.InstallAsync(forgeVersionName, token);

            progress.Report(new LauncherProgress(48, "Распаковка полной клиентской сборки..."));
            ExtractMultiMcFiles(archivePath, stagingPath, progress);
            progress.Report(new LauncherProgress(92, "Применение обновления..."));
            CommitPack(stagingPath, new InstallationState
            {
                PackageRevision = CurrentPackageRevision,
                PackVersion = version,
                MinecraftVersion = minecraftVersion,
                ForgeVersion = forgeVersion,
                ForgeVersionName = forgeVersionName
            });
            progress.Report(new LauncherProgress(100, "Сборка готова."));
        }
        catch (Exception ex)
        {
            Log(ex);
            throw;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }
    }

    public async Task<RepairResult> VerifyInstallationAsync(CancellationToken token = default)
    {
        var state = LoadState();
        if (state.ManagedFiles.Count == 0 || state.ManagedHashes.Count == 0)
            return new RepairResult(false, ["Манифест установленной сборки отсутствует"]);
        var damaged = new List<string>();
        foreach (var relative in state.ManagedFiles)
        {
            token.ThrowIfCancellationRequested();
            var path = SafePath.Resolve(GameDirectory, relative);
            if (!File.Exists(path)) { damaged.Add(relative); continue; }
            await using var file = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            if (!state.ManagedHashes.TryGetValue(relative, out var expected) ||
                !actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) damaged.Add(relative);
        }
        return new RepairResult(damaged.Count == 0, damaged);
    }

    public string CreateDiagnosticsArchive(Exception? error = null)
    {
        var directory = Path.Combine(RootDirectory, "diagnostics");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"TFG-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddDiagnosticFile(archive, LogPath, "launcher.log");
        AddDiagnosticFile(archive, Path.Combine(GameDirectory, "logs", "latest.log"), "minecraft/latest.log");
        var crashDirectory = Path.Combine(GameDirectory, "crash-reports");
        if (Directory.Exists(crashDirectory))
            foreach (var crash in Directory.EnumerateFiles(crashDirectory, "*.txt").OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
                AddDiagnosticFile(archive, crash, $"minecraft/crash-reports/{Path.GetFileName(crash)}");
        var state = LoadState();
        var entry = archive.CreateEntry("diagnostic.json", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(JsonSerializer.Serialize(new
        {
            launcher_version = Application.ProductVersion,
            installation = new
            {
                state.PackVersion,
                state.MinecraftVersion,
                state.ForgeVersion,
                state.PackageRevision,
                managed_files = state.ManagedFiles.Count
            },
            os = Environment.OSVersion.ToString(),
            is_64_bit = Environment.Is64BitOperatingSystem,
            error = error?.ToString(),
            created_at = DateTimeOffset.Now
        }, JsonOptions));
        return path;
    }

    private static void AddDiagnosticFile(ZipArchive archive, string source, string name)
    {
        if (File.Exists(source)) archive.CreateEntryFromFile(source, name, CompressionLevel.Optimal);
    }

    public async Task<Process> LaunchAsync(string nickname, int maximumRamMb, CancellationToken token = default)
    {
        var process = await BuildProcessAsync(nickname, maximumRamMb, token);
        process.EnableRaisingEvents = true;
        process.Start();
        return process;
    }

    internal async Task<Process> BuildProcessAsync(
        string nickname, int maximumRamMb, CancellationToken token = default)
    {
        var state = LoadState();
        if (string.IsNullOrWhiteSpace(state.ForgeVersionName))
            throw new InvalidOperationException("Сборка ещё не установлена.");

        EnsureDefaultLanguage();
        EnsureDefaultServer();
        var path = new MinecraftPath(GameDirectory);
        var parameters = MinecraftLauncherParameters.CreateDefault(path);
        parameters.VersionLoader = new LocalJsonVersionLoader(path);
        var launcher = new MinecraftLauncher(parameters);
        var process = await launcher.BuildProcessAsync(state.ForgeVersionName, new MLaunchOption
        {
            Session = MSession.CreateOfflineSession(nickname),
            MinimumRamMb = 2048,
            MaximumRamMb = maximumRamMb,
            ServerIp = ConnectionHost,
            ServerPort = ServerPort,
            GameLauncherName = "TFG Launcher",
            GameLauncherVersion = Application.ProductVersion
        }, token);
        return process;
    }

    internal void EnsureDefaultLanguage()
    {
        var path = Path.Combine(GameDirectory, "options.txt");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        if (lines.Any(x => x.StartsWith("lang:", StringComparison.OrdinalIgnoreCase))) return;
        lines.Add("lang:ru_ru");
        File.WriteAllLines(path, lines);
    }

    internal void EnsureDefaultServer()
    {
        var path = Path.Combine(GameDirectory, "servers.dat");
        if (!File.Exists(path))
        {
            using var output = new MemoryStream();
            output.Write([10, 0, 0, 9]);
            WriteNbtString(output, "servers");
            output.WriteByte(10);
            WriteBigEndianInt(output, 1);
            WriteServerEntry(output);
            output.WriteByte(0);
            SaveBytesAtomic(path, output.ToArray());
            return;
        }

        var data = File.ReadAllBytes(path);
        var address = Encoding.UTF8.GetBytes(ServerHost);
        var addressIndex = data.AsSpan().IndexOf(address);
        if (addressIndex >= 0)
        {
            var hidden = Encoding.ASCII.GetBytes("hidden");
            var tail = data.AsSpan(addressIndex + address.Length);
            var hiddenIndex = tail.IndexOf(hidden);
            if (hiddenIndex >= 3 && hiddenIndex + hidden.Length < tail.Length &&
                tail[hiddenIndex - 3] == 1 && tail[hiddenIndex - 2] == 0 && tail[hiddenIndex - 1] == hidden.Length)
            {
                tail[hiddenIndex + hidden.Length] = 0;
                SaveBytesAtomic(path, data);
            }
            return;
        }

        ReadOnlySpan<byte> header = [10, 0, 0, 9, 0, 7, (byte)'s', (byte)'e', (byte)'r', (byte)'v', (byte)'e', (byte)'r', (byte)'s', 10];
        if (data.Length < header.Length + 5 || !data.AsSpan().StartsWith(header) || data[^1] != 0) return;
        var count = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(header.Length, 4));
        using var updated = new MemoryStream();
        updated.Write(data, 0, data.Length - 1);
        WriteServerEntry(updated);
        updated.WriteByte(0);
        var result = updated.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(header.Length, 4), count + 1);
        SaveBytesAtomic(path, result);
    }

    private static void WriteServerEntry(Stream output)
    {
        output.WriteByte(8);
        WriteNbtString(output, "name");
        WriteNbtString(output, "TerraFirmaGreg");
        output.WriteByte(8);
        WriteNbtString(output, "ip");
        WriteNbtString(output, $"{ServerHost}:{ServerPort}");
        output.WriteByte(1);
        WriteNbtString(output, "hidden");
        output.WriteByte(0);
        output.WriteByte(1);
        WriteNbtString(output, "preventsChatReports");
        output.WriteByte(0);
        output.WriteByte(0);
    }

    private static void WriteNbtString(Stream output, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
        output.Write(length);
        output.Write(bytes);
    }

    private static void WriteBigEndianInt(Stream output, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void SaveBytesAtomic(string path, byte[] data)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, true);
    }

    private MinecraftLauncher CreateMinecraftLauncher(IProgress<LauncherProgress> progress)
    {
        var launcher = new MinecraftLauncher(new MinecraftPath(GameDirectory));
        launcher.FileProgressChanged += (_, e) =>
            progress.Report(new LauncherProgress(25, e.Name ?? "Установка Minecraft..."));
        return launcher;
    }

    private async Task<string> InstallForgeSilentlyAsync(
        MinecraftLauncher launcher,
        string minecraftVersion,
        string forgeVersion,
        IProgress<LauncherProgress> progress,
        CancellationToken token)
    {
        var versions = await new ForgeVersionLoader(http).GetForgeVersions(minecraftVersion);
        var selected = versions.FirstOrDefault(x => x.ForgeVersionName == forgeVersion)
            ?? throw new InvalidOperationException($"Forge {forgeVersion} не найден.");
        var installer = new ForgeInstallerVersionMapper().CreateInstaller(selected);

        try
        {
            _ = await launcher.GetVersionAsync(installer.VersionName, token);
            return installer.VersionName;
        }
        catch (KeyNotFoundException) { }

        var vanilla = await launcher.GetVersionAsync(minecraftVersion, token);
        var javaPath = launcher.GetJavaPath(vanilla);
        if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
            throw new InvalidOperationException("Не удалось установить Java для Minecraft.");

        await installer.Install(launcher.MinecraftPath, launcher.GameInstaller, new ForgeInstallOptions
        {
            JavaPath = javaPath,
            CancellationToken = token,
            InstallerOutput = new Progress<string>(message =>
                progress.Report(new LauncherProgress(35, message)))
        });
        _ = await launcher.GetAllVersionsAsync(token);
        return installer.VersionName;
    }

    private async Task DownloadAsync(
        string url,
        string destination,
        (string Algorithm, string Hash)? expectedHash,
        Action<long, long>? progress,
        CancellationToken token)
    {
        var part = destination + ".part";
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
            var buffer = new byte[1024 * 128];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                progress?.Invoke(done, total);
            }
            await target.FlushAsync(token);
            target.Close();

            if (expectedHash is { } expected)
            {
                await using var file = File.OpenRead(part);
                var actual = expected.Algorithm switch
                {
                    "sha512" => Convert.ToHexString(await SHA512.HashDataAsync(file, token)),
                    "sha256" => Convert.ToHexString(await SHA256.HashDataAsync(file, token)),
                    _ => Convert.ToHexString(await SHA1.HashDataAsync(file, token))
                };
                if (!actual.Equals(expected.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Контрольная сумма не совпала: {Path.GetFileName(destination)}");
            }
            File.Move(part, destination, true);
        }
        finally { try { File.Delete(part); } catch { } }
    }

    private static string GetComponentVersion(IEnumerable<JsonElement> components, string uid)
    {
        var component = components.FirstOrDefault(x =>
            x.TryGetProperty("uid", out var value) && value.GetString() == uid);
        if (component.ValueKind == JsonValueKind.Undefined ||
            !component.TryGetProperty("version", out var version) || string.IsNullOrWhiteSpace(version.GetString()))
            throw new InvalidDataException($"В сборке не указана версия {uid}.");
        return version.GetString()!;
    }

    private static void ExtractMultiMcFiles(
        string archivePath,
        string stagingPath,
        IProgress<LauncherProgress> progress)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var files = archive.Entries
            .Where(x => x.FullName.StartsWith(".minecraft/", StringComparison.OrdinalIgnoreCase) &&
                !x.FullName.EndsWith('/'))
            .ToList();
        for (var index = 0; index < files.Count; index++)
        {
            var entry = files[index];
            var relative = entry.FullName[".minecraft/".Length..];
            var destination = SafePath.Resolve(stagingPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
            if (index % 50 == 0)
                progress.Report(new LauncherProgress(48 + index * 42 / Math.Max(1, files.Count),
                    $"Распаковка: {index + 1} / {files.Count}"));
        }
    }

    internal void CommitPack(string stagingPath, InstallationState newState)
    {
        var oldState = LoadState();
        var newFiles = Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(stagingPath, path).Replace('\\', '/'))
            .Where(path => !IsProtected(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        newState.ManagedFiles = newFiles;

        var affected = oldState.ManagedFiles.Concat(newFiles)
            .Where(path => !IsProtected(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var backup = Path.Combine(RootDirectory, $".backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backup);

        try
        {
            foreach (var relative in affected)
            {
                var target = SafePath.Resolve(GameDirectory, relative);
                if (!File.Exists(target)) continue;
                var saved = SafePath.Resolve(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                File.Move(target, saved, true);
            }
            foreach (var relative in newFiles)
            {
                var source = SafePath.Resolve(stagingPath, relative);
                var target = SafePath.Resolve(GameDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target, true);
            }
            newState.ManagedHashes = newFiles.ToDictionary(
                relative => relative,
                relative => ComputeSha256(SafePath.Resolve(GameDirectory, relative)),
                StringComparer.OrdinalIgnoreCase);
            SaveJsonAtomic(StatePath, newState);
        }
        catch
        {
            foreach (var relative in affected)
            {
                var target = SafePath.Resolve(GameDirectory, relative);
                try { File.Delete(target); } catch { }
                var saved = SafePath.Resolve(backup, relative);
                if (!File.Exists(saved)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(saved, target, true);
            }
            throw;
        }
        try { Directory.Delete(backup, true); } catch { }
    }

    private static bool IsProtected(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return ProtectedFiles.Contains(normalized, StringComparer.OrdinalIgnoreCase) ||
            ProtectedDirectories.Any(x => normalized.StartsWith(x, StringComparison.OrdinalIgnoreCase));
    }

    private static string ComputeSha256(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private void EnsureFreeSpace(long requiredBytes)
    {
        var root = Path.GetPathRoot(RootDirectory) ?? throw new IOException("Не удалось определить диск.");
        if (new DriveInfo(root).AvailableFreeSpace < requiredBytes)
            throw new IOException("Для установки требуется не менее 12 ГБ свободного места.");
    }

    private T LoadJson<T>(string path, T fallback)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? fallback : fallback; }
        catch (Exception ex) { Log(ex); return fallback; }
    }

    private static void SaveJsonAtomic<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temp, path, true);
    }

    public void Log(Exception exception)
    {
        try { File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:u}] {exception}\n\n"); } catch { }
    }

    private static string FormatBytes(long value) => value <= 0 ? "?" : $"{value / 1024d / 1024d:N0} МБ";
}
