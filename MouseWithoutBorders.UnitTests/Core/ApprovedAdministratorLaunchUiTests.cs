// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class ApprovedAdministratorLaunchUiTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task OpeningTheSectionReadsActualStateWithoutSettingUpOrRemovingAnything(bool enabled, bool available)
    {
        await OnSta(() =>
        {
            int reads = 0, configurations = 0;
            var options = new MouseWithoutBorders.FrmMatrix.ApprovedAdministratorLaunchOptions(
                () => { reads++; return enabled; }, () => available,
                _ => { configurations++; return Task.FromResult(true); }, _ => Assert.Fail());
            using var content = options.Content;
            Assert.AreEqual(1, reads);
            Assert.AreEqual(0, configurations, "Opening Settings must never request approval or change a scheduled task.");
            Assert.AreEqual(available, options.SetUp.Enabled);
            Assert.AreEqual(enabled, options.Remove.Enabled, "Removal must remain available when the ordinary administrator option is off.");
            Assert.AreEqual(enabled ? "Update approved launch…" : "Set up approved launch…", options.SetUp.Text);
            Assert.IsTrue(options.Status.Text.Contains(enabled ? "is configured" : "is off", StringComparison.Ordinal));
            Assert.IsTrue(options.Help.Text.Contains("Approve setup once", StringComparison.Ordinal));
            Assert.IsTrue(options.Help.Text.Contains("without another prompt at each launch", StringComparison.Ordinal));
            Assert.IsTrue(options.Help.Text.Contains("Updates to this protected copy require approval", StringComparison.Ordinal));
            Assert.IsTrue(options.Help.Text.Contains("separate from the Secure Desktop service", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public async Task SetupUpdateAndRemovalRequireTheirOwnExplicitButtonClicks()
    {
        await OnSta(() =>
        {
            bool enabled = false;
            var requested = new List<bool>();
            var options = new MouseWithoutBorders.FrmMatrix.ApprovedAdministratorLaunchOptions(
                () => enabled, () => true,
                value => { requested.Add(value); enabled = value; return Task.FromResult(true); }, _ => Assert.Fail());
            using var host = Host(options);
            options.SetUp.PerformClick();
            Assert.IsTrue(enabled && options.Remove.Enabled);
            Assert.AreEqual("Update approved launch…", options.SetUp.Text);
            Assert.IsTrue(options.SetUp.Enabled, "An existing protected copy must still be updateable after approval.");
            options.SetUp.PerformClick();
            options.Remove.PerformClick();
            Assert.IsFalse(enabled || options.Remove.Enabled);
            Assert.AreEqual("Set up approved launch…", options.SetUp.Text);
            CollectionAssert.AreEqual(new[] { true, true, false }, requested);
            host.Close();
        });
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PendingConfigurationDisablesBothButtonsAndPreventsReentrantApprovalRequests(bool enable)
    {
        await OnSta(() =>
        {
            bool enabled = !enable;
            int reads = 0, configurations = 0;
            bool? requested = null;
            var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var options = new MouseWithoutBorders.FrmMatrix.ApprovedAdministratorLaunchOptions(
                () => { reads++; return enabled; }, () => true,
                value => { requested = value; configurations++; return response.Task; }, _ => Assert.Fail());
            using var host = Host(options);
            (enable ? options.SetUp : options.Remove).PerformClick();
            Assert.AreEqual(enable, requested);
            Assert.IsFalse(options.SetUp.Enabled || options.Remove.Enabled);
            Assert.IsTrue(options.Status.Text.StartsWith(enable ? "Setting up" : "Removing", StringComparison.Ordinal));
            int readsWhileBusy = reads;
            options.Refresh();
            options.SetUp.PerformClick();
            options.Remove.PerformClick();
            Assert.AreEqual(readsWhileBusy, reads, "A tab refresh must not overwrite an in-progress approval status.");
            Assert.AreEqual(1, configurations);
            enabled = enable;
            response.SetResult(true);
            PumpUntil(() => options.SetUp.Enabled);
            Assert.AreEqual(enabled, options.Remove.Enabled);
            Assert.IsTrue(options.Status.Text.Contains(enabled ? "is configured" : "is off", StringComparison.Ordinal));
            host.Close();
        });
    }

    [TestMethod]
    public async Task UncompletedSetupKeepsTheReadBackStateAndDoesNotReportSuccess()
    {
        await OnSta(() =>
        {
            int configurations = 0;
            var options = new MouseWithoutBorders.FrmMatrix.ApprovedAdministratorLaunchOptions(
                () => false, () => true,
                _ => { configurations++; return Task.FromResult(false); }, _ => Assert.Fail());
            using var host = Host(options);
            options.SetUp.PerformClick();
            Assert.AreEqual(1, configurations);
            Assert.IsTrue(options.Status.Text.Contains("is off", StringComparison.Ordinal));
            Assert.IsTrue(options.Status.Text.Contains("Setup was not completed", StringComparison.Ordinal));
            Assert.IsTrue(options.SetUp.Enabled);
            Assert.IsFalse(options.Remove.Enabled);
            host.Close();
        });
    }

    [TestMethod]
    public async Task ConfigurationFailureShowsOneErrorAndRestoresControlsFromActualState()
    {
        await OnSta(() =>
        {
            int errors = 0;
            Exception? shown = null;
            var expected = new IOException("Deterministic setup failure.");
            var options = new MouseWithoutBorders.FrmMatrix.ApprovedAdministratorLaunchOptions(
                () => false, () => true,
                _ => Task.FromException<bool>(expected), error => { errors++; shown = error; });
            using var host = Host(options);
            options.SetUp.PerformClick();
            Assert.AreEqual(1, errors);
            Assert.AreSame(expected, shown);
            Assert.IsTrue(options.SetUp.Enabled);
            Assert.IsFalse(options.Remove.Enabled);
            Assert.IsTrue(options.Status.Text.Contains("is off", StringComparison.Ordinal));
            host.Close();
        });
    }

    [TestMethod]
    public async Task RefreshReflectsExternalChangesWithoutConfiguringAnything()
    {
        await OnSta(() =>
        {
            bool enabled = false, available = true;
            int configurations = 0;
            var options = new MouseWithoutBorders.FrmMatrix.ApprovedAdministratorLaunchOptions(
                () => enabled, () => available,
                _ => { configurations++; return Task.FromResult(true); }, _ => Assert.Fail());
            using var content = options.Content;
            enabled = true; available = false;
            options.Refresh();
            Assert.IsTrue(options.Remove.Enabled);
            Assert.IsFalse(options.SetUp.Enabled);
            Assert.AreEqual("Update approved launch…", options.SetUp.Text);
            enabled = false; available = true;
            options.Refresh();
            Assert.IsFalse(options.Remove.Enabled);
            Assert.IsTrue(options.SetUp.Enabled);
            Assert.AreEqual(0, configurations);
        });
    }

    [TestMethod]
    public async Task ReadFailuresStayInlineWithoutConfiguringOrOpeningADialog()
    {
        await OnSta(() =>
        {
            int configurations = 0;
            var options = new MouseWithoutBorders.FrmMatrix.ApprovedAdministratorLaunchOptions(
                () => throw new UnauthorizedAccessException("Deterministic read failure."), () => true,
                _ => { configurations++; return Task.FromResult(true); }, _ => Assert.Fail());
            using var content = options.Content;
            Assert.IsFalse(options.SetUp.Enabled || options.Remove.Enabled);
            Assert.IsTrue(options.Status.Text.StartsWith("Could not read", StringComparison.Ordinal));
            Assert.AreEqual(0, configurations);
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApprovedLaunchControlsWrapAtNormalAndLargerTextSizes(bool large)
    {
        await OnSta(() =>
        {
            int configurations = 0;
            var options = new MouseWithoutBorders.FrmMatrix.ApprovedAdministratorLaunchOptions(
                () => true, () => true,
                _ => { configurations++; return Task.FromResult(true); }, _ => Assert.Fail());
            using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, large ? 14F : SystemFonts.MessageBoxFont.SizeInPoints);
            using var host = Host(options, large ? new Size(900, 520) : new Size(720, 420), font);
            foreach (Control control in Descendants(options.Content).Where(control => control.Visible && control is Label or Button))
            {
                Size required = TextRenderer.MeasureText(control.Text, control.Font,
                    new Size(control.ClientSize.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                Assert.IsTrue(control.Height >= required.Height, $"{control.Name} clips its text.");
                var bounds = host.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                Assert.IsTrue(host.ClientRectangle.Contains(bounds), $"{control.Name} lies outside the visible settings section.");
            }
            Assert.IsFalse(options.SetUp.Bounds.IntersectsWith(options.Remove.Bounds), "Setup and removal must remain separate targets.");
            Assert.AreEqual(0, configurations);
            string folder = Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath(), "mwb-ui-previews");
            Directory.CreateDirectory(folder);
            using var bitmap = new Bitmap(host.Width, host.Height);
            host.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(folder, large ? "approved-administrator-launch-large.png" : "approved-administrator-launch-normal.png"), ImageFormat.Png);
            host.Close();
        });
    }

    private static System.Windows.Forms.Form Host(MouseWithoutBorders.FrmMatrix.ApprovedAdministratorLaunchOptions options,
        Size? size = null, Font? font = null)
    {
        var host = new System.Windows.Forms.Form
        {
            ClientSize = size ?? new Size(720, 420),
            AutoScaleMode = AutoScaleMode.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
            Padding = new Padding(8),
        };
        if (font is not null) host.Font = font;
        host.Controls.Add(options.Content);
        host.Show();
        Application.DoEvents();
        host.PerformLayout();
        return host;
    }

    private static void PumpUntil(Func<bool> completed)
    {
        var elapsed = Stopwatch.StartNew();
        while (!completed())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(3)) Assert.Fail("The injected configuration did not finish on the UI thread.");
            Application.DoEvents();
            Thread.Yield();
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private static async Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, true);
                action(); completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
