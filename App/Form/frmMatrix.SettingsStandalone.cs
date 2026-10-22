// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#if PORTABLE_SINGLE_FILE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders;

internal partial class FrmMatrix
{
    private Label receivingFolderPath;
    private CheckBox automaticTransferClose;
    private CheckBox installationStartup;
    private bool refreshingInstallation;
    private bool refreshingTransferPreference;

    // These delegates are instance-local so UI verification never needs to
    // change the real account's launch preference or request elevation.
    internal sealed class AdministratorLaunchOptions
    {
        private readonly Func<bool> readPreference;
        private readonly Action<bool> savePreference;
        private readonly Func<bool> isElevated;
        private readonly Action<Exception> showError;
        private bool refreshing;
        private bool storedPreference;
        internal SettingsStack Content { get; }
        internal CheckBox Choice { get; }
        internal Label Status { get; }
        internal Label Help { get; }

        internal AdministratorLaunchOptions(Func<bool> readPreference, Action<bool> savePreference,
            Func<bool> isElevated, Action<Exception> showError)
        {
            this.readPreference = readPreference;
            this.savePreference = savePreference;
            this.isElevated = isElevated;
            this.showError = showError;
            Content = Section("Administrator launch");
            Content.Name = "installationAdministratorSection";
            Choice = new CheckBox
            {
                Name = "installationAdministrator", Text = "Run as administrator",
                AutoSize = true, Enabled = false,
            };
            Status = Explanation("");
            Status.Name = "installationAdministratorStatus";
            Help = Explanation("Administrator mode lets MWB click elevated windows such as Task Manager. "
                + "The entire normal app, including input received from another PC, then runs with administrator privileges. "
                + "Without approved administrator launch, Windows asks for UAC approval at each launch, including sign-in if Start with Windows is enabled. "
                + "This choice applies on the next launch and is saved only for this Windows user and this EXE location, separately from portable preferences. "
                + "The optional Secure Desktop service for protected UAC prompts is separate.");
            Help.Name = "installationAdministratorHelp";
            Content.Add(Choice); Content.Add(Status); Content.Add(Help);
            Choice.CheckedChanged += (_, _) => SaveChoice();
            Refresh();
        }

        internal void Refresh()
        {
            refreshing = true;
            Choice.Enabled = false;
            try
            {
                storedPreference = readPreference();
                Choice.Checked = storedPreference;
                UpdateStatus();
                Choice.Enabled = true;
            }
            catch (Exception error)
            {
                UpdateStatus();
                Status.Text += "\r\nCould not read the administrator launch setting: " + error.Message;
            }
            finally { refreshing = false; }
        }

        private void SaveChoice()
        {
            if (refreshing || !Choice.Enabled) return;
            bool previous = storedPreference;
            try
            {
                savePreference(Choice.Checked);
                storedPreference = Choice.Checked;
                UpdateStatus();
            }
            catch (Exception error)
            {
                refreshing = true;
                try { Choice.Checked = previous; }
                finally { refreshing = false; }
                UpdateStatus();
                showError(error);
            }
        }

        private void UpdateStatus()
        {
            try { Status.Text = isElevated() ? "Current session: Administrator." : "Current session: Standard user."; }
            catch (Exception) { Status.Text = "Current session: privileges unavailable."; }
            Status.Text += storedPreference
                ? " Administrator launch is on for the next launch."
                : " Administrator launch is off for the next launch.";
        }
    }

    internal sealed class ApprovedAdministratorLaunchOptions
    {
        private readonly Func<bool> readEnabled;
        private readonly Func<bool> isAvailable;
        private readonly Func<bool, Task<bool>> configure;
        private readonly Action<Exception> showError;
        private bool busy;
        internal SettingsStack Content { get; }
        internal Label Status { get; }
        internal Label Help { get; }
        internal Button SetUp { get; }
        internal Button Remove { get; }

        internal ApprovedAdministratorLaunchOptions(Func<bool> readEnabled, Func<bool> isAvailable,
            Func<bool, Task<bool>> configure, Action<Exception> showError)
        {
            this.readEnabled = readEnabled;
            this.isAvailable = isAvailable;
            this.configure = configure;
            this.showError = showError;
            Content = Section("Approved administrator launch");
            Content.Name = "installationApprovedAdministratorSection";
            Status = Explanation("");
            Status.Name = "installationApprovedAdministratorStatus";
            Help = Explanation("Approve setup once to start MWB as administrator without another prompt at each launch. "
                + "Updates to this protected copy require approval. This is separate from the Secure Desktop service.");
            Help.Name = "installationApprovedAdministratorHelp";
            SetUp = SettingsButton("Set up approved launch…", async (_, _) => await ConfigureAsync(true));
            SetUp.Name = "installationApprovedAdministratorSetUp";
            Remove = SettingsButton("Remove approved launch…", async (_, _) => await ConfigureAsync(false));
            Remove.Name = "installationApprovedAdministratorRemove";
            Content.Add(Status); Content.Add(Help); Content.Add(Actions(SetUp, Remove));
            Refresh();
        }

        internal void Refresh()
        {
            if (busy || Content.IsDisposed) return;
            SetUp.Enabled = Remove.Enabled = false;
            try
            {
                bool enabled = readEnabled();
                Status.Text = enabled ? "Approved administrator launch is configured." : "Approved administrator launch is off.";
                SetUp.Text = enabled ? "Update approved launch…" : "Set up approved launch…";
                // Removing an existing approval must remain possible after the
                // ordinary administrator preference is turned off.
                Remove.Enabled = enabled;
                bool available = isAvailable();
                if (!available)
                    Status.Text += " Turn on Run as administrator and save this copy's preferences to set up or update this launch method.";
                SetUp.Enabled = available;
            }
            catch (Exception error)
            {
                Status.Text = "Could not read approved administrator launch status: " + error.Message;
            }
        }

        private async Task ConfigureAsync(bool enable)
        {
            if (busy || Content.IsDisposed || !(enable ? SetUp.Enabled : Remove.Enabled)) return;
            busy = true;
            SetUp.Enabled = Remove.Enabled = false;
            Status.Text = enable ? "Setting up approved administrator launch…" : "Removing approved administrator launch…";
            bool completed = false;
            bool failed = false;
            try { completed = await configure(enable); }
            catch (Exception error)
            {
                failed = true;
                if (!Content.IsDisposed) showError(error);
            }
            finally
            {
                busy = false;
                if (!Content.IsDisposed)
                {
                    Refresh();
                    if (!completed && !failed)
                        Status.Text += enable ? " Setup was not completed." : " Removal was not completed.";
                }
            }
        }
    }

    // Keep options directly in each section. Nested, autosizing tables for every
    // option multiply preferred-size passes when a hidden tab becomes visible.
    internal sealed class SettingsStack : Panel
    {
        private bool updatingWidths, arranging;
        private int constrainedWidth = -1;
        private readonly bool sideBySide;
        internal SettingsStack(bool sideBySide = false)
        {
            this.sideBySide = sideBySide;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Dock = DockStyle.Top; Margin = new Padding(0); Padding = new Padding(2);
        }
        internal void Add(Control control)
        {
            control.Dock = DockStyle.None; control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            control.Margin = new Padding(3, 1, 3, 1);
            Controls.Add(control);
        }
        private int ColumnWidth(int width) => Math.Max(1, (width - Padding.Horizontal) /
            (sideBySide ? Math.Max(1, Controls.Count) : 1));
        private int ChildWidth(Control child, int width) => Math.Max(40, ColumnWidth(width) - child.Margin.Horizontal);
        private int Measure(Control child, int width) => child.AutoSize ? child.GetPreferredSize(
            new Size(ChildWidth(child, width), 0)).Height : child.Height;
        public override Size GetPreferredSize(Size proposedSize)
        {
            int width = proposedSize.Width > 0 && proposedSize.Width < int.MaxValue ? proposedSize.Width : Width;
            int height = 0;
            foreach (Control child in Controls)
            {
                int rowHeight = Measure(child, width) + child.Margin.Vertical;
                height = sideBySide ? Math.Max(height, rowHeight) : height + rowHeight;
            }
            return new Size(width, height + Padding.Vertical);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            if (arranging) return;
            arranging = true;
            try
            {
                int y = Padding.Top, x = Padding.Left, bottom = Padding.Top;
                foreach (Control child in Controls)
                {
                    // Resolve wrapping at the new width before measuring height.
                    // SetBounds can relayout a nested section; using its old height
                    // afterwards leaves empty rows and an inflated scroll range.
                    child.Width = ChildWidth(child, ClientSize.Width);
                    int height = Measure(child, ClientSize.Width);
                    child.SetBounds(x + child.Margin.Left, y + child.Margin.Top,
                        ChildWidth(child, ClientSize.Width), height);
                    bottom = Math.Max(bottom, child.Bottom + child.Margin.Bottom);
                    if (sideBySide) x += ColumnWidth(ClientSize.Width);
                    else y = bottom;
                }
                int needed = bottom + Padding.Bottom;
                if (Height != needed) Height = needed;
            }
            finally { arranging = false; }
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateChildWidths();
        }
        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            UpdateChildWidths(force: true);
        }
        private void UpdateChildWidths(bool force = false)
        {
            // Changing height alone must not invalidate every child's measurement.
            if (updatingWidths || (!force && constrainedWidth == ClientSize.Width)) return;
            updatingWidths = true; constrainedWidth = ClientSize.Width;
            SuspendLayout();
            try
            {
                foreach (Control child in Controls)
                    if (child is Label or CheckBox or FlowLayoutPanel)
                    {
                        var maximum = new Size(ChildWidth(child, ClientSize.Width), 0);
                        if (child.MaximumSize != maximum) child.MaximumSize = maximum;
                    }
            }
            finally { ResumeLayout(true); updatingWidths = false; }
        }
    }

    private static Label Explanation(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, UseMnemonic = false };
    private static SettingsStack Section(string title)
    {
        var stack = new SettingsStack();
        stack.Add(new Label { Text = title, AutoSize = true, ForeColor = Color.DarkGreen, UseMnemonic = false });
        return stack;
    }
    private void SetOptionTip(Control option, string defaults, string explanation)
    {
        string text = explanation ?? toolTip.GetToolTip(option);
        if (defaults != null) text = (string.IsNullOrWhiteSpace(text) ? "" : text + "\n\n") + "Default: " + defaults + ".";
        toolTip.SetToolTip(option, text);
    }
    private void AddOption(SettingsStack parent, CheckBox option, string defaults, string explanation = null)
    {
        option.AutoSize = true; option.ResetFont();
        SetOptionTip(option, defaults, explanation);
        parent.Add(option);
    }
    private static Button SettingsButton(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 4) };
        button.Click += click; return button;
    }
    private static FlowLayoutPanel Actions(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0) };
        foreach (var control in controls)
        {
            control.Dock = DockStyle.None; control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            control.Margin = new Padding(3, 1, 8, 1); flow.Controls.Add(control);
        }
        return flow;
    }
    private void HostStack(TabPage tab, Control content)
    {
        tab.AutoScroll = true; tab.Padding = new Padding(4);
        tab.Controls.Add(content); content.Dock = DockStyle.Top;
        RegisterDisabledOptionTips(content);
    }

    private Control disabledOptionTipTarget;
    private ToolTip disabledOptionTip;
    private void RegisterDisabledOptionTips(Control host)
    {
        // Disabled WinForms controls cannot receive hover events. Their enabled
        // parent provides the same help, so dependencies need no extra text row.
        host.MouseMove += (_, e) =>
        {
            var child = host.GetChildAtPoint(e.Location, GetChildAtPointSkip.Invisible);
            if (child?.Enabled != false || string.IsNullOrEmpty(toolTip.GetToolTip(child)))
            {
                if (disabledOptionTipTarget != null) disabledOptionTip?.Hide(disabledOptionTipTarget.Parent);
                disabledOptionTipTarget = null;
                return;
            }
            if (child == disabledOptionTipTarget) return;
            disabledOptionTip ??= new ToolTip(components) { ShowAlways = true };
            if (disabledOptionTipTarget != null) disabledOptionTip.Hide(disabledOptionTipTarget.Parent);
            disabledOptionTipTarget = child;
            disabledOptionTip.Show(toolTip.GetToolTip(child), host, child.Left, child.Bottom + 4, 10000);
        };
        host.MouseLeave += (_, _) =>
        {
            if (disabledOptionTipTarget?.Parent != host) return;
            disabledOptionTip?.Hide(host);
            disabledOptionTipTarget = null;
        };
        foreach (Control child in host.Controls)
            if (child.HasChildren) RegisterDisabledOptionTips(child);
    }

    private void LayoutPortableSettingsPages()
    {
        SuspendLayout();
        groupBoxShortcuts.SizeChanged -= GroupBoxShortcuts_SizeChanged;
        try
        {
            var mouse = Section("Mouse and screen switching");
            AddOption(mouse, checkBoxMouseEdgeSwitching, "On");
            labelEasyMouse.Text = "Activation:"; labelEasyMouse.AutoSize = true;
            comboBoxEasyMouseOption.ResetFont();
            SetOptionTip(comboBoxEasyMouseOption, "Always", "Switch at a screen edge immediately, or only while holding Ctrl or Shift.");
            toolTip.SetToolTip(labelEasyMouse, toolTip.GetToolTip(comboBoxEasyMouseOption));
            mouse.Add(Actions(labelEasyMouse, comboBoxEasyMouseOption));
            AddOption(mouse, checkBoxBlockFullscreenEdgeSwitching, "On",
                "Prevent edge switching while a full-screen app is active on this PC. Turn off to allow switching from full-screen or borderless apps. Requires Switch computers at screen edge.\n\nSome games capture the pointer. Enable keyboard shortcuts and choose Switch PCs to leave those games.");
            AddOption(mouse, checkBoxCircle, "Off", "Wrap across the outside edges of your computer layout.");
            AddOption(mouse, checkBoxHideMouse, "On", "Hide the pointer on this PC while controlling another.");
            checkBoxDrawMouse.Text = "Show a fallback cursor";
            AddOption(mouse, checkBoxDrawMouse, "On", "Draw a replacement pointer when Windows reports that its cursor is invisible. Turn off if it conflicts with intentional cursor hiding.");
            checkBoxMouseMoveRelatively.Text = "Use relative mouse movement";
            AddOption(mouse, checkBoxMouseMoveRelatively, "Off", "Try this if pointer movement feels uneven between different screens.");
            checkBoxBlockMouseAtCorners.Text = "Block switching at screen corners";
            AddOption(mouse, checkBoxBlockMouseAtCorners, "Off", "Reduce accidental switches near a corner.");
            AddOption(mouse, checkBoxBlockScreenSaver, "On", "Keep other PCs' screen savers from starting while you are actively using MWB.");
            // Keep the extra edge-switch option within the original window.
            // Remove only inter-row margins, preserving each control's full text height.
            foreach (Control option in mouse.Controls)
                if (option is not Label) option.Margin = new Padding(3, 0, 3, 0);

            var sharing = Section("Clipboard and file transfers");
            AddOption(sharing, checkBoxShareClipboard, "On", "Share copied text and images between PCs.");
            AddOption(sharing, checkBoxTransferFile, "On");
            UpdatePortableTransferFileText();
            var receivingLabel = new Label { Text = "Receiving folder on this PC", AutoSize = true };
            sharing.Add(receivingLabel);
            receivingFolderPath = Explanation(ReceivingFolderText());
            receivingFolderPath.Name = "receivingFolderPath";
            sharing.Add(receivingFolderPath);
            const string receivingHelp = "Used for new drag/drop transfers when you aren't dropping into an Explorer folder. Existing transfers keep their destination.\n\nDefault: Desktop \\ MouseWithoutBorders.";
            toolTip.SetToolTip(receivingLabel, receivingHelp);
            toolTip.SetToolTip(receivingFolderPath, receivingHelp);
            sharing.Add(Actions(SettingsButton("Choose folder…", ChooseReceivingFolder), SettingsButton("Use default", ResetReceivingFolder)));
            automaticTransferClose = new CheckBox { Name = "automaticTransferClose", Text = "Automatically close finished transfers", Checked = Setting.Values.AutoCloseTransferWindow };
            AddOption(sharing, automaticTransferClose, "On", "Close this PC's completed or cancelled list after cleanup. Errors stay visible.");
            automaticTransferClose.CheckedChanged += (_, _) =>
            {
                if (refreshingTransferPreference) return;
                try { Setting.Values.SetAutoCloseTransferWindow(automaticTransferClose.Checked); }
                catch (Exception error)
                {
                    refreshingTransferPreference = true;
                    try { automaticTransferClose.Checked = Setting.Values.AutoCloseTransferWindow; }
                    finally { refreshingTransferPreference = false; }
                    SettingsError(error);
                }
            };

            var columns = new SettingsStack(sideBySide: true) { Padding = new Padding(0) };
            columns.Add(mouse); columns.Add(sharing);

            var shortcuts = new SettingsStack();
            AddOption(shortcuts, checkBoxEnableKeyboardShortcuts, "Off", "Assignments below are preserved while shortcuts are disabled.");
            labelSwitchBetweenMachine.Text = "Switch PCs, Ctrl+Alt:"; labelSwitchBetweenMachine.AutoSize = true; labelSwitchBetweenMachine.ResetFont();
            foreach (var control in new Control[] { labelSwitchBetweenMachine, radioButtonF1, radioButtonNum, radioButtonDisable })
            {
                control.ResetFont();
                SetOptionTip(control, "None", "Hold Ctrl+Alt and the chosen function key or number to switch directly to a PC.");
            }
            shortcuts.Add(Actions(labelSwitchBetweenMachine, radioButtonF1, radioButtonNum, radioButtonDisable));
            ConfigureShortcut(labelLockMachine, "Lock PCs", comboBoxLockMachine);
            ConfigureShortcut(labelSwitch2AllPCMode, "All PCs", comboBoxSwitchToAllPC);
            ConfigureShortcut(labelReconnect, "Reconnect", comboBoxReconnect);
            ConfigureShortcut(LabelToggleEasyMouse, "Toggle edge switching", comboBoxEasyMouse);
            shortcuts.Add(Actions(labelLockMachine, comboBoxLockMachine, labelSwitch2AllPCMode, comboBoxSwitchToAllPC));
            shortcuts.Add(Actions(labelReconnect, comboBoxReconnect, LabelToggleEasyMouse, comboBoxEasyMouse));
            groupBoxOtherOptions.Visible = groupBoxShortcuts.Visible = false;
            var content = new SettingsStack();
            content.Add(columns);
            content.Add(new Panel { Name = "keyboardShortcutDivider", Height = 2, BackColor = SystemColors.ControlDark });
            content.Add(shortcuts);
            HostStack(tabPageOther, content);

            var network = Section("Connection troubleshooting");
            checkBoxReverseLookup.Text = "Check computer names against DNS" + (Setting.Values.ReverseLookupIsGpoConfigured ? " [Managed]" : "");
            AddOption(network, checkBoxReverseLookup, "Off", "Ask the network's name service (DNS) to check that an IP address matches the expected PC name. Use this to troubleshoot name/address mismatches. Some home networks have no matching DNS records, so enabling this check can prevent a connection.");
            network.Add(new Label { Text = "Machine name to IP address mappings", AutoSize = true });
            const string mappingHelp = "MWB normally finds your other PCs automatically. If they already connect, you can leave this box empty.\n\nIf MWB cannot find a PC, enter its exact name from Machine Setup, a space, and its local IP address. Add one PC per line.\nExample: OFFICE-PC 192.168.1.20\n\nAn IP address is a PC's address on your network. Find it in that PC's Windows network settings (IPv4 address). If it changes, update the entry here. A router setting called a DHCP reservation can keep that address from changing.";
            network.Add(Explanation(mappingHelp));
            toolTip.SetToolTip(textBoxMachineName2IP, mappingHelp);
            // A multiline TextBox can still report a one-line preferred height.
            // Keep room for entries even when the editor is empty.
            textBoxMachineName2IP.AutoSize = false;
            textBoxMachineName2IP.MinimumSize = new Size(0, Math.Max(150, Font.Height * 8));
            textBoxMachineName2IP.Height = textBoxMachineName2IP.MinimumSize.Height;
            network.Add(textBoxMachineName2IP);
            if (Setting.Values.Name2IpPolicyListIsGpoConfigured)
            {
                network.Add(Explanation("These mappings are set by your Windows administrator and cannot be edited here."));
                textBoxMachineName2IPPolicyList.AutoSize = false;
                textBoxMachineName2IPPolicyList.MinimumSize = new Size(0, Math.Max(120, Font.Height * 6));
                textBoxMachineName2IPPolicyList.Height = textBoxMachineName2IPPolicyList.MinimumSize.Height;
                network.Add(textBoxMachineName2IPPolicyList);
            }
            groupBoxDNS.Visible = groupBoxName2IPPolicyList.Visible = pictureBoxMouseWithoutBorders.Visible = textBoxDNS.Visible = false;
            HostStack(tabPageAdvancedSettings, network);
            checkBoxTwoRow.Text = "Two rows";
            toolTip.SetToolTip(checkBoxTwoRow, "Arrange the computers in two rows to match screens above and below each other.");
            UpdatePortableShortcutControlState();
        }
        finally { ResumeLayout(true); }
    }

    private void ConfigureShortcut(Label label, string title, ComboBox choice)
    {
        label.Text = title + ", Ctrl+Alt:"; label.AutoSize = true; label.ResetFont(); choice.ResetFont();
        choice.Width = Math.Max(60, TextRenderer.MeasureText("None", choice.Font).Width + 32);
        SetOptionTip(choice, "None", toolTip.GetToolTip(choice));
        toolTip.SetToolTip(label, toolTip.GetToolTip(choice));
    }
    private string ReceivingFolderText() => string.IsNullOrEmpty(Setting.Values.DefaultReceivingFolder)
        ? TransferReceivePreferences.DesktopFolder : Setting.Values.DefaultReceivingFolder;
    private void ChooseReceivingFolder(object sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose the default receiving folder on this PC", UseDescriptionForTitle = true, SelectedPath = ReceivingFolderText() };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            string folder = TransferReceivePreferences.NormalizeFolder(dialog.SelectedPath);
            TransferReceivePreferences.ValidateWritable(folder);
            Setting.Values.SetDefaultReceivingFolder(folder); receivingFolderPath.Text = ReceivingFolderText();
        }
        catch (Exception error) { SettingsError(error); }
    }
    private void ResetReceivingFolder(object sender, EventArgs e)
    {
        try { Setting.Values.SetDefaultReceivingFolder(""); receivingFolderPath.Text = ReceivingFolderText(); }
        catch (Exception error) { SettingsError(error); }
    }
    private void SettingsError(Exception error)
    {
        Logger.Log("Settings: " + error.Message);
        MessageBox.Show(this, error.Message, "Could not change setting", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void AddPortableSettingsTab()
    {
        var tab = new TabPage { Text = "Installation", Name = "installationTab", BackColor = tabPageOther.BackColor };
        var content = Section(PortableApplication.IsInstalledCopy ? "Installed for your Windows account" : "Running as a portable copy");
        content.Add(Explanation(PortableApplication.IsInstalledCopy
            ? "Choose whether MWB starts when you sign in, or uninstall this copy."
            : "Run MWB from this folder, or install it for your Windows account and keep your current settings."));
        content.Add(new Label { Text = "Application location", AutoSize = true });
        content.Add(Explanation(PortableApplication.CurrentExecutablePath));
        content.Add(new Label { Text = "Preferences", AutoSize = true });
        content.Add(Explanation(PortableApplication.CurrentSettingsPath));
        content.Add(SettingsButton("Open folder", (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(PortableApplication.CurrentSettingsPath)) { UseShellExecute = true }); }
            catch (Exception error) { SettingsError(error); }
        }));
        var administrator = new AdministratorLaunchOptions(PortableElevation.IsEnabled, PortableElevation.SetEnabled,
            PortableElevation.IsElevated, SettingsError);
        SetOptionTip(administrator.Choice, "Off", administrator.Help.Text);
        content.Add(administrator.Content);
        tab.Enter += (_, _) => administrator.Refresh();
        var approvedAdministrator = new ApprovedAdministratorLaunchOptions(PortableApprovedLaunch.IsEnabled,
            () => PortableApprovedLaunch.IsSupported && PortableElevation.IsEnabled() && File.Exists(PortableApplication.CurrentSettingsPath),
            enabled => PortableApprovedLaunch.ConfigureAsync(this, enabled), SettingsError);
        content.Add(approvedAdministrator.Content);
        administrator.Choice.CheckedChanged += (_, _) => approvedAdministrator.Refresh();
        tab.Enter += (_, _) => approvedAdministrator.Refresh();
        if (PortableApplication.IsInstalledCopy)
        {
            installationStartup = new CheckBox { Text = "Start with Windows", Name = "installationStartup" };
            AddOption(content, installationStartup, null, "Start this installed copy when you sign in to Windows.");
            installationStartup.CheckedChanged += (_, _) =>
            {
                if (refreshingInstallation) return;
                try { PortableApplication.SetStartWithWindows(installationStartup.Checked); }
                catch (Exception error) { RefreshInstallationStartup(); SettingsError(error); }
            };
            RefreshInstallationStartup();
            tab.Enter += (_, _) => RefreshInstallationStartup();
            content.Add(SettingsButton("Uninstall…", UninstallInstalledCopy));
        }
        else content.Add(SettingsButton("Install for me…", InstallPortableCopyButton_Click));
        content.Add(Explanation("Your portable settings are saved beside MouseWithoutBorders.exe. Normal portable and per-user setup do not install a Windows service. Use About → Check for updates to check this fork's releases and choose whether to download and install an update."));
        content.Add(new Label { Text = "Optional sign-in and UAC control", AutoSize = true });
        content.Add(Explanation("The optional Secure Desktop service preview is intended to add input on the Windows sign-in screen and protected UAC prompts. It requires administrator installation, a reviewed settings snapshot, and testing on both PCs. Clipboard and file transfers stay on the normal desktop. Read README.md in the service package (docs/SECURE_DESKTOP_PREVIEW.md in the source) before installing."));
        HostStack(tab, content); tabControlSetting.TabPages.Add(tab);
    }
    private void RefreshInstallationStartup()
    {
        refreshingInstallation = true;
        try { installationStartup.Checked = PortableApplication.IsStartWithWindowsEnabled(); }
        catch (Exception error) { SettingsError(error); }
        finally { refreshingInstallation = false; }
    }
    private void UninstallInstalledCopy(object sender, EventArgs e)
    {
        if (MessageBox.Show(this, "Uninstall this copy of Mouse Without Borders?", "Uninstall Mouse Without Borders",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var choice = MessageBox.Show(this, "Delete MouseWithoutBorders.prefs.json too?\n\nChoose No to keep your computer layout, security key, and preferences for a later reinstall.",
            "Delete preferences?", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (choice == DialogResult.Cancel) return;
        try { PortableApplication.BeginUninstall(choice == DialogResult.Yes); Hide(); Common.MainForm?.Quit(cleanup: true, isFormClosing: false); }
        catch (Exception error) { SettingsError(error); }
    }

    private IEnumerable<Control.ControlCollection> PortableDiagnosticControls()
    {
        foreach (var tab in new[] { tabPageOther, tabPageAdvancedSettings, tabControlSetting.TabPages[tabControlSetting.TabPages.Count - 1] })
            foreach (var collection in VisibleCollections(tab)) yield return collection;
    }
    private static IEnumerable<Control.ControlCollection> VisibleCollections(Control parent)
    {
        yield return parent.Controls;
        foreach (Control child in parent.Controls)
            if (child is not GroupBox && child.HasChildren)
                foreach (var collection in VisibleCollections(child)) yield return collection;
    }
}
#endif
