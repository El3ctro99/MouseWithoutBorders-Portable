// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#if PORTABLE_SINGLE_FILE

using System;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders;

internal partial class FrmAbout
{
    private bool layingOutPortableAbout;

    private void ApplyPortableAboutContent()
    {
        Opacity = 1D;
        Text = "About Mouse Without Borders — Portable";
        labelProductName.MaximumSize = new Size(ClientSize.Width - labelProductName.Left - Padding.Right, 0);
        labelProductName.Text = $"Mouse Without Borders — Portable\r\n{Application.ProductVersion}";
        labelCopyright.Location = new Point(labelCopyright.Left, labelProductName.Bottom + 6);
        labelCompanyName.Text = "Current fork maintainer: El3ctro99";
        groupBoxContributors.Text = " Portable edition and project credits ";

        StandaloneBranding.Apply(this);
        logoPictureBox.Image = StandaloneBranding.CreateProductIconBitmap(new Size(128, 128));
        logoPictureBox.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;

        var check = new Button
        {
            Name = "checkForUpdatesButton", Text = "Check for updates",
            Location = new Point(9, 532), Size = new Size(140, 27),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };
        var releases = new LinkLabel
        {
            Name = "releasePageLink", Text = "GitHub releases",
            Location = new Point(155, 537), Size = new Size(242, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left, AutoEllipsis = true,
        };
        check.Click += (_, _) => { using var update = new UpdateForm(); update.ShowDialog(this); };
        releases.LinkClicked += (_, _) =>
        {
            const string releaseUrl = StandaloneProduct.ProjectUrl + "/releases";
            try { _ = Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true }); }
            catch (Exception error)
            {
                Logger.Log("Open releases: " + error.Message);
                MessageBox.Show(this, "Windows could not open GitHub. Open this address in your browser:\r\n\r\n" + releaseUrl,
                    Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        Controls.Add(check); Controls.Add(releases);

        var originalCredits = textBoxContributors.Text.TrimStart();
        textBoxContributors.Text =
            "ABOUT THIS PORTABLE EDITION\r\n\r\n" +
            "This is an unofficial Windows-focused edition of Mouse Without Borders. It keeps the familiar MWB connection, input, clipboard, file-transfer, and machine-layout engine while packaging it as one self-contained EXE with its preferences beside it. PowerToys is not required.\r\n\r\n" +
            "WHAT THIS EDITION CHANGES\r\n\r\n" +
            "- Runs portably or installs a personal copy without a separate installer.\r\n" +
            "- Stores MouseWithoutBorders.prefs.json beside the active EXE.\r\n" +
            "- Removes PowerToys runtime dependencies and telemetry from this build.\r\n" +
            "- Uses the classic MWB pixel icon recolored green.\r\n" +
            "- Avoids recurring key-expiration prompts and keeps local logs bounded.\r\n\r\n" +
            "PROJECT ORIGINS AND CONTINUING WORK\r\n\r\n" +
            "Mouse Without Borders was created by Truong Do (Đỗ Đức Trường) and developed with help from the Microsoft Garage community. It was later incorporated into Microsoft PowerToys, where Microsoft engineers and open-source contributors continued maintaining and improving it.\r\n\r\n" +
            "This fork is maintained by El3ctro99 with user-directed, ChatGPT-assisted development. The upstream portable edition is maintained by aeae1. This fork builds on the work of both the upstream portable edition and the original developers and contributors listed below.\r\n\r\n" +
            originalCredits;

        // Windows may cap a scaled dialog at the available screen height.
        // Keep the footer visible and let the credits use the remaining space.
        okButton.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        labelProductName.AutoSize = labelCopyright.AutoSize = labelCompanyName.AutoSize = false;
        labelProductName.MaximumSize = labelCopyright.MaximumSize = labelCompanyName.MaximumSize = Size.Empty;
        Layout += (_, _) => LayoutPortableAbout(check, releases);
        Shown += (_, _) => LayoutPortableAbout(check, releases);
        LayoutPortableAbout(check, releases);
    }

    private void LayoutPortableAbout(Button check, LinkLabel releases)
    {
        if (layingOutPortableAbout || IsDisposed) return;
        layingOutPortableAbout = true;
        try
        {
            float scale = labelProductName.Font.SizeInPoints / 8.25f;
            int Unit(int value) => Math.Max(1, (int)Math.Ceiling(value * scale));
            int margin = Unit(9);
            int width = ClientSize.Width;
            int logoSize = Math.Min(Unit(128), width / 3);
            logoPictureBox.SetBounds(margin, margin, logoSize, logoSize);
            int titleLeft = logoPictureBox.Right + margin;
            int titleWidth = Math.Max(1, width - titleLeft - margin);
            int TextHeight(Label label, int availableWidth) => TextRenderer.MeasureText(label.Text, label.Font,
                new Size(availableWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            labelProductName.SetBounds(titleLeft, margin, titleWidth, TextHeight(labelProductName, titleWidth));
            labelCopyright.SetBounds(titleLeft, labelProductName.Bottom + Unit(6), titleWidth, TextHeight(labelCopyright, titleWidth));
            labelCompanyName.SetBounds(margin, Math.Max(logoPictureBox.Bottom, labelCopyright.Bottom) + Unit(12),
                Math.Max(1, width - 2 * margin), TextHeight(labelCompanyName, Math.Max(1, width - 2 * margin)));

            int footerHeight = Unit(30);
            int footerTop = ClientSize.Height - margin - footerHeight;
            int checkWidth = Math.Max(Unit(140), check.GetPreferredSize(Size.Empty).Width);
            int closeWidth = Math.Max(Unit(75), okButton.GetPreferredSize(Size.Empty).Width);
            check.SetBounds(margin, footerTop, checkWidth, footerHeight);
            okButton.SetBounds(width - margin - closeWidth, footerTop, closeWidth, footerHeight);
            int linkLeft = check.Right + margin;
            releases.SetBounds(linkLeft, footerTop + Unit(6), Math.Max(1, okButton.Left - margin - linkLeft), footerHeight - Unit(6));
            int groupTop = labelCompanyName.Bottom + Unit(4);
            groupBoxContributors.SetBounds(margin, groupTop, Math.Max(1, width - 2 * margin), Math.Max(1, footerTop - margin - groupTop));
        }
        finally { layingOutPortableAbout = false; }
    }
}

#endif
