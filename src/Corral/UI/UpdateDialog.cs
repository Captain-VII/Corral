using System.Diagnostics;
using Corral.Core;

namespace Corral.UI;

public enum UpdateChoice { Later, Skip, Installed }

/// <summary>Propose la mise à jour, la télécharge puis l'installe.</summary>
public sealed class UpdateDialog : Form
{
    readonly UpdateInfo info;
    readonly ModernButton install = new(Tr("Installer et redémarrer", "Install and restart"), primary: true);
    readonly ModernButton later = new(Tr("Plus tard", "Later"));
    readonly ModernButton skip = new(Tr("Ignorer cette version", "Skip this version"));
    readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Visible = false, Height = 16 };
    readonly Label status = new() { AutoSize = true, Tag = Theme.HintTag };
    CancellationTokenSource? cts;

    public UpdateDialog(UpdateInfo info)
    {
        this.info = info;
        Text = Tr("Mise à jour de Corral", "Corral update");
        Font = Ui.Base;
        Icon = AppIcon.Load(new Size(32, 32));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 380);
        TopMost = true;

        var title = new Label
        {
            Text = Tr($"La version {info.Version.ToString(3)} est disponible (installée : {Updater.CurrentVersion.ToString(3)}).", $"Version {info.Version.ToString(3)} is available (installed: {Updater.CurrentVersion.ToString(3)})."),
            AutoSize = true,
            Font = Ui.Section,
            Margin = new Padding(3, 3, 3, 8),
        };
        var notes = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Tag = Theme.FlatTag,
            Text = string.IsNullOrWhiteSpace(info.Notes) ? Tr("(pas de notes de version)", "(no release notes)") : info.Notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
        };
        var link = new LinkLabel { Text = Tr("Voir la page de la version", "View the release page"), AutoSize = true, Visible = info.PageUrl.Length > 0 };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(info.PageUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Ouverture du lien", ex); }
        };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(install);
        buttons.Controls.Add(later);
        buttons.Controls.Add(skip);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(title);
        layout.Controls.Add(notes);
        layout.Controls.Add(link);
        layout.Controls.Add(progress);
        layout.Controls.Add(status);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = install;
        install.Click += async (_, _) => await InstallAsync();
        later.Click += (_, _) => { Choice = UpdateChoice.Later; Close(); };
        skip.Click += (_, _) => { Choice = UpdateChoice.Skip; Close(); };
        Theme.Apply(this);
    }

    public UpdateChoice Choice { get; private set; } = UpdateChoice.Later;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        cts?.Cancel(); // fermer pendant le téléchargement l'annule
        base.OnFormClosing(e);
    }

    async Task InstallAsync()
    {
        install.Enabled = later.Enabled = skip.Enabled = false;
        progress.Visible = true;
        status.Text = Tr("Téléchargement…", "Downloading…");
        cts = new CancellationTokenSource();
        try
        {
            var file = await Updater.DownloadAsync(info, new Progress<int>(p => progress.Value = Math.Clamp(p, 0, 100)), cts.Token);
            status.Text = Tr("Installation…", "Installing…");
            Updater.InstallAndRestart(file);
            Choice = UpdateChoice.Installed;
            Close();
        }
        catch (OperationCanceledException)
        {
            // fenêtre fermée pendant le téléchargement
        }
        catch (Exception ex)
        {
            Log.Error("Mise à jour", ex);
            if (IsDisposed)
                return;
            status.Text = Tr("Échec : ", "Failed: ") + ex.Message;
            progress.Visible = false;
            install.Enabled = later.Enabled = skip.Enabled = true;
        }
    }
}
