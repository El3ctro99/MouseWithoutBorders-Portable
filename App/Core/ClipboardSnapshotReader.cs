// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#nullable enable
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MouseWithoutBorders.Core;

internal enum ClipboardSnapshotStatus
{
    Ready,
    Retry,
    TooLarge,
}

internal readonly record struct ClipboardSnapshotRead(
    ClipboardSnapshotStatus Status, ByteArrayOrString Data, bool IsFilePath, uint Sequence);

internal static class ClipboardSnapshotReader
{
    private const string TextTypeSeparator = "{4CFF57F7-BEDD-43d5-AE8F-27A61E886F2F}";
    private const int MaxTextCharacters = 20 * 1024 * 1024;
    private const int MaxImageBytes = 50 * 1024 * 1024;

    internal static ClipboardSnapshotRead Read(
        Func<uint> sequence,
        Func<IDataObject?> getDataObject,
        int maxTextCharacters = MaxTextCharacters,
        int maxImageBytes = MaxImageBytes)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(getDataObject);
        ArgumentOutOfRangeException.ThrowIfNegative(maxTextCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(maxImageBytes);
        uint before = sequence();
        if (before == 0) return Retry(before);

        try
        {
            // One OLE data object supplies every representation. Reading formats
            // separately from the live clipboard can combine different copies.
            IDataObject? data = getDataObject();
            ClipboardSnapshotRead result = data == null
                ? Retry(before)
                : ReadData(data, before, maxTextCharacters, maxImageBytes);
            uint after = sequence();
            return before == after ? result : Retry(after);
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException or ArgumentException)
        {
            // Delayed rendering and clipboard contention need another attempt,
            // even if the owner never sends another clipboard notification.
            return Retry(sequence());
        }
    }

    private static ClipboardSnapshotRead ReadData(IDataObject data, uint sequence, int maxTextCharacters, int maxImageBytes)
    {
        string[] formats = data.GetFormats(autoConvert: false);
        bool Has(string format) => Array.IndexOf(formats, format) >= 0;
        bool unicode = Has(DataFormats.UnicodeText);
        bool ansi = Has(DataFormats.Text);
        bool oem = Has(DataFormats.OemText);
        bool rtf = Has(DataFormats.Rtf);
        bool html = Has(DataFormats.Html);
        // Image copies can also advertise an HTML representation. Preserve
        // plain text precedence, but prefer the bitmap to rich-only metadata.
        if (!(unicode || ansi || oem) &&
            (Has(DataFormats.Bitmap) || Has(DataFormats.Dib) || data.GetDataPresent(DataFormats.Bitmap, autoConvert: true)))
        {
            return ReadImage(data, sequence, maxImageBytes);
        }

        if (unicode || ansi || oem || rtf || html)
        {
            var text = new StringBuilder();
            bool Append(string format, string prefix)
            {
                // An advertised but unavailable representation must not make us
                // fall through to an image or send only half the rich text.
                if (data.GetData(format, autoConvert: false) is not string value || value.Length == 0)
                    return false;
                if ((long)text.Length + prefix.Length + value.Length + TextTypeSeparator.Length > maxTextCharacters)
                    throw new ClipboardSnapshotTooLargeException();
                text.Append(prefix).Append(value).Append(TextTypeSeparator);
                return true;
            }

            try
            {
                if ((unicode || ansi || oem) && !Append(unicode ? DataFormats.UnicodeText : ansi ? DataFormats.Text : DataFormats.OemText, "TXT"))
                    return Retry(sequence);
                if (rtf && !Append(DataFormats.Rtf, "RTF")) return Retry(sequence);
                if (html && !Append(DataFormats.Html, "HTM")) return Retry(sequence);
            }
            catch (ClipboardSnapshotTooLargeException)
            {
                return new(ClipboardSnapshotStatus.TooLarge, default, false, sequence);
            }

            return new(ClipboardSnapshotStatus.Ready, text.ToString(), false, sequence);
        }

        if (Has(DataFormats.FileDrop))
        {
            if (data.GetData(DataFormats.FileDrop, autoConvert: false) is not string[] paths ||
                paths.Length == 0 || string.IsNullOrWhiteSpace(paths[0])) return Retry(sequence);
            return new(ClipboardSnapshotStatus.Ready, paths[0], true, sequence);
        }

        // Some owners publish their formats after the notification. Retry this
        // state within the scheduler's budget instead of permanently dropping it.
        return Retry(sequence);
    }

    private static ClipboardSnapshotRead ReadImage(IDataObject data, uint sequence, int maxImageBytes)
    {
        // Match WinForms Clipboard.GetImage on the existing snapshot. The
        // legacy GetData path can miss images in a typed .NET 10 data object.
        using Image? image = data is ITypedDataObject typed
            ? typed.TryGetData<Image>(DataFormats.Bitmap, autoConvert: true, out var bitmap) ? bitmap : null
            : data.GetData(DataFormats.Bitmap, autoConvert: true) as Image;
        if (image == null) return Retry(sequence);

        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        if (stream.Length == 0) return Retry(sequence);
        if (stream.Length > maxImageBytes)
            return new(ClipboardSnapshotStatus.TooLarge, default, false, sequence);
        return new(ClipboardSnapshotStatus.Ready, stream.ToArray(), false, sequence);
    }

    private static ClipboardSnapshotRead Retry(uint sequence) => new(ClipboardSnapshotStatus.Retry, default, false, sequence);

    private sealed class ClipboardSnapshotTooLargeException : Exception { }
}
