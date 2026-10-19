// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#if PORTABLE_SINGLE_FILE
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders;

internal sealed class UpdateForm : System.Windows.Forms.Form
{
    private readonly Label status;
    private readonly Label serviceNote;
    private readonly ProgressBar progress;
    private readonly Button check;
    private readonly Button install;
    private readonly string serviceVersion;
    private readonly Func<string, CancellationToken, Task<UpdateRelease>> checkForUpdates;
    private CancellationTokenSource operation;
    private UpdateRelease candidate;
    private PortableUpdateApply.PreparedUpdate prepared;
    private bool quitting;
    private bool manualInstallRequired;

    internal UpdateForm() : this(ManualUpdates.CheckCandidateAsync)
    {
    }

    internal UpdateForm(Func<string, CancellationToken, Task<UpdateRelease>> checkForUpdates)
    {
        this.checkForUpdates = checkForUpdates ?? throw new ArgumentNullException(nameof(checkForUpdates));
        Text = "Updates — Mouse Without Borders";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        MinimumSize = new Size(540, 360);
        ClientSize = new Size(590, 350);
        Padding = new Padding(16);
        StandaloneBranding.Apply(this);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7,
            AutoScroll = true, Padding = new Padding(0),
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 7; row++) content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(content);
        Label AddText(string name, string text)
        {
            var label = new Label
            {
                Name = name, Text = text, AutoSize = true, Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 12), UseMnemonic = false,
            };
            content.Controls.Add(label);
            return label;
        }
        AddText("installedVersion", "Installed version: " + Application.ProductVersion);
        AddText("updateSource", "Updates from El3ctro99/MouseWithoutBorders-Portable on GitHub.");
        status = AddText("updateStatus", "Ready to check for updates.");
        serviceVersion = ReadOptionalServiceVersion();
        serviceNote = AddText("serviceUpdateNote", serviceVersion == null
            ? "This updates the normal application. Your pairing, preferences and transfer recovery files stay in place."
            : "Optional UAC service: " + serviceVersion + ". This updates the normal application only. For service updates, use the full package on GitHub.");
        progress = new ProgressBar
        {
            Name = "updateProgress", Dock = DockStyle.Top, Height = 18,
            Minimum = 0, Maximum = 100, Margin = new Padding(0, 0, 0, 12), Visible = false,
        };
        content.Controls.Add(progress);
        var releases = new LinkLabel
        {
            Name = "updateReleasesLink", Text = "Open GitHub releases", AutoSize = true,
            Margin = new Padding(0, 0, 0, 14),
        };
        releases.LinkClicked += (_, _) => OpenReleases();
        content.Controls.Add(releases);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true, Margin = new Padding(0),
        };
        check = new Button { Name = "checkUpdates", Text = "Check again", AutoSize = true, MinimumSize = new Size(105, 30) };
        install = new Button { Name = "installUpdate", Text = "Download and install", AutoSize = true, MinimumSize = new Size(155, 30), Enabled = false };
        var close = new Button { Name = "closeUpdates", Text = "Close", AutoSize = true, MinimumSize = new Size(80, 30), DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(check); buttons.Controls.Add(install); buttons.Controls.Add(close);
        content.Controls.Add(buttons);
        CancelButton = close;
        check.Click += async (_, _) => await CheckAsync();
        install.Click += async (_, _) => await InstallAsync();
        Shown += async (_, _) => await CheckAsync();
        FormClosing += (_, _) =>
        {
            if (quitting) return;
            operation?.Cancel();
            prepared?.CancelPending();
        };
        FormClosed += (_, _) => { if (!quitting) prepared?.Dispose(); };
    }

    private async Task CheckAsync()
    {
        if (operation != null) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        candidate = null;
        manualInstallRequired = false;
        SetBusy(true);
        status.Text = "Checking GitHub…";
        try
        {
            var found = await checkForUpdates(Application.ProductVersion, cancellation.Token);
            if (IsDisposed) return;
            candidate = found;
            status.Text = found == null
                ? "You're up to date."
                : "Version " + found.Version + " is available (" + (found.Executable.Size / 1048576d).ToString("0.0") + " MB).";
            if (found != null)
            {
                try { PortableUpdateApply.ValidateNormalTarget(Environment.ProcessPath ?? string.Empty); }
                catch (Exception error) when (error is System.Security.SecurityException or IOException
                    or UnauthorizedAccessException or ArgumentException)
                {
                    manualInstallRequired = true;
                    status.Text += " This installation requires a manual update. Open GitHub releases, exit MWB, and replace only the EXE while keeping its preferences.";
                }
            }
        }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = "The update check was cancelled or timed out. You can try again."; }
        catch (Exception error)
        {
            Logger.Log("Update check: " + error.Message);
            if (!IsDisposed) status.Text = "Couldn't check for updates. " + error.Message;
        }
        finally
        {
            operation = null;
            if (!IsDisposed) SetBusy(false);
        }
    }

    private async Task InstallAsync()
    {
        if (operation != null || candidate == null) return;
        string note = serviceVersion == null ? string.Empty
            : "\r\n\r\nThe optional UAC service remains at " + serviceVersion + "; update it separately from the full package if required by the release notes.";
        if (MessageBox.Show(this,
            "Download version " + candidate.Version + " from this fork's GitHub release, then close and restart Mouse Without Borders?\r\n\r\nYour pairing, preferences and transfer recovery files will be kept." + note,
            "Install update", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        SetBusy(true);
        progress.Visible = true;
        progress.Value = 0;
        status.Text = "Downloading and verifying the update…";
        try
        {
            // Reject unsupported portable locations before downloading the payload.
            PortableUpdateApply.ValidateNormalTarget(Environment.ProcessPath
                ?? throw new InvalidOperationException("The current application path is unavailable. Download the update manually from GitHub."));
            var reporting = new Progress<UpdateDownloadProgress>(value =>
            {
                if (IsDisposed || operation != cancellation) return;
                progress.Value = value.Total > 0 ? Math.Clamp((int)(value.Received * 100d / value.Total), 0, 100) : 0;
                status.Text = "Downloading " + (value.Received / 1048576d).ToString("0.0") + " / "
                    + (value.Total / 1048576d).ToString("0.0") + " MB…";
            });
            using var download = await UpdateDownload.DownloadAsync(candidate, reporting, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (IsDisposed) return;
            status.Text = "Preparing a safe restart…";
            prepared = PortableUpdateApply.Prepare(download.ExecutablePath, download.Sha256);
            await prepared.StartAndWaitReadyAsync(TimeSpan.FromSeconds(15), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (IsDisposed) { prepared.CancelPending(); return; }
            if (Common.MainForm == null) throw new InvalidOperationException("The main application is unavailable. No update was installed.");

            // Quit saves preferences and pauses/journals transfers. It can refuse
            // exit; a returning call means the helper must not update on a later exit.
            status.Text = "Restarting Mouse Without Borders…";
            quitting = true;
            Common.MainForm.Quit(true, false);
            quitting = false;
            prepared.CancelPending();
            status.Text = "The application could not close safely. Finish any active transfers and try again. No files were replaced.";
        }
        catch (OperationCanceledException)
        {
            quitting = false;
            prepared?.CancelPending();
            if (!IsDisposed) status.Text = "The update was cancelled. Your current application is unchanged.";
        }
        catch (Exception error)
        {
            quitting = false;
            prepared?.CancelPending();
            Logger.Log("Install update: " + error.Message);
            if (!IsDisposed) status.Text = "The update could not be installed. " + error.Message
                + " Open GitHub releases for a manual update.";
        }
        finally
        {
            if (!quitting) { prepared?.Dispose(); prepared = null; }
            operation = null;
            if (!IsDisposed) { progress.Visible = false; SetBusy(false); }
        }
    }

    private void SetBusy(bool busy)
    {
        check.Enabled = !busy;
        install.Enabled = !busy && candidate != null && !manualInstallRequired;
    }

    private void OpenReleases()
    {
        try { _ = Process.Start(new ProcessStartInfo(ManualUpdates.Releases) { UseShellExecute = true }); }
        catch (Exception error)
        {
            MessageBox.Show(this, "Windows could not open GitHub. Open this address in your browser:\r\n\r\n"
                + ManualUpdates.Releases + "\r\n\r\n" + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static string ReadOptionalServiceVersion()
    {
        try
        {
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Mouse Without Borders Secure Desktop", "MouseWithoutBorders.exe");
            return File.Exists(executable) ? FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "installed" : null;
        }
        catch (Exception) { return "installed (version unavailable)"; }
    }
}
#endif
