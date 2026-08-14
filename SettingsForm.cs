namespace TFGLauncher;

internal sealed class SettingsForm : Form
{
    private readonly TextBox addressBox = new();
    public string ServerAddress { get; private set; }

    public SettingsForm(string currentAddress)
    {
        ServerAddress = currentAddress;
        Text = "Настройки TFG Launcher";
        ClientSize = new Size(430, 180);
        MinimumSize = MaximumSize = Size;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        BackColor = Color.FromArgb(20, 20, 22);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 10F);

        var label = new Label { Text = "Адрес Minecraft-сервера", AutoSize = true };
        label.SetBounds(25, 22, 250, 24);
        addressBox.SetBounds(25, 50, 380, 30);
        addressBox.Text = currentAddress;
        addressBox.MaxLength = 255;
        addressBox.BackColor = Color.FromArgb(38, 38, 42);
        addressBox.ForeColor = Color.White;
        addressBox.BorderStyle = BorderStyle.FixedSingle;

        var hint = new Label
        {
            Text = "IPv4 или домен, необязательный порт после двоеточия",
            ForeColor = Color.Gray,
            AutoSize = true
        };
        hint.SetBounds(25, 84, 380, 22);

        var save = new Button { Text = "СОХРАНИТЬ", DialogResult = DialogResult.None };
        var cancel = new Button { Text = "ОТМЕНА", DialogResult = DialogResult.Cancel };
        save.SetBounds(185, 122, 105, 34);
        cancel.SetBounds(300, 122, 105, 34);
        foreach (var button in new[] { save, cancel })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(38, 38, 42);
            button.ForeColor = Color.WhiteSmoke;
        }
        save.Click += (_, _) =>
        {
            if (!InputRules.TryParseServer(addressBox.Text, out var endpoint))
            {
                MessageBox.Show("Пример корректного адреса: 77.51.139.159:25565",
                    "Некорректный адрес", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ServerAddress = endpoint.Address;
            DialogResult = DialogResult.OK;
            Close();
        };

        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange([label, addressBox, hint, save, cancel]);
    }
}
