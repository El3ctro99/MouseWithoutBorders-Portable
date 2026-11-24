// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class ClipboardSendQueueTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task EnqueuedSendsKeepTheirOrderBehindAnActiveSend()
    {
        var queue = new ClipboardSendQueue();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var order = new List<int>();
        var sends = new List<Task>();
        sends.Add(queue.Enqueue(() =>
        {
            order.Add(0);
            started.Set();
            Assert.IsTrue(release.Wait(Deadline), "The test must release the active send.");
        }));
        try
        {
            Assert.IsTrue(started.Wait(Deadline));
            for (int i = 1; i <= 32; i++)
            {
                int item = i;
                sends.Add(queue.Enqueue(() => order.Add(item)));
            }

            Assert.IsFalse(sends[^1].IsCompleted, "Enqueue must not bypass the blocked send.");
        }
        finally { release.Set(); }

        await Task.WhenAll(sends).WaitAsync(Deadline);
        CollectionAssert.AreEqual(Enumerable.Range(0, 33).ToArray(), order.ToArray());
    }

    [TestMethod]
    public async Task ConcurrentProducersCannotOverlapTheirSends()
    {
        var queue = new ClipboardSendQueue();
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseProducers = new ManualResetEventSlim();
        var sends = new ConcurrentBag<Task>();
        int active = 0;
        int completed = 0;
        bool firstFinished = false;
        sends.Add(queue.Enqueue(() =>
        {
            Assert.AreEqual(1, Interlocked.Increment(ref active));
            firstStarted.Set();
            try { Assert.IsTrue(releaseFirst.Wait(Deadline)); }
            finally
            {
                firstFinished = true;
                Interlocked.Decrement(ref active);
                Interlocked.Increment(ref completed);
            }
        }));
        try
        {
            Assert.IsTrue(firstStarted.Wait(Deadline));
            Task[] producers = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            {
                Assert.IsTrue(releaseProducers.Wait(Deadline));
                sends.Add(queue.Enqueue(() =>
                {
                    Assert.IsTrue(firstFinished, "The first send must finish before later sends start.");
                    Assert.AreEqual(1, Interlocked.Increment(ref active));
                    try { Interlocked.Increment(ref completed); }
                    finally { Interlocked.Decrement(ref active); }
                }));
            })).ToArray();
            releaseProducers.Set();
            await Task.WhenAll(producers).WaitAsync(Deadline);
            Assert.AreEqual(1, Volatile.Read(ref active));
            Assert.AreEqual(0, Volatile.Read(ref completed));
        }
        finally
        {
            releaseProducers.Set();
            releaseFirst.Set();
        }

        await Task.WhenAll(sends).WaitAsync(Deadline);
        Assert.AreEqual(33, completed);
        Assert.AreEqual(0, active);
    }

    [TestMethod]
    public async Task FailedSendRemainsObservableAndDoesNotBlockLaterSends()
    {
        var queue = new ClipboardSendQueue();
        var failure = new InvalidOperationException("Synthetic send failure.");
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task failed = queue.Enqueue(() =>
        {
            started.Set();
            Assert.IsTrue(release.Wait(Deadline));
            throw failure;
        });
        int laterRuns = 0;
        Task later;
        try
        {
            Assert.IsTrue(started.Wait(Deadline));
            later = queue.Enqueue(() => Interlocked.Increment(ref laterRuns));
        }
        finally { release.Set(); }

        var observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await failed.WaitAsync(Deadline));
        Assert.AreSame(failure, observed);
        await later.WaitAsync(Deadline);
        Assert.AreEqual(1, laterRuns);
        Assert.IsTrue(failed.IsFaulted);

        await queue.Enqueue(() => Interlocked.Increment(ref laterRuns)).WaitAsync(Deadline);
        Assert.AreEqual(2, laterRuns);
    }

    [TestMethod]
    public async Task SendUsesDefaultSchedulerAndDoesNotHoldEnqueueLock()
    {
        var queue = new ClipboardSendQueue();
        var schedulers = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, maxConcurrencyLevel: 1);
        Task? nested = null;
        try
        {
            await Task.Factory.StartNew(async () =>
            {
                Assert.AreSame(schedulers.ExclusiveScheduler, TaskScheduler.Current);
                await queue.Enqueue(() =>
                {
                    Assert.AreSame(TaskScheduler.Default, TaskScheduler.Current);
                    // A different thread can enqueue while this action is executing.
                    Task.Run(() => { nested = queue.Enqueue(() => { }); })
                        .WaitAsync(Deadline).GetAwaiter().GetResult();
                }).WaitAsync(Deadline);
            }, CancellationToken.None, TaskCreationOptions.None, schedulers.ExclusiveScheduler).Unwrap().WaitAsync(Deadline);
            Assert.IsNotNull(nested);
            await nested.WaitAsync(Deadline);
        }
        finally { schedulers.Complete(); }
    }

    [TestMethod]
    public async Task NullSendIsRejectedWithoutBreakingTheQueue()
    {
        var queue = new ClipboardSendQueue();
        Assert.ThrowsException<ArgumentNullException>(() => queue.Enqueue(null!));
        int ran = 0;
        await queue.Enqueue(() => ran++).WaitAsync(Deadline);
        Assert.AreEqual(1, ran);
    }
}
