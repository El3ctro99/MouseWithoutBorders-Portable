// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class ClipboardSequenceTrackerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void NewActualCopyIsAcceptedEvenWhenItCouldContainTheSameValue()
    {
        var tracker = new ClipboardSequenceTracker();
        Assert.AreEqual(ClipboardSequenceDecision.Accept, tracker.Decide(10, 10));
        tracker.MarkForwarded(10);
        Assert.AreEqual(ClipboardSequenceDecision.AlreadyForwarded, tracker.Decide(10, 10));
        Assert.AreEqual(ClipboardSequenceDecision.Accept, tracker.Decide(11, 11));
        tracker.MarkForwarded(11);
        Assert.AreEqual(ClipboardSequenceDecision.AlreadyForwarded, tracker.Decide(11, 11));
    }

    [TestMethod]
    public void ActiveWriteTakesPriorityAndNestedWritesRemainPendingUntilAllComplete()
    {
        var tracker = new ClipboardSequenceTracker();
        tracker.BeginRemoteWrite();
        tracker.BeginRemoteWrite();
        Assert.AreEqual(ClipboardSequenceDecision.RemoteWriteInProgress, tracker.Decide(10, 11));
        tracker.CompleteRemoteWrite(10);
        Assert.AreEqual(ClipboardSequenceDecision.RemoteWriteInProgress, tracker.Decide(10, 10));
        tracker.CompleteRemoteWrite(null);
        Assert.AreEqual(ClipboardSequenceDecision.RemoteEcho, tracker.Decide(10, 10));
        Assert.AreEqual(ClipboardSequenceDecision.Accept, tracker.Decide(11, 11));
    }

    [TestMethod]
    public void FailedRemoteWriteDoesNotSuppressItsSequence()
    {
        var tracker = new ClipboardSequenceTracker();
        tracker.BeginRemoteWrite();
        tracker.CompleteRemoteWrite(null);
        Assert.AreEqual(ClipboardSequenceDecision.Accept, tracker.Decide(20, 20));
    }

    [TestMethod]
    public void SuccessfulRemoteWriteSuppressesOnlyItsOwnObservedSequence()
    {
        var tracker = new ClipboardSequenceTracker();
        tracker.BeginRemoteWrite();
        tracker.CompleteRemoteWrite(30);
        Assert.AreEqual(ClipboardSequenceDecision.RemoteEcho, tracker.Decide(30, 30));
        Assert.AreEqual(ClipboardSequenceDecision.Accept, tracker.Decide(31, 31),
            "An immediate newer local copy must not be lost to a time window.");
    }

    [TestMethod]
    public void StaleSnapshotIsRejectedBeforeEchoAndForwardedChecks()
    {
        var tracker = new ClipboardSequenceTracker();
        tracker.BeginRemoteWrite();
        tracker.CompleteRemoteWrite(40);
        tracker.MarkForwarded(40);
        Assert.AreEqual(ClipboardSequenceDecision.Stale, tracker.Decide(40, 41));
        Assert.AreEqual(ClipboardSequenceDecision.RemoteEcho, tracker.Decide(40, 40));
    }

    [TestMethod]
    public void SequenceWrapUsesEqualityInsteadOfOrdering()
    {
        var tracker = new ClipboardSequenceTracker();
        tracker.MarkForwarded(uint.MaxValue);
        Assert.AreEqual(ClipboardSequenceDecision.Accept, tracker.Decide(0, 0));
        tracker.MarkForwarded(0);
        Assert.AreEqual(ClipboardSequenceDecision.AlreadyForwarded, tracker.Decide(0, 0));
        Assert.AreEqual(ClipboardSequenceDecision.Stale, tracker.Decide(uint.MaxValue, 0));
        tracker.BeginRemoteWrite();
        tracker.CompleteRemoteWrite(uint.MaxValue);
        Assert.AreEqual(ClipboardSequenceDecision.RemoteEcho, tracker.Decide(uint.MaxValue, uint.MaxValue));
        Assert.AreEqual(ClipboardSequenceDecision.AlreadyForwarded, tracker.Decide(0, 0));
    }

    [TestMethod]
    public void RemoteHistoryRetainsOnlyTheLatestSixteenDistinctSequences()
    {
        var tracker = new ClipboardSequenceTracker();
        for (uint sequence = 1; sequence <= 17; sequence++)
        {
            tracker.BeginRemoteWrite();
            tracker.CompleteRemoteWrite(sequence);
        }
        Assert.AreEqual(ClipboardSequenceDecision.Accept, tracker.Decide(1, 1));
        for (uint sequence = 2; sequence <= 17; sequence++)
            Assert.AreEqual(ClipboardSequenceDecision.RemoteEcho, tracker.Decide(sequence, sequence));
    }

    [TestMethod]
    public void RepeatedSuccessRefreshesHistoryWithoutPrematureEviction()
    {
        var tracker = new ClipboardSequenceTracker();
        for (uint sequence = 1; sequence <= 16; sequence++)
        {
            tracker.BeginRemoteWrite();
            tracker.CompleteRemoteWrite(sequence);
        }
        tracker.BeginRemoteWrite();
        tracker.CompleteRemoteWrite(1);
        tracker.BeginRemoteWrite();
        tracker.CompleteRemoteWrite(17);
        Assert.AreEqual(ClipboardSequenceDecision.RemoteEcho, tracker.Decide(1, 1));
        Assert.AreEqual(ClipboardSequenceDecision.Accept, tracker.Decide(2, 2));
        Assert.AreEqual(ClipboardSequenceDecision.RemoteEcho, tracker.Decide(17, 17));
    }

    [TestMethod]
    public void CompletionUnderflowThrowsWithoutPoisoningLaterWrites()
    {
        var tracker = new ClipboardSequenceTracker();
        Assert.ThrowsException<InvalidOperationException>(() => tracker.CompleteRemoteWrite(50));
        Assert.AreEqual(ClipboardSequenceDecision.Accept, tracker.Decide(50, 50));
        tracker.BeginRemoteWrite();
        tracker.CompleteRemoteWrite(51);
        Assert.AreEqual(ClipboardSequenceDecision.RemoteEcho, tracker.Decide(51, 51));
    }

    [TestMethod]
    public async Task ConcurrentWritesKeepTheGateClosedUntilEveryWriterCompletes()
    {
        var tracker = new ClipboardSequenceTracker();
        var allBegun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int begun = 0;
        Task[] writes = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            tracker.BeginRemoteWrite();
            if (Interlocked.Increment(ref begun) == 16) allBegun.TrySetResult();
            await release.Task;
            tracker.CompleteRemoteWrite((uint)(100 + index));
        })).ToArray();
        try
        {
            await allBegun.Task.WaitAsync(Deadline);
            Assert.AreEqual(ClipboardSequenceDecision.RemoteWriteInProgress, tracker.Decide(200, 200));
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(writes).WaitAsync(Deadline);
        Assert.AreEqual(ClipboardSequenceDecision.Accept, tracker.Decide(200, 200));
        for (uint sequence = 100; sequence < 116; sequence++)
            Assert.AreEqual(ClipboardSequenceDecision.RemoteEcho, tracker.Decide(sequence, sequence));
    }

    [TestMethod]
    public async Task CallerCanLockDecisionPublicationAndCommitAgainstNewRemoteWrites()
    {
        var tracker = new ClipboardSequenceTracker();
        using var attemptingWrite = new ManualResetEventSlim();
        Task write;
        lock (tracker.SyncRoot)
        {
            Assert.AreEqual(ClipboardSequenceDecision.Accept, tracker.Decide(300, 300));
            write = Task.Run(() =>
            {
                attemptingWrite.Set();
                tracker.BeginRemoteWrite();
                tracker.CompleteRemoteWrite(301);
            });
            Assert.IsTrue(attemptingWrite.Wait(Deadline));
            Assert.IsFalse(write.IsCompleted, "A remote write must wait for the publication transaction.");
            tracker.MarkForwarded(300);
            Assert.AreEqual(ClipboardSequenceDecision.AlreadyForwarded, tracker.Decide(300, 300));
        }
        await write.WaitAsync(Deadline);
        Assert.AreEqual(ClipboardSequenceDecision.RemoteEcho, tracker.Decide(301, 301));
    }
}
