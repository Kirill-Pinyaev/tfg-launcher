namespace TFGLauncher;

internal sealed class SkinForm : Form
{
    private readonly ComboBox provider = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox value = new();
    private readonly CheckBox slim = new() { Text = "Тонкие руки (Slim)", AutoSize = true };
    private readonly Label hint = new();

    public SkinForm()
    {
        Text = "Скин";
        ClientSize = new Size(440, 280);
        MinimumSize = MaximumSize = Size;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(20, 20, 22);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 10F);

        var title = new Label { Text = "Управление скином", Font = new Font("Segoe UI", 16F, FontStyle.Bold), AutoSize = true, Location = new Point(24, 20) };
        provider.Items.AddRange(["Mojang", "Ely.by", "URL"]);
        provider.SelectedIndex = 0;
        provider.Location = new Point(24, 66);
        provider.Size = new Size(130, 30);
        provider.SelectedIndexChanged += (_, _) => UpdateMode();
        value.Location = new Point(164, 66);
        value.Size = new Size(250, 30);
        slim.Location = new Point(164, 106);
        hint.Location = new Point(24, 138);
        hint.Size = new Size(390, 45);
        hint.ForeColor = Color.Silver;

        var copy = MakeButton("СКОПИРОВАТЬ КОМАНДУ", 24, 192, 250);
        copy.Click += (_, _) => CopyCommand();
        var reset = MakeButton("СБРОСИТЬ", 284, 192, 130);
        reset.Click += (_, _) => Copy("/skin reset");
        Controls.AddRange([title, provider, value, slim, hint, copy, reset]);
        UpdateMode();
    }

    private Button MakeButton(string text, int x, int y, int width) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, 46),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(221, 139, 38),
        ForeColor = Color.Black,
        Font = new Font("Segoe UI", 9F, FontStyle.Bold)
    };

    private void UpdateMode()
    {
        var web = provider.Text == "URL";
        slim.Visible = web;
        value.PlaceholderText = web ? "https://.../skin.png" : "Имя аккаунта";
        hint.Text = "Команда копируется в буфер. Вставьте её в чат Minecraft и нажмите Enter.";
    }

    private void CopyCommand()
    {
        try { Copy(SkinCommands.Build(provider.Text, value.Text.Trim(), slim.Checked)); }
        catch (ArgumentException ex) { MessageBox.Show(ex.Message, "Скин", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static void Copy(string command)
    {
        Clipboard.SetText(command);
        MessageBox.Show($"Команда скопирована:\n{command}", "Скин", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
