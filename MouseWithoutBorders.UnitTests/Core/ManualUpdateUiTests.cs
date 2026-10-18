// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class ManualUpdateUiTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AboutShowsFullVersionMaintainerAndCreditsWithoutClipping(bool large)
    {
        await OnSta(() =>
        {
            using var about = new MouseWithoutBorders.FrmAbout();
            var fonts = new List<Font>();
            try
            {
                about.AutoScaleMode = AutoScaleMode.None;
                about.StartPosition = FormStartPosition.Manual;
                about.Location = new Point(-32000, -32000);
                about.TopMost = false;
                if (large)
                {
                    var originalFonts = Descendants(about).Prepend(about).Select(control => (control, control.Font)).ToArray();
                    about.Scale(new SizeF(1.5f, 1.5f));
                    foreach (var (control, original) in originalFonts)
                    {
                        var bigger = new Font(original.FontFamily, original.SizeInPoints * 1.5f, original.Style);
                        fonts.Add(bigger);
                        control.Font = bigger;
                    }
                }

                about.Show();
                Application.DoEvents();
                about.PerformLayout();
                SavePreview(about, large ? "about-update-large.png" : "about-update-normal.png");

                var controls = Descendants(about).ToArray();
                var title = controls.OfType<Label>().Single(control => control.Name == "labelProductName");
                Assert.IsTrue(title.Text.Contains(Application.ProductVersion, StringComparison.Ordinal));
                Assert.IsTrue(title.Text.EndsWith(Application.ProductVersion, StringComparison.Ordinal), "The full product version, including any preview suffix, must not be replaced by the numeric assembly version.");
                var maintainer = controls.OfType<Label>().Single(control => control.Name == "labelCompanyName");
                Assert.AreEqual("Current fork maintainer: El3ctro99", maintainer.Text);
                Assert.IsTrue(maintainer.Font.Bold, "The current maintainer belongs in a prominent label.");
                var credits = controls.OfType<TextBox>().Single(control => control.Name == "textBoxContributors");
                Assert.IsTrue(credits.Text.Contains("El3ctro99", StringComparison.Ordinal));
                Assert.IsTrue(credits.Text.Contains("aeae1", StringComparison.Ordinal));
                Assert.IsTrue(credits.Text.Contains("Truong Do", StringComparison.Ordinal));
                Assert.IsTrue(credits.Text.Contains("Microsoft Garage", StringComparison.Ordinal));
                Assert.IsTrue(credits.ReadOnly && credits.Multiline && credits.ScrollBars == ScrollBars.Vertical);

                foreach (Control control in controls.Where(control => control.Visible && control is Label or Button))
                {
                    AssertTextFits(control);
                    Assert.IsTrue(control.Parent!.ClientRectangle.Contains(control.Bounds), $"{control.Name} lies outside its parent: {control.Bounds}; parent {control.Parent.ClientSize}.");
                }

                var group = controls.OfType<GroupBox>().Single();
                var check = controls.OfType<Button>().Single(control => control.Name == "checkForUpdatesButton");
                var release = controls.OfType<LinkLabel>().Single(control => control.Name == "releasePageLink");
                var close = controls.OfType<Button>().Single(control => control.Name == "okButton");
                foreach (Control footer in new Control[] { check, release, close })
                {
                    Assert.IsTrue(footer.Top >= group.Bottom, $"{footer.Name} overlaps the credits section.");
                    Assert.IsTrue(about.ClientRectangle.Contains(footer.Bounds), $"{footer.Name} is clipped by the About window.");
                }
                Assert.IsFalse(check.Bounds.IntersectsWith(release.Bounds), "The update button and releases link must remain separate.");
                Assert.IsFalse(release.Bounds.IntersectsWith(close.Bounds), "The releases link must not overlap OK.");
                var copyright = controls.OfType<Label>().Single(control => control.Name == "labelCopyright");
                Assert.IsFalse(title.Bounds.IntersectsWith(copyright.Bounds), "The complete preview version must not cover the copyright line.");
                Assert.IsTrue(maintainer.Bottom <= group.Top, "The maintainer must remain above the credits section.");
                Assert.AreEqual(1D, about.Opacity);
                about.Close();
            }
            finally
            {
                foreach (Font font in fonts) font.Dispose();
            }
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpdateConstructorShowsSourceScopeAndDisabledInstallWithoutStartingNetwork(bool large)
    {
        await OnSta(() =>
        {
            using var update = new MouseWithoutBorders.UpdateForm();
            using var largeFont = new Font(update.Font.FontFamily, 14F);
            using var host = new System.Windows.Forms.Form
            {
                ClientSize = large ? new Size(update.ClientSize.Width * 3 / 2, update.ClientSize.Height * 3 / 2) : update.ClientSize,
                Padding = large ? new Padding(24) : update.Padding,
                AutoScaleMode = AutoScaleMode.None,
                Font = large ? largeFont : update.Font,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                ShowInTaskbar = false,
                Text = update.Text,
            };
            // The real constructor's entire content is laid out and rendered, but the
            // UpdateForm is never shown: its Shown handler must not make a network request.
            var content = update.Controls.OfType<TableLayoutPanel>().Single();
            host.Controls.Add(content);
            host.Show();
            Application.DoEvents();
            host.PerformLayout();
            SavePreview(host, large ? "updater-large.png" : "updater-normal.png");

            var controls = Descendants(host).ToArray();
            var version = controls.OfType<Label>().Single(control => control.Name == "installedVersion");
            Assert.AreEqual("Installed version: " + Application.ProductVersion, version.Text);
            var source = controls.OfType<Label>().Single(control => control.Name == "updateSource");
            Assert.IsTrue(source.Text.Contains("El3ctro99/MouseWithoutBorders-Portable", StringComparison.Ordinal));
            var status = controls.OfType<Label>().Single(control => control.Name == "updateStatus");
            Assert.AreEqual("Ready to check for updates.", status.Text, "Rendering the constructor must not trigger the network check.");
            var serviceNote = controls.OfType<Label>().Single(control => control.Name == "serviceUpdateNote");
            Assert.IsTrue(serviceNote.Text.Contains("normal application", StringComparison.Ordinal));
            if (serviceNote.Text.StartsWith("Optional UAC service:", StringComparison.Ordinal))
            {
                Assert.IsTrue(serviceNote.Text.Contains("normal application only", StringComparison.Ordinal));
                Assert.IsTrue(serviceNote.Text.Contains("full package on GitHub", StringComparison.Ordinal));
            }
            else
            {
                Assert.IsTrue(serviceNote.Text.Contains("pairing, preferences and transfer recovery files stay in place", StringComparison.Ordinal));
            }
            var install = controls.OfType<Button>().Single(control => control.Name == "installUpdate");
            Assert.IsFalse(install.Enabled, "An update cannot be installed before checking and validating a candidate.");
            Assert.IsTrue(controls.OfType<Button>().Single(control => control.Name == "checkUpdates").Enabled);
            Assert.IsFalse(controls.OfType<ProgressBar>().Single().Visible);

            foreach (Control control in controls.Where(control => control.Visible && control is Label or Button))
            {
                AssertTextFits(control);
                var onHost = host.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                Assert.IsTrue(host.ClientRectangle.Contains(onHost), $"{control.Name} is outside the updater window: {onHost}; client {host.ClientSize}.");
            }
            var footer = install.Parent!;
            foreach (var label in content.Controls.OfType<Label>().Where(label => label.Visible))
            {
                Assert.IsTrue(label.Bottom <= footer.Top, $"The footer overlaps {label.Name}.");
            }
            var buttons = footer.Controls.OfType<Button>().ToArray();
            for (int left = 0; left < buttons.Length; left++)
            {
                for (int right = left + 1; right < buttons.Length; right++)
                {
                    Assert.IsFalse(buttons[left].Bounds.IntersectsWith(buttons[right].Bounds), "Updater buttons must remain separate with larger text.");
                }
            }
            host.Close();
        });
    }

    private static void AssertTextFits(Control control)
    {
        TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        Size required = TextRenderer.MeasureText(control.Text, control.Font, new Size(control.ClientSize.Width, int.MaxValue), flags);
        Assert.IsTrue(required.Height <= control.ClientSize.Height,
            $"{control.Name} clips its text: required {required}, actual {control.ClientSize}; font {control.Font.SizeInPoints}; text {control.Text}.");
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private static void SavePreview(System.Windows.Forms.Form form, string fileName)
    {
        string directory = Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath(), "mwb-ui-previews");
        Directory.CreateDirectory(directory);
        using var preview = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));
        preview.Save(Path.Combine(directory, fileName), ImageFormat.Png);
    }

    private static async Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, true);
                action();
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
}
