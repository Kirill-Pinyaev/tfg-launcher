using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TFGLauncher;

internal static partial class MemoryPolicy
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    public static int DetectMaximumRamMb()
    {
        return MaximumRamMb(TotalPhysicalBytes());
    }

    public static ulong TotalPhysicalBytes()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new InvalidOperationException("Не удалось определить объём оперативной памяти.");
        return status.TotalPhysical;
    }

    internal static int MaximumRamMb(ulong totalBytes)
    {
        var roundedGiB = (int)Math.Round(totalBytes / (double)GiB, MidpointRounding.AwayFromZero);
        if (roundedGiB < 8)
            throw new InvalidOperationException("Для TerraFirmaGreg требуется не менее 8 ГБ оперативной памяти.");
        return roundedGiB == 8 ? 6144 : 8192;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

internal static partial class InputRules
{
    [GeneratedRegex("^[A-Za-z0-9_-]{3,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex NicknameRegex();

    public static bool IsValidNickname(string value) => NicknameRegex().IsMatch(value);

    public static bool TryParseServer(string? value, out ServerEndpoint endpoint)
    {
        endpoint = new ServerEndpoint(LauncherService.ServerHost, LauncherService.ServerPort);
        var parts = (value ?? "").Trim().Split(':');
        if (parts.Length is < 1 or > 2 || string.IsNullOrWhiteSpace(parts[0])) return false;
        var host = parts[0].Trim();
        if (!IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) == UriHostNameType.Unknown) return false;
        ushort port = LauncherService.ServerPort;
        if (parts.Length == 2 && (!ushort.TryParse(parts[1], out port) || port == 0)) return false;
        endpoint = new ServerEndpoint(host, port);
        return true;
    }
}

internal static partial class SkinCommands
{
    [GeneratedRegex("^[A-Za-z0-9_]{1,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex AccountRegex();

    public static string Build(string provider, string value, bool slim)
    {
        if (provider is "Mojang" or "Ely.by")
        {
            if (!AccountRegex().IsMatch(value))
                throw new ArgumentException("Введите имя аккаунта: 1–16 латинских букв, цифр или '_'.");
            return $"/skin set {provider.ToLowerInvariant()} {value}";
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || value.Contains('"'))
            throw new ArgumentException("Укажите прямую HTTPS-ссылку на PNG без кавычек.");
        return $"/skin set web {(slim ? "slim" : "classic")} \"{value}\"";
    }
}

internal static partial class ServerPing
{
    [GeneratedRegex(@"\[TFG:(\d+\.\d+\.\d+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackVersionRegex();

    public static string? ExtractPackVersion(string text) =>
        PackVersionRegex().Match(text) is { Success: true } match ? match.Groups[1].Value : null;

    public static async Task<ServerStatus> QueryAsync(string host, ushort port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var client = new TcpClient();
        if (IPAddress.TryParse(host, out var address))
            await client.ConnectAsync(address, port, timeout.Token);
        else
            await client.ConnectAsync(host, port, timeout.Token);
        await using var stream = client.GetStream();

        using (var handshake = new MemoryStream())
        {
            WriteVarInt(handshake, 0);
            WriteVarInt(handshake, 763);
            WriteString(handshake, host);
            var portBytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(portBytes, port);
            handshake.Write(portBytes);
            WriteVarInt(handshake, 1);
            await WritePacketAsync(stream, handshake.ToArray(), timeout.Token);
        }

        await WritePacketAsync(stream, [0], timeout.Token);
        _ = await ReadVarIntAsync(stream, timeout.Token);
        if (await ReadVarIntAsync(stream, timeout.Token) != 0)
            throw new InvalidDataException("Некорректный ответ сервера.");
        var jsonLength = await ReadVarIntAsync(stream, timeout.Token);
        if (jsonLength <= 0 || jsonLength > 1_000_000)
            throw new InvalidDataException("Некорректная длина ответа сервера.");

        var jsonBytes = new byte[jsonLength];
        await stream.ReadExactlyAsync(jsonBytes, timeout.Token);
        var json = Encoding.UTF8.GetString(jsonBytes);
        using var document = JsonDocument.Parse(json);
        var players = document.RootElement.GetProperty("players");
        return new ServerStatus(
            true,
            players.GetProperty("online").GetInt32(),
            players.GetProperty("max").GetInt32(),
            ExtractPackVersion(json));
    }

    private static async Task WritePacketAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        await stream.WriteAsync(packet.ToArray(), token);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        do
        {
            var current = (byte)(value & 0x7F);
            value = (int)((uint)value >> 7);
            if (value != 0) current |= 0x80;
            stream.WriteByte(current);
        } while (value != 0);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken token)
    {
        var value = 0;
        for (var position = 0; position < 35; position += 7)
        {
            var buffer = new byte[1];
            await stream.ReadExactlyAsync(buffer, token);
            value |= (buffer[0] & 0x7F) << position;
            if ((buffer[0] & 0x80) == 0) return value;
        }
        throw new InvalidDataException("VarInt слишком длинный.");
    }
}

internal static class SafePath
{
    public static string Resolve(string root, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var rootPath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root, normalized));
        if (!result.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Небезопасный путь в сборке: {relativePath}");
        return result;
    }
}

internal static class UpdateSignature
{
    private const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE6IaYPxKZYX5scI+vux3iocLQcN+D6oou0V/hMJXdGrvAtlILLZjrwY2xYbanAyO+qIUQRE3cgILfSwrQc3upFA==";

    public static bool Verify(string path, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
            using var file = File.OpenRead(path);
            return key.VerifyData(file, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException) { return false; }
    }
}

internal static class SelfTest
{
    public static void Run()
    {
        const ulong gib = 1024UL * 1024 * 1024;
        if (MemoryPolicy.MaximumRamMb(8 * gib) != 6144) throw new Exception("8 GB memory rule failed");
        if (MemoryPolicy.MaximumRamMb(16 * gib) != 8192) throw new Exception("16 GB memory rule failed");
        try { _ = MemoryPolicy.MaximumRamMb(7 * gib); throw new Exception("Low memory rule failed"); }
        catch (InvalidOperationException) { }
        if (ServerPing.ExtractPackVersion("Modern [TFG:0.13.7]") != "0.13.7") throw new Exception("MOTD parser failed");
        if (!LauncherService.TryParseReleaseVersion("1.5.1+9f9175d5f99a6937632ca4f828e3eab4ae581a5d", out var sdkStamped) ||
            sdkStamped != new Version(1, 5, 1)) throw new Exception("SDK-stamped version parsing failed");
        if (!LauncherService.TryParseReleaseVersion("1.5.2", out var plain) || plain != new Version(1, 5, 2))
            throw new Exception("Plain version parsing failed");
        if (!InputRules.IsValidNickname("katushka-s-tokom")) throw new Exception("Nickname rule failed");
        if (!InputRules.TryParseServer("192.168.1.78:25570", out var endpoint) ||
            endpoint.Host != "192.168.1.78" || endpoint.Port != 25570) throw new Exception("Server address rule failed");
        if (InputRules.TryParseServer("bad host:99999", out _)) throw new Exception("Invalid server address rule failed");
        if (SkinCommands.Build("Mojang", "Notch", false) != "/skin set mojang Notch") throw new Exception("Skin command failed");
        if (SkinCommands.Build("URL", "https://example.org/skin.png", true) != "/skin set web slim \"https://example.org/skin.png\"")
            throw new Exception("Web skin command failed");
        try { _ = SafePath.Resolve(Path.GetTempPath(), "../bad"); throw new Exception("Path rule failed"); }
        catch (InvalidDataException) { }

        var root = Path.Combine(Path.GetTempPath(), $"tfg-launcher-test-{Guid.NewGuid():N}");
        try
        {
            var service = new LauncherService(root);
            Directory.CreateDirectory(Path.Combine(service.GameDirectory, "mods"));
            File.WriteAllText(Path.Combine(service.GameDirectory, "mods", "old.jar"), "old");
            File.WriteAllText(Path.Combine(service.GameDirectory, "options.txt"), "keep");
            File.WriteAllText(Path.Combine(root, "installation.json"),
                "{\"PackVersion\":\"old\",\"ManagedFiles\":[\"mods/old.jar\"]}");
            var staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(Path.Combine(staging, "mods"));
            File.WriteAllText(Path.Combine(staging, "mods", "new.jar"), "new");
            File.WriteAllText(Path.Combine(staging, "options.txt"), "replace");
            service.CommitPack(staging, new InstallationState { PackVersion = "new" });
            if (File.Exists(Path.Combine(service.GameDirectory, "mods", "old.jar"))) throw new Exception("Stale file cleanup failed");
            if (!File.Exists(Path.Combine(service.GameDirectory, "mods", "new.jar"))) throw new Exception("Pack commit failed");
            if (File.ReadAllText(Path.Combine(service.GameDirectory, "options.txt")) != "keep") throw new Exception("Protected file failed");
            service.EnsureDefaultLanguage();
            if (!File.ReadAllLines(Path.Combine(service.GameDirectory, "options.txt")).Contains("lang:ru_ru"))
                throw new Exception("Default language failed");
            File.WriteAllText(Path.Combine(service.GameDirectory, "options.txt"), "lang:en_us");
            service.EnsureDefaultLanguage();
            if (File.ReadAllText(Path.Combine(service.GameDirectory, "options.txt")) != "lang:en_us")
                throw new Exception("Selected language preservation failed");
            service.EnsureDefaultServer();
            var servers = File.ReadAllBytes(Path.Combine(service.GameDirectory, "servers.dat"));
            if (!Encoding.UTF8.GetString(servers).Contains("77.51.139.159:25565"))
                throw new Exception("Default server failed");
            var hiddenValue = servers.AsSpan().IndexOf(Encoding.ASCII.GetBytes("hidden")) + "hidden".Length;
            servers[hiddenValue] = 1;
            File.WriteAllBytes(Path.Combine(service.GameDirectory, "servers.dat"), servers);
            service.EnsureDefaultServer();
            if (File.ReadAllBytes(Path.Combine(service.GameDirectory, "servers.dat"))[hiddenValue] != 0)
                throw new Exception("Hidden server failed");
            var serverLength = servers.Length;
            service.EnsureDefaultServer();
            if (File.ReadAllBytes(Path.Combine(service.GameDirectory, "servers.dat")).Length != serverLength)
                throw new Exception("Duplicate server failed");
            service.SaveServerAddress("192.168.1.78:25570");
            service.EnsureDefaultServer();
            if (!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(service.GameDirectory, "servers.dat")))
                    .Contains("192.168.1.78:25570")) throw new Exception("Configured server failed");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        Console.WriteLine("Self-test passed.");
    }
}
