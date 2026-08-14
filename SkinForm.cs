namespace TFGLauncher;

internal sealed class SkinForm : Form
{
    private readonly LauncherService service;
    private readonly ComboBox provider = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox value = new();
    private readonly CheckBox slim = new() { Text = "Тонкие руки (Slim)", AutoSize = true };
    private readonly Label status = new() { ForeColor = Color.Silver };
    private readonly PictureBox preview = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(30, 30, 34) };
    private byte[]? upload;

    public SkinForm(LauncherService service)
    {
        this.service = service;
        Text = "Скин";
        ClientSize = new Size(520, 350);
        MinimumSize = MaximumSize = Size;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(20, 20, 22);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 10F);

        Controls.Add(new Label { Text = "Управление скином", Font = new Font("Segoe UI", 16F, FontStyle.Bold), AutoSize = true, Location = new Point(24, 20) });
        provider.Items.AddRange(["Mojang", "Ely.by", "HTTPS URL", "PNG файл"]);
        provider.SelectedIndex = 0;
        provider.SetBounds(24, 66, 140, 30);
        provider.SelectedIndexChanged += (_, _) => UpdateMode();
        value.SetBounds(174, 66, 200, 30);
        slim.SetBounds(174, 106, 180, 25);
        preview.SetBounds(390, 66, 100, 100);
        status.SetBounds(24, 145, 350, 60);
        var choose = MakeButton("ВЫБРАТЬ PNG", 24, 210, 145);
        choose.Click += (_, _) => ChoosePng();
        var apply = MakeButton("ПРИМЕНИТЬ", 179, 210, 145);
        apply.Click += async (_, _) => await ApplyAsync();
        var reset = MakeButton("СБРОСИТЬ", 334, 210, 156);
        reset.Click += async (_, _) => await ApplyAsync(true);
        Controls.AddRange([provider, value, slim, preview, status, choose, apply, reset]);
        Shown += async (_, _) => await RefreshAsync();
        UpdateMode();
    }

    private Button MakeButton(string text, int x, int y, int width) => new()
    {
        Text = text, Location = new Point(x, y), Size = new Size(width, 46), FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(221, 139, 38), ForeColor = Color.Black, Font = new Font("Segoe UI", 9F, FontStyle.Bold)
    };

    private void UpdateMode()
    {
        value.Enabled = provider.SelectedIndex != 3;
        value.PlaceholderText = provider.SelectedIndex == 2 ? "https://.../skin.png" : "Имя аккаунта";
        slim.Visible = provider.SelectedIndex >= 2;
        status.Text = provider.SelectedIndex == 3 ? (upload is null ? "Выберите PNG 64×64, не более 1 МиБ." : "PNG выбран.") : "";
    }

    private void ChoosePng()
    {
        using var dialog = new OpenFileDialog { Filter = "PNG (*.png)|*.png", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var data = File.ReadAllBytes(dialog.FileName);
        if (data.Length > 1024 * 1024) { MessageBox.Show("PNG больше 1 МиБ."); return; }
        upload = data;
        provider.SelectedIndex = 3;
        using var stream = new MemoryStream(data);
        preview.Image?.Dispose();
        using var loaded = new Bitmap(stream);
        preview.Image = new Bitmap(loaded);
        UpdateMode();
    }

    private async Task ApplyAsync(bool reset = false)
    {
        try
        {
            UseWaitCursor = true;
            var source = reset ? "reset" : provider.SelectedIndex switch { 0 => "mojang", 1 => "ely_by", 2 => "url", _ => "upload" };
            var id = await service.ApplySkinAsync(source, value.Text.Trim(), slim.Checked, source == "upload" ? upload : null);
            status.Text = "Скин поставлен в очередь...";
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(1000);
                var job = await service.GetSkinJobAsync(id);
                if (job.Status == "applied") { status.Text = "Скин применён."; await RefreshAsync(); return; }
                if (job.Status == "failed") throw new InvalidOperationException($"Не удалось применить скин: {job.Error}");
            }
            status.Text = "Скин остаётся в очереди и применится позже.";
        }
        catch (Exception error) { MessageBox.Show(error.Message, "Скин", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { UseWaitCursor = false; }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var state = await service.GetSkinAsync();
            if (state.Skin is not null) status.Text = $"Текущий источник: {state.Skin.Source}, {state.Skin.Variant}";
            var png = await service.GetSkinPreviewAsync();
            if (png is null) return;
            using var stream = new MemoryStream(png);
            preview.Image?.Dispose();
            using var loaded = new Bitmap(stream);
            preview.Image = new Bitmap(loaded);
        }
        catch (Exception error) { status.Text = error.Message; }
    }
}
