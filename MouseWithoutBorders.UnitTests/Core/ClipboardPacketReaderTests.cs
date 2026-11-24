// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System.IO;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class ClipboardPacketReaderTests
{
    private const int DataSize = 48;
    private const ID Source = (ID)12;

    [TestMethod]
    public void MoreThanOneHundredInputPacketsDoNotInterruptClipboardCopy()
    {
        byte[] firstBytes = Encoding.Unicode.GetBytes("First clipboard text bit");
        byte[] secondBytes = Encoding.Unicode.GetBytes("Second clipboard section");
        var first = Chunk(PackageType.ClipboardText, firstBytes);
        var packets = new Queue<DATA>();
        for (int i = 0; i < 150; i++)
            packets.Enqueue(new DATA { Type = i % 2 == 0 ? PackageType.Mouse : PackageType.Keyboard, Src = Source });
        packets.Enqueue(Chunk(PackageType.ClipboardText, secondBytes));
        packets.Enqueue(End());
        var processed = new List<DATA>();

        byte[]? result = ClipboardPacketReader.Read(first, packets.Dequeue, processed.Add, () => true);

        CollectionAssert.AreEqual(firstBytes.Concat(secondBytes).ToArray(), result!);
        Assert.AreEqual(150, processed.Count);
        Assert.AreEqual(0, packets.Count);
    }

    [TestMethod]
    public void ImagePayloadRemainsImageBytes()
    {
        byte[] bytes = Enumerable.Range(0, DataSize).Select(i => (byte)i).ToArray();
        var packets = new Queue<DATA>(new[] { End() });

        byte[]? result = ClipboardPacketReader.Read(Chunk(PackageType.ClipboardImage, bytes), packets.Dequeue, _ => { }, () => true);

        CollectionAssert.AreEqual(bytes, result!);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void MixedPayloadIsRejectedAndDrainedThroughItsTerminator(bool wrongType)
    {
        var mixed = Chunk(wrongType ? PackageType.ClipboardImage : PackageType.ClipboardText,
            source: wrongType ? Source : (ID)13);
        var packets = new Queue<DATA>(new[]
        {
            mixed,
            new DATA { Type = PackageType.Mouse, Src = Source },
            Chunk(PackageType.ClipboardText),
            End(),
            new DATA { Type = PackageType.Heartbeat, Src = Source },
        });
        int processed = 0;

        byte[]? result = ClipboardPacketReader.Read(Chunk(PackageType.ClipboardText), packets.Dequeue, _ => processed++, () => true);

        Assert.IsNull(result);
        Assert.AreEqual(1, processed);
        Assert.AreEqual(PackageType.Heartbeat, packets.Dequeue().Type);
        Assert.AreEqual(0, packets.Count);
    }

    [TestMethod]
    public void AnotherSourcesTerminatorCannotCompleteTheCopy()
    {
        var packets = new Queue<DATA>(new[] { End((ID)13), Chunk(PackageType.ClipboardText), End() });

        byte[]? result = ClipboardPacketReader.Read(Chunk(PackageType.ClipboardText), packets.Dequeue, _ => { }, () => true);

        Assert.IsNull(result);
        Assert.AreEqual(0, packets.Count);
    }

    [TestMethod]
    public void DisabledClipboardIsDrainedWithoutReturningPayload()
    {
        var packets = new Queue<DATA>(new[] { Chunk(PackageType.ClipboardText), End() });

        byte[]? result = ClipboardPacketReader.Read(Chunk(PackageType.ClipboardText), packets.Dequeue, _ => { }, () => false);

        Assert.IsNull(result);
        Assert.AreEqual(0, packets.Count);
    }

    [TestMethod]
    public void DisablingDuringReceptionRejectsEvenIfSharingIsEnabledAgain()
    {
        var packets = new Queue<DATA>(new[] { Chunk(PackageType.ClipboardText), End() });
        int checkedCount = 0;

        byte[]? result = ClipboardPacketReader.Read(Chunk(PackageType.ClipboardText), packets.Dequeue, _ => { }, () => ++checkedCount != 2);

        Assert.IsNull(result);
        Assert.AreEqual(3, checkedCount);
        Assert.AreEqual(0, packets.Count);
    }

    [TestMethod]
    public void SizeLimitRejectsAndDrainsTheWholeBatch()
    {
        var packets = new Queue<DATA>(new[] { Chunk(PackageType.ClipboardText), End() });

        byte[]? result = ClipboardPacketReader.Read(Chunk(PackageType.ClipboardText), packets.Dequeue, _ => { }, () => true, maxBytes: DataSize);

        Assert.IsNull(result);
        Assert.AreEqual(0, packets.Count);
    }

    [TestMethod]
    public void ExactSizeLimitIsAccepted()
    {
        var packets = new Queue<DATA>(new[] { End() });

        byte[]? result = ClipboardPacketReader.Read(Chunk(PackageType.ClipboardText), packets.Dequeue, _ => { }, () => true, maxBytes: DataSize);

        Assert.AreEqual(DataSize, result!.Length);
    }

    [TestMethod]
    public void LimitSmallerThanInitialChunkStillDrainsTheBatch()
    {
        var packets = new Queue<DATA>(new[] { End() });

        byte[]? result = ClipboardPacketReader.Read(Chunk(PackageType.ClipboardText), packets.Dequeue, _ => { }, () => true, maxBytes: 0);

        Assert.IsNull(result);
        Assert.AreEqual(0, packets.Count);
    }

    [DataTestMethod]
    [DataRow((int)PackageType.Error)]
    [DataRow((int)PackageType.Invalid)]
    [DataRow(0)]
    public void BrokenFrameThrowsWithoutProcessingOrReadingAgain(int typeValue)
    {
        var type = (PackageType)typeValue;
        int reads = 0;

        Assert.ThrowsException<IOException>(() => ClipboardPacketReader.Read(
            Chunk(PackageType.ClipboardText),
            () => ++reads == 1 ? new DATA { Type = type } : throw new AssertFailedException("Read past a broken connection."),
            _ => throw new AssertFailedException("Processed a broken frame."),
            () => true));

        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public void NullFrameAndEndOfStreamThrowIOException()
    {
        Assert.ThrowsException<IOException>(() => ClipboardPacketReader.Read(
            Chunk(PackageType.ClipboardText), () => null!, _ => { }, () => true));
        Assert.ThrowsException<EndOfStreamException>(() => ClipboardPacketReader.Read(
            Chunk(PackageType.ClipboardText), () => throw new EndOfStreamException(), _ => { }, () => true));
    }

    [TestMethod]
    public void RejectedBatchStillReportsConnectionFailureInsteadOfSuccess()
    {
        Assert.ThrowsException<IOException>(() => ClipboardPacketReader.Read(
            Chunk(PackageType.ClipboardText), () => new DATA { Type = PackageType.Error }, _ => { }, () => false));
    }

    [TestMethod]
    public void MalformedArgumentsAreRejectedBeforeReceiving()
    {
        static DATA Receive() => throw new AssertFailedException("Received with malformed arguments.");

        Assert.ThrowsException<ArgumentNullException>(() => ClipboardPacketReader.Read(null!, Receive, _ => { }, () => true));
        Assert.ThrowsException<ArgumentNullException>(() => ClipboardPacketReader.Read(Chunk(PackageType.ClipboardText), null!, _ => { }, () => true));
        Assert.ThrowsException<ArgumentNullException>(() => ClipboardPacketReader.Read(Chunk(PackageType.ClipboardText), Receive, null!, () => true));
        Assert.ThrowsException<ArgumentNullException>(() => ClipboardPacketReader.Read(Chunk(PackageType.ClipboardText), Receive, _ => { }, null!));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ClipboardPacketReader.Read(Chunk(PackageType.ClipboardText), Receive, _ => { }, () => true, -1));
        Assert.ThrowsException<ArgumentException>(() => ClipboardPacketReader.Read(End(), Receive, _ => { }, () => true));
        Assert.ThrowsException<ArgumentException>(() => ClipboardPacketReader.Read(Chunk(PackageType.ClipboardText, source: ID.NONE), Receive, _ => { }, () => true));
        Assert.ThrowsException<ArgumentException>(() => ClipboardPacketReader.Read(Chunk(PackageType.ClipboardText, source: ID.ALL), Receive, _ => { }, () => true));
        Assert.ThrowsException<IOException>(() => ClipboardPacketReader.Read(new DATA { Type = PackageType.Error }, Receive, _ => { }, () => true));
    }

    private static DATA Chunk(PackageType type, byte[]? payload = null, ID source = Source)
    {
        var bytes = new byte[Package.PACKAGE_SIZE_EX];
        if (payload is not null)
        {
            Assert.AreEqual(DataSize, payload.Length);
            payload.CopyTo(bytes, Package.PACKAGE_SIZE_EX - DataSize);
        }

        return new DATA(bytes) { Type = type, Src = source };
    }

    private static DATA End(ID source = Source) => new() { Type = PackageType.ClipboardDataEnd, Src = source };
}
