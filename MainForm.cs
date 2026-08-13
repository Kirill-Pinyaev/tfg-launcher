using System.Diagnostics;

namespace TFGLauncher;

internal sealed class MainForm : Form
{
    private readonly LauncherService service = new();
    private readonly TextBox nicknameBox = new();
    private readonly Label serverLabel = new();
    private readonly Label versionLabel = new();
    private readonly Label statusLabel = new();
    private readonly Label footerLabel = new();
    private readonly ProgressBar progressBar = new();
    private readonly Button playButton = new();
    private readonly Button skinButton = new();
    private readonly Button repairButton = new();
    private readonly Button diagnosticsButton = new();
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 30_000 };
    private ServerStatus serverStatus = new(false, 0, 0, null);
    private Process? gameProcess;
    private bool updateBlocked;

    public MainForm()
    {
        Text = "TFG Launcher";
        ClientSize = new Size(500, 425);
        MinimumSize = MaximumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.FromArgb(20, 20, 22);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 10F);

        var title = MakeLabel("TerraFirmaGreg", 26F, FontStyle.Bold);
        title.Location = new Point(32, 26);
        title.AutoSize = true;

        serverLabel.Location = new Point(35, 82);
        serverLabel.Size = new Size(430, 24);
        serverLabel.ForeColor = Color.Silver;
        serverLabel.Text = "Сервер: проверка...";

        versionLabel.Location = new Point(35, 108);
        versionLabel.Size = new Size(430, 24);
        versionLabel.ForeColor = Color.Silver;

        var nicknameLabel = MakeLabel("Ник", 9F, FontStyle.Regular);
        nicknameLabel.Location = new Point(35, 148);
        nicknameLabel.AutoSize = true;
        nicknameLabel.ForeColor = Color.Silver;

        nicknameBox.Location = new Point(35, 170);
        nicknameBox.Size = new Size(430, 30);
        nicknameBox.BackColor = Color.FromArgb(38, 38, 42);
        nicknameBox.ForeColor = Color.White;
        nicknameBox.BorderStyle = BorderStyle.FixedSingle;
        nicknameBox.MaxLength = 16;

        skinButton.SetBounds(35, 214, 135, 34);
        repairButton.SetBounds(180, 214, 135, 34);
        diagnosticsButton.SetBounds(325, 214, 140, 34);
        foreach (var button in new[] { skinButton, repairButton, diagnosticsButton })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(38, 38, 42);
            button.ForeColor = Color.WhiteSmoke;
        }
        skinButton.Text = "СКИН";
        skinButton.Click += (_, _) => new SkinForm().ShowDialog(this);
        repairButton.Text = "ПРОВЕРИТЬ";
        repairButton.Click += async (_, _) => await RepairAsync(true);
        diagnosticsButton.Text = "ДИАГНОСТИКА";
        diagnosticsButton.Click += (_, _) => ShowDiagnostics();

        playButton.Location = new Point(35, 262);
        playButton.Size = new Size(430, 52);
        playButton.FlatStyle = FlatStyle.Flat;
        playButton.FlatAppearance.BorderSize = 0;
        playButton.BackColor = Color.FromArgb(221, 139, 38);
        playButton.ForeColor = Color.Black;
        playButton.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
        playButton.Text = "ИГРАТЬ";
        playButton.Cursor = Cursors.Hand;
        playButton.Click += async (_, _) => await PlayAsync();

        progressBar.Location = new Point(35, 328);
        progressBar.Size = new Size(430, 8);
        progressBar.Style = ProgressBarStyle.Continuous;
        progressBar.Visible = false;

        statusLabel.Location = new Point(35, 344);
        statusLabel.Size = new Size(430, 24);
        statusLabel.ForeColor = Color.Gray;
        statusLabel.TextAlign = ContentAlignment.MiddleCenter;

        footerLabel.Location = new Point(35, 390);
        footerLabel.Size = new Size(430, 20);
        footerLabel.ForeColor = Color.DimGray;
        footerLabel.Font = new Font("Segoe UI", 8F);
        footerLabel.TextAlign = ContentAlignment.MiddleCenter;

        Controls.AddRange([title, serverLabel, versionLabel, nicknameLabel, nicknameBox,
            skinButton, repairButton, diagnosticsButton, playButton, progressBar, statusLabel, footerLabel]);
        var settings = service.LoadSettings();
        nicknameBox.Text = settings.Nickname;
        UpdateVersionLabel();

        Shown += async (_, _) =>
        {
            await CheckSelfUpdateAsync();
            if (!updateBlocked) await RefreshServerAsync();
        };
        refreshTimer.Tick += async (_, _) => await RefreshServerAsync();
        refreshTimer.Start();
        FormClosing += (_, _) => service.SaveSettings(new LauncherSettings { Nickname = nicknameBox.Text.Trim() });
    }

    private static Label MakeLabel(string text, float size, FontStyle style) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", size, style),
        ForeColor = Color.White
    };

    private async Task RefreshServerAsync()
    {
        serverStatus = await service.GetServerStatusAsync();
        serverLabel.Text = serverStatus.Online
            ? $"Сервер онлайн  •  {serverStatus.Players} / {serverStatus.MaxPlayers} игроков"
            : ServerStateText(serverStatus);
        serverLabel.ForeColor = serverStatus.Online ? Color.FromArgb(111, 207, 151) : Color.FromArgb(220, 100, 100);
        UpdateVersionLabel();
    }

    private static string ServerStateText(ServerStatus status) => status.State switch
    {
        "starting" => "Сервер запускается",
        "restarting" => "Сервер перезапускается",
        "updating" => "Сервер обновляется",
        "rollback" => "Выполняется откат сервера",
        _ => "Сервер недоступен"
    } + (status.ExpectedUntil is { } until ? $"  •  до {until.ToLocalTime():HH:mm}" : "");

    private async Task CheckSelfUpdateAsync()
    {
        LauncherUpdate? update;
        try
        {
            update = await service.GetRequiredLauncherUpdateAsync(Application.ProductVersion);
        }
        catch (HttpRequestException) { return; }
        catch (TaskCanceledException) { return; }
        try
        {
            if (update is null) return;
            SetBusy(true, $"Обязательное обновление лаунчера до {update.Version}...");
            var installer = await service.DownloadLauncherUpdateAsync(update);
            LauncherService.StartInstaller(installer);
            Application.Exit();
        }
        catch (Exception ex)
        {
            updateBlocked = true;
            service.Log(ex);
            ShowError(ex);
        }
        finally { if (!updateBlocked) SetBusy(false, ""); }
    }

    private void UpdateVersionLabel()
    {
        var installed = service.LoadState().PackVersion;
        var required = serverStatus.PackVersion;
        versionLabel.Text = $"Сборка: {(string.IsNullOrEmpty(installed) ? "не установлена" : installed)}" +
            (string.IsNullOrEmpty(required) ? "" : $"  •  сервер: {required}");
        footerLabel.Text = $"Лаунчер {Application.ProductVersion}  •  Сборка {(string.IsNullOrEmpty(installed) ? "—" : installed)}";
    }

    private async Task PlayAsync()
    {
        if (gameProcess is { HasExited: false }) return;
        var nickname = nicknameBox.Text.Trim();
        if (!InputRules.IsValidNickname(nickname))
        {
            MessageBox.Show("Ник должен содержать 3–16 латинских букв, цифр, '_' или '-'.",
                "Некорректный ник", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int memory;
        try { memory = MemoryPolicy.DetectMaximumRamMb(); }
        catch (Exception ex) { ShowError(ex); return; }

        service.SaveSettings(new LauncherSettings { Nickname = nickname });
        SetBusy(true, "Проверка версии...");
        try
        {
            serverStatus = await service.GetServerStatusAsync();
            var installed = service.LoadState().PackVersion;
            var packageRevision = service.LoadState().PackageRevision;
            var target = serverStatus.PackVersion ?? (string.IsNullOrEmpty(installed)
                ? LauncherService.InitialPackVersion
                : installed);

            if (!string.Equals(installed, target, StringComparison.OrdinalIgnoreCase) ||
                packageRevision < LauncherService.CurrentPackageRevision)
            {
                var asset = await service.GetReleaseAssetAsync(target);
                SetBusy(false, "");
                var answer = MessageBox.Show(
                    $"Для входа требуется TerraFirmaGreg {target}.\nРазмер пакета: {asset.Size / 1024d / 1024d:N0} МБ.\n\nУстановить обновление?",
                    "Обновление сборки", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer != DialogResult.Yes) return;

                SetBusy(true, "Подготовка установки...");
                var progress = new Progress<LauncherProgress>(value =>
                {
                    progressBar.Value = Math.Clamp(value.Percent, 0, 100);
                    statusLabel.Text = value.Message;
                });
                await service.InstallPackAsync(target, asset, progress);
                UpdateVersionLabel();
            }
            else if (!await RepairAsync(false)) return;

            statusLabel.Text = $"Запуск с {memory / 1024} ГБ памяти...";
            gameProcess = await service.LaunchAsync(nickname, memory);
            playButton.Text = "ИГРА ЗАПУЩЕНА";
            WindowState = FormWindowState.Minimized;
            await gameProcess.WaitForExitAsync();
            WindowState = FormWindowState.Normal;
            Activate();
        }
        catch (Exception ex)
        {
            service.Log(ex);
            ShowError(ex);
        }
        finally
        {
            gameProcess = null;
            playButton.Text = "ИГРАТЬ";
            SetBusy(false, "");
        }
    }

    private void SetBusy(bool busy, string status)
    {
        playButton.Enabled = !busy;
        nicknameBox.Enabled = !busy;
        skinButton.Enabled = !busy;
        repairButton.Enabled = !busy;
        diagnosticsButton.Enabled = !busy;
        progressBar.Visible = busy;
        if (busy && progressBar.Value == 0) progressBar.Value = 1;
        if (!busy) progressBar.Value = 0;
        statusLabel.Text = status;
        UseWaitCursor = busy;
    }

    private async Task<bool> RepairAsync(bool showHealthy)
    {
        SetBusy(true, "Проверка файлов сборки...");
        try
        {
            var result = await service.VerifyInstallationAsync();
            if (result.Healthy)
            {
                if (showHealthy) MessageBox.Show("Файлы сборки исправны.", "Проверка", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            var state = service.LoadState();
            if (string.IsNullOrWhiteSpace(state.PackVersion))
            {
                if (showHealthy) MessageBox.Show("Сборка ещё не установлена.", "Проверка", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (showHealthy && MessageBox.Show(
                    $"Обнаруждены повреждённые файлы ({result.DamagedFiles.Count}). Переустановить сборку {state.PackVersion}?",
                    "Восстановление", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
            var asset = await service.GetReleaseAssetAsync(state.PackVersion);
            var progress = new Progress<LauncherProgress>(value =>
            {
                progressBar.Value = Math.Clamp(value.Percent, 0, 100);
                statusLabel.Text = value.Message;
            });
            await service.InstallPackAsync(state.PackVersion, asset, progress);
            return true;
        }
        catch (Exception ex) { service.Log(ex); ShowError(ex); return false; }
        finally { SetBusy(false, ""); }
    }

    private void ShowDiagnostics()
    {
        try
        {
            var path = service.CreateDiagnosticsArchive();
            MessageBox.Show($"Диагностика сохранена:\n{path}", "TFG Launcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { service.Log(ex); ShowError(ex); }
    }

    private void ShowError(Exception exception)
    {
        string diagnostics;
        try { diagnostics = service.CreateDiagnosticsArchive(exception); }
        catch { diagnostics = "launcher.log"; }
        MessageBox.Show($"{exception.Message}\n\nДиагностика сохранена:\n{diagnostics}",
            "TFG Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
