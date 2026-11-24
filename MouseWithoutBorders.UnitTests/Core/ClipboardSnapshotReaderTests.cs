// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class ClipboardSnapshotReaderTests
{
    [TestMethod]
    public void PlainRichAndHtmlTextComeFromOneDataObject()
    {
        var data = new SnapshotData(new()
        {
            [DataFormats.UnicodeText] = "fixture plain",
            [DataFormats.Rtf] = "fixture rich",
            [DataFormats.Html] = "fixture html",
        });
        int acquisitions = 0;

        var result = ClipboardSnapshotReader.Read(() => 1, () => { acquisitions++; return data; });

        Assert.AreEqual(ClipboardSnapshotStatus.Ready, result.Status);
        Assert.AreEqual(1, acquisitions);
        Assert.AreEqual(1u, result.Sequence);
        Assert.IsFalse(result.IsFilePath);
        string text = result.Data.GetString();
        Assert.IsTrue(text.Contains("TXTfixture plain"));
        Assert.IsTrue(text.Contains("RTFfixture rich"));
        Assert.IsTrue(text.Contains("HTMfixture html"));
        CollectionAssert.AreEqual(new[] { DataFormats.UnicodeText, DataFormats.Rtf, DataFormats.Html }, data.Reads.ToArray());
    }

    [TestMethod]
    public void AdvertisedTextThatIsNotReadyNeverFallsThroughToImage()
    {
        var data = new SnapshotData(new()
        {
            [DataFormats.UnicodeText] = null,
            [DataFormats.Bitmap] = new object(),
        });

        var result = ClipboardSnapshotReader.Read(() => 1, () => data);

        Assert.AreEqual(ClipboardSnapshotStatus.Retry, result.Status);
        CollectionAssert.AreEqual(new[] { DataFormats.UnicodeText }, data.Reads.ToArray());
    }

    [TestMethod]
    public void AdvertisedEmptyHtmlDefersTheWholeTextSnapshot()
    {
        var data = new SnapshotData(new()
        {
            [DataFormats.UnicodeText] = "fixture plain",
            [DataFormats.Html] = string.Empty,
        });

        Assert.AreEqual(ClipboardSnapshotStatus.Retry, ClipboardSnapshotReader.Read(() => 1, () => data).Status);
    }

    [TestMethod]
    public void DelayedRenderingIsRetriedWithoutAnotherNotification()
    {
        var scheduler = new ClipboardCaptureScheduler();
        var data = new SnapshotData(new() { [DataFormats.UnicodeText] = string.Empty });
        scheduler.Notify(1, 0);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            long now = 50 + (attempt * 100);
            Assert.IsTrue(scheduler.TryBegin(now, out int ticket));
            if (attempt == 2) data.Values[DataFormats.UnicodeText] = "fixture ready";
            var result = ClipboardSnapshotReader.Read(() => 1, () => data);
            bool ready = result.Status == ClipboardSnapshotStatus.Ready;
            Assert.AreEqual(attempt == 2, ready);
            scheduler.Complete(ticket, ready, now);
        }

        Assert.IsFalse(scheduler.Pending);
        Assert.AreEqual(3, data.Reads.Count);
    }

    [TestMethod]
    public void BusyProviderCanBecomeReadyOnTheSameSequence()
    {
        int attempts = 0;
        var data = new SnapshotData(new() { [DataFormats.UnicodeText] = "fixture text" });
        IDataObject GetData()
        {
            if (++attempts == 1) throw new COMException("Fixture provider is busy.");
            return data;
        }

        Assert.AreEqual(ClipboardSnapshotStatus.Retry, ClipboardSnapshotReader.Read(() => 1, GetData).Status);
        Assert.AreEqual(ClipboardSnapshotStatus.Ready, ClipboardSnapshotReader.Read(() => 1, GetData).Status);
    }

    [TestMethod]
    public void SequenceChangeDuringReadRejectsTheSnapshot()
    {
        uint sequence = 1;
        var data = new SnapshotData(new() { [DataFormats.UnicodeText] = "fixture text" });
        data.BeforeRead = _ => sequence = 2;

        var result = ClipboardSnapshotReader.Read(() => sequence, () => data);

        Assert.AreEqual(ClipboardSnapshotStatus.Retry, result.Status);
        Assert.AreEqual(2u, result.Sequence);
    }

    [TestMethod]
    public void MissingObjectOrNotYetAdvertisedFormatsAreRetried()
    {
        Assert.AreEqual(ClipboardSnapshotStatus.Retry, ClipboardSnapshotReader.Read(() => 1, () => null).Status);
        Assert.AreEqual(ClipboardSnapshotStatus.Retry, ClipboardSnapshotReader.Read(() => 1, () => new SnapshotData(new())).Status);
    }

    [TestMethod]
    public void UnavailableSequenceDoesNotReadTheClipboardObject()
    {
        var result = ClipboardSnapshotReader.Read(() => 0, () => throw new AssertFailedException("Read with unknown sequence."));

        Assert.AreEqual(ClipboardSnapshotStatus.Retry, result.Status);
    }

    [TestMethod]
    public void HtmlOnlyAndAnsiTextAreSupported()
    {
        var html = new SnapshotData(new() { [DataFormats.Html] = "fixture html" });
        var ansi = new SnapshotData(new() { [DataFormats.Text] = "fixture ansi" });

        var htmlResult = ClipboardSnapshotReader.Read(() => 1, () => html);
        var ansiResult = ClipboardSnapshotReader.Read(() => 2, () => ansi);

        Assert.IsTrue(htmlResult.Data.GetString().StartsWith("HTMfixture html"));
        Assert.IsTrue(ansiResult.Data.GetString().StartsWith("TXTfixture ansi"));
    }

    [TestMethod]
    public void OversizedTextIsHandledWithoutPublishingPartialFormats()
    {
        var data = new SnapshotData(new() { [DataFormats.UnicodeText] = "fixture text" });

        var result = ClipboardSnapshotReader.Read(() => 1, () => data, maxTextCharacters: 1);

        Assert.AreEqual(ClipboardSnapshotStatus.TooLarge, result.Status);
    }

    [TestMethod]
    public void ImageIsMaterializedAsExactPngBytesAndDisposed()
    {
        var image = new Bitmap(2, 2);
        image.SetPixel(0, 0, Color.Green);
        var data = new SnapshotData(new() { [DataFormats.Bitmap] = image });

        var result = ClipboardSnapshotReader.Read(() => 1, () => data);

        Assert.AreEqual(ClipboardSnapshotStatus.Ready, result.Status);
        byte[] bytes = result.Data.GetByteArray();
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        using var stream = new MemoryStream(bytes);
        using var decoded = Image.FromStream(stream);
        Assert.AreEqual(2, decoded.Width);
        Assert.AreEqual(2, decoded.Height);
        Assert.ThrowsException<ArgumentException>(() => _ = image.Width);
    }

    [TestMethod]
    public void TypedBitmapIsReadWhenLegacyGetDataCannotSeeIt()
    {
        using var image = new Bitmap(2, 2);
        image.SetPixel(0, 0, Color.Green);
        IDataObject data = new TypedOnlyBitmapDataObject(image);
        Assert.IsNull(data.GetData(DataFormats.Bitmap, autoConvert: true));

        var result = ClipboardSnapshotReader.Read(() => 1, () => data);

        AssertPngPixel(result, Color.Green);
    }

    [TestMethod]
    public void BitmapAvailableOnlyByAutoConversionIsRead()
    {
        using var image = new Bitmap(2, 2);
        image.SetPixel(0, 0, Color.Blue);
        var data = new SnapshotData(new() { ["Fixture convertible image"] = new object() });
        data.ConvertibleValues[DataFormats.Bitmap] = image;
        Assert.IsFalse(data.GetFormats(autoConvert: false).Contains(DataFormats.Bitmap));
        Assert.IsFalse(data.GetDataPresent(DataFormats.Bitmap, autoConvert: false));

        var result = ClipboardSnapshotReader.Read(() => 1, () => data);

        AssertPngPixel(result, Color.Blue);
    }

    [DataTestMethod]
    [DataRow("<img alt='fixture'>")]
    [DataRow("")]
    public void BitmapTakesPrecedenceOverHtmlCompanionData(string html)
    {
        using var image = new Bitmap(2, 2);
        image.SetPixel(0, 0, Color.Red);
        var data = new SnapshotData(new()
        {
            [DataFormats.Bitmap] = image,
            [DataFormats.Html] = html,
        });

        var result = ClipboardSnapshotReader.Read(() => 1, () => data);

        AssertPngPixel(result, Color.Red);
        CollectionAssert.AreEqual(new[] { DataFormats.Bitmap }, data.Reads.ToArray());
    }

    [TestMethod]
    public void PlainTextStillTakesPrecedenceOverBitmapAndKeepsHtml()
    {
        using var image = new Bitmap(2, 2);
        var data = new SnapshotData(new()
        {
            [DataFormats.UnicodeText] = "fixture plain",
            [DataFormats.Bitmap] = image,
            [DataFormats.Html] = "<b>fixture plain</b>",
        });

        var result = ClipboardSnapshotReader.Read(() => 1, () => data);

        Assert.AreEqual(ClipboardSnapshotStatus.Ready, result.Status);
        Assert.IsTrue(result.Data.IsString);
        Assert.IsTrue(result.Data.GetString().Contains("TXTfixture plain"));
        Assert.IsTrue(result.Data.GetString().Contains("HTM<b>fixture plain</b>"));
        CollectionAssert.AreEqual(new[] { DataFormats.UnicodeText, DataFormats.Html }, data.Reads.ToArray());
    }

    [TestMethod]
    public void OversizedImageIsRejectedAndDisposed()
    {
        var image = new Bitmap(1, 1);
        var data = new SnapshotData(new() { [DataFormats.Bitmap] = image });

        var result = ClipboardSnapshotReader.Read(() => 1, () => data, maxImageBytes: 0);

        Assert.AreEqual(ClipboardSnapshotStatus.TooLarge, result.Status);
        Assert.ThrowsException<ArgumentException>(() => _ = image.Width);
    }

    [TestMethod]
    public void FileDropRetainsTheExistingFirstFileBehavior()
    {
        var data = new SnapshotData(new() { [DataFormats.FileDrop] = new[] { @"C:\fixture\one.txt", @"C:\fixture\two.txt" } });

        var result = ClipboardSnapshotReader.Read(() => 1, () => data);

        Assert.AreEqual(ClipboardSnapshotStatus.Ready, result.Status);
        Assert.IsTrue(result.IsFilePath);
        Assert.AreEqual(@"C:\fixture\one.txt", result.Data.GetString());
    }

    [TestMethod]
    public void InvalidArgumentsAreRejected()
    {
        Assert.ThrowsException<ArgumentNullException>(() => ClipboardSnapshotReader.Read(null!, () => null));
        Assert.ThrowsException<ArgumentNullException>(() => ClipboardSnapshotReader.Read(() => 1, null!));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ClipboardSnapshotReader.Read(() => 1, () => null, -1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ClipboardSnapshotReader.Read(() => 1, () => null, maxImageBytes: -1));
    }

    private static void AssertPngPixel(ClipboardSnapshotRead result, Color expected)
    {
        Assert.AreEqual(ClipboardSnapshotStatus.Ready, result.Status);
        Assert.IsTrue(result.Data.IsByteArray);
        using var stream = new MemoryStream(result.Data.GetByteArray());
        using var decoded = new Bitmap(stream);
        Assert.AreEqual(expected.ToArgb(), decoded.GetPixel(0, 0).ToArgb());
    }

    private sealed class TypedOnlyBitmapDataObject : DataObject
    {
        internal TypedOnlyBitmapDataObject(Bitmap bitmap) => SetImage(bitmap);

#pragma warning disable CS0672 // Deliberately reproduce a legacy GetData implementation that cannot expose the typed bitmap.
        public override object? GetData(string format, bool autoConvert) => null;
#pragma warning restore CS0672
    }

    private sealed class SnapshotData : IDataObject
    {
        internal SnapshotData(Dictionary<string, object?> values) => Values = values;
        internal Dictionary<string, object?> Values { get; }
        internal Dictionary<string, object?> ConvertibleValues { get; } = new();
        internal List<string> Reads { get; } = new();
        internal Action<string>? BeforeRead { get; set; }

        public object? GetData(string format, bool autoConvert)
        {
            Reads.Add(format);
            BeforeRead?.Invoke(format);
            return Values.TryGetValue(format, out var value) ? value : autoConvert ? ConvertibleValues.GetValueOrDefault(format) : null;
        }

        public object? GetData(string format) => GetData(format, true);
        public object? GetData(Type format) => GetData(format.FullName!);
        public bool GetDataPresent(string format, bool autoConvert) => Values.ContainsKey(format) || (autoConvert && ConvertibleValues.ContainsKey(format));
        public bool GetDataPresent(string format) => GetDataPresent(format, true);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!);
        public string[] GetFormats(bool autoConvert) => autoConvert ? Values.Keys.Union(ConvertibleValues.Keys).ToArray() : Values.Keys.ToArray();
        public string[] GetFormats() => GetFormats(true);
        public void SetData(string format, bool autoConvert, object? data) => throw new NotSupportedException();
        public void SetData(string format, object? data) => throw new NotSupportedException();
        public void SetData(Type format, object? data) => throw new NotSupportedException();
        public void SetData(object? data) => throw new NotSupportedException();
    }
}
