// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class AdministratorLaunchUiTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task InitialDisplaySeparatesSavedChoiceFromActualPrivilegesWithoutWriting(bool saved, bool elevated)
    {
        await OnSta(() =>
        {
            int reads = 0, writes = 0;
            var options = new MouseWithoutBorders.FrmMatrix.AdministratorLaunchOptions(
                () => { reads++; return saved; }, _ => writes++, () => elevated,
                _ => Assert.Fail("Constructing the controls must not report a save error."));
            using var content = options.Content;
            Assert.AreEqual(1, reads);
            Assert.AreEqual(0, writes, "Opening Settings must not change this Windows account's administrator preference.");
            Assert.IsTrue(options.Choice.Enabled);
            Assert.AreEqual(saved, options.Choice.Checked);
            Assert.AreEqual("installationAdministrator", options.Choice.Name);
            Assert.AreEqual("Run as administrator", options.Choice.Text);
            Assert.IsTrue(options.Status.Text.StartsWith(elevated ? "Current session: Administrator." : "Current session: Standard user.", StringComparison.Ordinal));
            Assert.IsTrue(options.Status.Text.Contains(saved ? "on for the next launch" : "off for the next launch", StringComparison.Ordinal));
            Assert.IsTrue(options.Help.Text.Contains("Task Manager", StringComparison.Ordinal));
            Assert.IsTrue(options.Help.Text.Contains("input received from another PC", StringComparison.Ordinal));
            Assert.IsTrue(options.Help.Text.Contains("UAC approval at each launch", StringComparison.Ordinal));
            Assert.IsTrue(options.Help.Text.Contains("this Windows user and this EXE location", StringComparison.Ordinal));
            Assert.IsTrue(options.Help.Text.Contains("separately from portable preferences", StringComparison.Ordinal));
            Assert.IsTrue(options.Help.Text.Contains("Secure Desktop service", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public async Task ChangingTheCheckboxSavesEachChoiceWithoutChangingTheCurrentSession()
    {
        await OnSta(() =>
        {
            bool saved = false;
            var writes = new List<bool>();
            var options = new MouseWithoutBorders.FrmMatrix.AdministratorLaunchOptions(
                () => saved, value => { writes.Add(value); saved = value; }, () => false,
                _ => Assert.Fail("A successful save must not show an error."));
            using var content = options.Content;
            options.Choice.Checked = true;
            Assert.IsTrue(saved);
            Assert.IsTrue(options.Status.Text.StartsWith("Current session: Standard user.", StringComparison.Ordinal));
            options.Choice.Checked = false;
            Assert.IsFalse(saved);
            CollectionAssert.AreEqual(new[] { true, false }, writes);
            Assert.IsTrue(options.Status.Text.Contains("off for the next launch", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public async Task FailedSaveRestoresThePreviousCheckboxValueWithoutAnotherWrite()
    {
        await OnSta(() =>
        {
            int writes = 0, errors = 0;
            var options = new MouseWithoutBorders.FrmMatrix.AdministratorLaunchOptions(
                () => false,
                _ => { writes++; throw new IOException("The preference could not be saved."); },
                () => true, _ => errors++);
            using var content = options.Content;
            options.Choice.Checked = true;
            Assert.IsFalse(options.Choice.Checked);
            Assert.AreEqual(1, writes, "Restoring a checkbox after failure must not trigger a second write.");
            Assert.AreEqual(1, errors);
            Assert.IsTrue(options.Choice.Enabled);
            Assert.IsTrue(options.Status.Text.StartsWith("Current session: Administrator.", StringComparison.Ordinal));
            Assert.IsTrue(options.Status.Text.Contains("off for the next launch", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public async Task RefreshUpdatesSavedChoiceAndTokenDisplayWithoutWriting()
    {
        await OnSta(() =>
        {
            bool saved = false, elevated = false;
            int writes = 0;
            var options = new MouseWithoutBorders.FrmMatrix.AdministratorLaunchOptions(
                () => saved, _ => writes++, () => elevated, _ => Assert.Fail());
            using var content = options.Content;
            saved = true; elevated = true;
            options.Refresh();
            Assert.IsTrue(options.Choice.Checked);
            Assert.IsTrue(options.Status.Text.StartsWith("Current session: Administrator.", StringComparison.Ordinal));
            saved = false;
            options.Refresh();
            Assert.IsFalse(options.Choice.Checked);
            Assert.AreEqual(0, writes);
        });
    }

    [TestMethod]
    public async Task UnreadablePreferenceDisablesTheChoiceWithoutWritingOrOpeningADialog()
    {
        await OnSta(() =>
        {
            int writes = 0;
            var options = new MouseWithoutBorders.FrmMatrix.AdministratorLaunchOptions(
                () => throw new UnauthorizedAccessException("Access denied."), _ => writes++, () => false,
                _ => Assert.Fail("Read failure should be explained inline without a constructor-time dialog."));
            using var content = options.Content;
            Assert.IsFalse(options.Choice.Enabled);
            Assert.AreEqual(0, writes);
            Assert.IsTrue(options.Status.Text.Contains("Could not read", StringComparison.Ordinal));
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AdministratorControlsWrapWithoutClippingAtNormalAndLargerTextSizes(bool large)
    {
        await OnSta(() =>
        {
            int writes = 0;
            var options = new MouseWithoutBorders.FrmMatrix.AdministratorLaunchOptions(
                () => true, _ => writes++, () => false, _ => Assert.Fail());
            using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, large ? 14F : SystemFonts.MessageBoxFont.SizeInPoints);
            using var host = new System.Windows.Forms.Form
            {
                ClientSize = large ? new Size(900, 520) : new Size(720, 350),
                Font = font, AutoScaleMode = AutoScaleMode.None,
                StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
                ShowInTaskbar = false, Padding = new Padding(8),
            };
            host.Controls.Add(options.Content);
            host.Show(); Application.DoEvents(); host.PerformLayout();
            foreach (Control control in options.Content.Controls)
            {
                Assert.IsTrue(control.Right <= options.Content.ClientSize.Width);
                Size required = TextRenderer.MeasureText(control.Text, control.Font,
                    new Size(control.ClientSize.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                Assert.IsTrue(control.ClientSize.Height >= required.Height, $"{control.Name} clips its text.");
            }
            Assert.IsTrue(host.ClientRectangle.Contains(options.Content.Bounds));
            Assert.AreEqual(0, writes);
            string folder = Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath(), "mwb-ui-previews");
            Directory.CreateDirectory(folder);
            using var image = new Bitmap(host.Width, host.Height);
            host.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save(Path.Combine(folder, large ? "administrator-launch-large.png" : "administrator-launch-normal.png"), ImageFormat.Png);
            host.Close();
        });
    }

    private static async Task OnSta(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, true);
                action(); done.SetResult();
            }
            catch (Exception error) { done.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
}
