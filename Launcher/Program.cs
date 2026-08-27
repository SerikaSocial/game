using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace SerikaLauncher;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string gameExe = Path.Combine(exeDir, "SerikaSocial.exe");

        if (!File.Exists(gameExe))
        {
            MessageBox.Show(
                $"Could not find SerikaSocial.exe in:\n{exeDir}\n\nPlease run this launcher from the game folder.",
                "Serika Social",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var form = new Form
        {
            Text = "Serika Social",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            Width = 360,
            Height = 200,
            BackColor = System.Drawing.Color.FromArgb(14, 10, 26),
        };

        var title = new Label
        {
            Text = "Serika Social",
            Font = new System.Drawing.Font("Segoe UI", 18F, System.Drawing.FontStyle.Bold),
            ForeColor = System.Drawing.Color.FromArgb(167, 139, 250),
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 50,
        };
        form.Controls.Add(title);

        var subtitle = new Label
        {
            Text = "Choose your mode",
            Font = new System.Drawing.Font("Segoe UI", 10F),
            ForeColor = System.Drawing.Color.FromArgb(160, 160, 180),
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 25,
        };
        form.Controls.Add(subtitle);

        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Fill,
            Padding = new System.Windows.Forms.Padding(20, 10, 20, 10),
            BackColor = System.Drawing.Color.FromArgb(14, 10, 26),
        };

        var btnDesktop = new Button
        {
            Text = "🖥  Desktop",
            Font = new System.Drawing.Font("Segoe UI", 11F),
            Width = 140,
            Height = 50,
            FlatStyle = FlatStyle.Flat,
            BackColor = System.Drawing.Color.FromArgb(76, 59, 122),
            ForeColor = System.Drawing.Color.White,
            Cursor = System.Windows.Forms.Cursors.Hand,
        };
        btnDesktop.FlatAppearance.BorderSize = 0;
        btnDesktop.Click += (s, e) => { form.Tag = "desktop"; form.Close(); };

        var btnVr = new Button
        {
            Text = "🥽  VR Mode",
            Font = new System.Drawing.Font("Segoe UI", 11F),
            Width = 140,
            Height = 50,
            FlatStyle = FlatStyle.Flat,
            BackColor = System.Drawing.Color.FromArgb(124, 58, 237),
            ForeColor = System.Drawing.Color.White,
            Cursor = System.Windows.Forms.Cursors.Hand,
        };
        btnVr.FlatAppearance.BorderSize = 0;
        btnVr.Click += (s, e) => { form.Tag = "vr"; form.Close(); };

        panel.Controls.Add(btnDesktop);
        panel.Controls.Add(new Panel { Width = 10 });
        panel.Controls.Add(btnVr);
        form.Controls.Add(panel);

        Application.Run(form);

        string? choice = form.Tag as string;
        if (choice == null) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = gameExe,
                UseShellExecute = false,
            };
            if (choice == "vr")
                psi.ArgumentList.Add("--vr");

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch game:\n{ex.Message}", "Serika Social",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
