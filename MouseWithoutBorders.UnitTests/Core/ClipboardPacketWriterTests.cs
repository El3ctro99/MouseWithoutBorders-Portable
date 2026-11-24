// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class ClipboardPacketWriterTests
{
    [DataTestMethod]
    [DataRow(1, false)]
    [DataRow(48, false)]
    [DataRow(49, false)]
    [DataRow(1024, true)]
    public void WholeBatchPreservesPayloadTypeSourceAndPadding(int size, bool image)
    {
        byte[] payload = Enumerable.Range(0, size).Select(i => (byte)i).ToArray();
        var sent = new List<DATA>();
        Assert.IsTrue(ClipboardPacketWriter.Write(payload, image, (ID)42, packet => { sent.Add(packet); return true; }));
        Assert.AreEqual((size + 47) / 48 + 1, sent.Count);
        Assert.AreEqual(PackageType.ClipboardDataEnd, sent[^1].Type);
        Assert.IsTrue(sent.All(p => p.Src == (ID)42 && p.Des == ID.ALL));
        Assert.IsTrue(sent.SkipLast(1).All(p => p.Type == (image ? PackageType.ClipboardImage : PackageType.ClipboardText)));
        byte[] received = sent.SkipLast(1).SelectMany(p => p.Bytes.Skip(Package.PACKAGE_SIZE_EX - 48)).ToArray();
        CollectionAssert.AreEqual(payload, received.Take(size).ToArray());
        Assert.IsTrue(received.Skip(size).All(b => b == 0));
    }

    [TestMethod]
    public void ReconnectDoesNotSendRemainingChunksOrEndToReplacementOwner()
    {
        object original = new();
        object current = original;
        var packets = new List<PackageType>();
        Assert.IsFalse(ClipboardPacketWriter.Write(new byte[200], false, (ID)42, packet =>
        {
            if (!ReferenceEquals(current, original)) return false;
            packets.Add(packet.Type);
            current = new object();
            return true;
        }));
        CollectionAssert.AreEqual(new[] { PackageType.ClipboardText }, packets);
    }

    [TestMethod]
    public async Task QueuedTextAndImageBatchesCannotInterleave()
    {
        var queue = new ClipboardSendQueue();
        var packets = new List<PackageType>();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task text = queue.Enqueue(() => ClipboardPacketWriter.Write(new byte[200], false, (ID)42, packet =>
        {
            packets.Add(packet.Type);
            if (packets.Count == 1)
            {
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
            return true;
        }));
        try
        {
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(10)));
            Task image = queue.Enqueue(() => ClipboardPacketWriter.Write(new byte[100], true, (ID)42, packet =>
            {
                packets.Add(packet.Type);
                return true;
            }));
            release.Set();
            await Task.WhenAll(text, image).WaitAsync(TimeSpan.FromSeconds(10));
            CollectionAssert.AreEqual(
                Enumerable.Repeat(PackageType.ClipboardText, 5).Append(PackageType.ClipboardDataEnd)
                    .Concat(Enumerable.Repeat(PackageType.ClipboardImage, 3)).Append(PackageType.ClipboardDataEnd).ToArray(), packets);
        }
        finally { release.Set(); }
    }
}
