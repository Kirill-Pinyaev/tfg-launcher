namespace TFGLauncher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            SelfTest.Run();
            return;
        }
        if (args.Contains("--status-test", StringComparer.OrdinalIgnoreCase))
        {
            var status = new LauncherService().GetServerStatusAsync().GetAwaiter().GetResult();
            Console.WriteLine($"online={status.Online} players={status.Players}/{status.MaxPlayers} pack={status.PackVersion}");
            return;
        }
        if (args.Contains("--build-test", StringComparer.OrdinalIgnoreCase))
        {
            using var process = new LauncherService().BuildProcessAsync("LauncherTest", 8192).GetAwaiter().GetResult();
            Console.WriteLine($"build=ok file={process.StartInfo.FileName}");
            return;
        }
        if (args.Length == 3 && args[0].Equals("--verify-update", StringComparison.OrdinalIgnoreCase))
        {
            var valid = UpdateSignature.Verify(args[1], File.ReadAllBytes(args[2]));
            Console.WriteLine($"signature={valid}");
            Environment.ExitCode = valid ? 0 : 1;
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
