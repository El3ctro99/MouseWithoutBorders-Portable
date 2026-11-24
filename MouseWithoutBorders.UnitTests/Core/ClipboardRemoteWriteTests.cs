// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class ClipboardRemoteWriteTests
{
    [TestMethod]
    public void RemoteEchoIsMarkedOnlyAfterActualSuccessfulWrite()
    {
        var tracker = new ClipboardSequenceTracker();
        uint? result = ClipboardRemoteWrite.Apply(tracker, () =>
        {
            Assert.AreEqual(ClipboardSequenceDecision.RemoteWriteInProgress, tracker.Decide(10, 10));
        }, () => 10);
        Assert.AreEqual((uint?)10, result);
        Assert.AreEqual(ClipboardSequenceDecision.RemoteEcho, tracker.Decide(10, 10));
        Assert.AreEqual(ClipboardSequenceDecision.Accept, tracker.Decide(11, 11));
    }

    [TestMethod]
    public void FailedWriteDoesNotPoisonLaterLocalCopy()
    {
        var tracker = new ClipboardSequenceTracker();
        bool queried = false;
        Assert.ThrowsException<InvalidOperationException>(() => ClipboardRemoteWrite.Apply(tracker,
            () => throw new InvalidOperationException("Synthetic clipboard busy."),
            () => { queried = true; return 10; }));
        Assert.IsFalse(queried);
        Assert.AreEqual(ClipboardSequenceDecision.Accept, tracker.Decide(10, 10));
    }

    [TestMethod]
    public void AnotherOwnerOrSequenceReadFailureDoesNotSuppressItsLocalClipboard()
    {
        var tracker = new ClipboardSequenceTracker();
        Assert.IsNull(ClipboardRemoteWrite.Apply(tracker, () => { }, () => null));
        Assert.AreEqual(ClipboardSequenceDecision.Accept, tracker.Decide(10, 10));
        Assert.ThrowsException<InvalidOperationException>(() => ClipboardRemoteWrite.Apply(tracker,
            () => { }, () => throw new InvalidOperationException("Synthetic native lookup failure.")));
        Assert.AreEqual(ClipboardSequenceDecision.Accept, tracker.Decide(11, 11));
    }
}
