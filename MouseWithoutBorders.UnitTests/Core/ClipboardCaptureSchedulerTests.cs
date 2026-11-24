// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class ClipboardCaptureSchedulerTests
{
    [TestMethod]
    public void NotificationsCoalesceWithoutSlidingTheCaptureDeadline()
    {
        var scheduler = new ClipboardCaptureScheduler();
        scheduler.Notify(1, 0);
        scheduler.Notify(2, 20);
        scheduler.Notify(3, 49);

        Assert.IsFalse(scheduler.TryBegin(49, out _));
        Assert.IsTrue(scheduler.TryBegin(50, out int ticket));
        Assert.AreEqual(3u, scheduler.Sequence);
        Assert.AreEqual(ClipboardCaptureCompletion.Handled, scheduler.Complete(ticket, true, 50));
        Assert.IsFalse(scheduler.Pending);
    }

    [TestMethod]
    public void FailedReadRetriesWithoutAnotherNotification()
    {
        var scheduler = new ClipboardCaptureScheduler();
        scheduler.Notify(1, 0);
        Assert.IsTrue(scheduler.TryBegin(50, out int ticket));
        Assert.AreEqual(ClipboardCaptureCompletion.Waiting, scheduler.Complete(ticket, false, 50));
        Assert.IsFalse(scheduler.TryBegin(149, out _));
        Assert.IsTrue(scheduler.TryBegin(150, out ticket));
        Assert.AreEqual(ClipboardCaptureCompletion.Handled, scheduler.Complete(ticket, true, 150));
        Assert.IsFalse(scheduler.Pending);
    }

    [TestMethod]
    public void ReentrantNotificationDoesNotGetClearedByTheEarlierRead()
    {
        var scheduler = new ClipboardCaptureScheduler();
        scheduler.Notify(1, 0);
        Assert.IsTrue(scheduler.TryBegin(50, out int oldTicket));
        scheduler.Notify(2, 60);
        Assert.IsFalse(scheduler.TryBegin(60, out _));

        Assert.AreEqual(ClipboardCaptureCompletion.Waiting, scheduler.Complete(oldTicket, true, 70));
        Assert.IsFalse(scheduler.TryBegin(119, out _));
        Assert.IsTrue(scheduler.TryBegin(120, out int newTicket));
        Assert.AreEqual(2u, scheduler.Sequence);
        Assert.AreEqual(ClipboardCaptureCompletion.Handled, scheduler.Complete(newTicket, true, 120));
    }

    [TestMethod]
    public void DuplicateNotificationsCannotResetTheRetryBudget()
    {
        var scheduler = new ClipboardCaptureScheduler(maxAttempts: 2);
        scheduler.Notify(1, 0);
        Assert.IsTrue(scheduler.TryBegin(50, out int ticket));
        scheduler.Notify(1, 55);
        Assert.AreEqual(ClipboardCaptureCompletion.Waiting, scheduler.Complete(ticket, false, 60));
        scheduler.Notify(1, 159);
        Assert.IsTrue(scheduler.TryBegin(160, out ticket));

        Assert.AreEqual(ClipboardCaptureCompletion.Exhausted, scheduler.Complete(ticket, false, 160));
        Assert.IsFalse(scheduler.Pending);
        Assert.IsFalse(scheduler.TryBegin(10000, out _));
    }

    [TestMethod]
    public void ANewSequenceGetsItsOwnRetryBudgetWithoutPostponingThePendingAttempt()
    {
        var scheduler = new ClipboardCaptureScheduler(maxAttempts: 1);
        scheduler.Notify(1, 0);
        Assert.IsTrue(scheduler.TryBegin(50, out int oldTicket));
        scheduler.Notify(2, 60);
        Assert.AreEqual(ClipboardCaptureCompletion.Waiting, scheduler.Complete(oldTicket, false, 70));
        Assert.IsTrue(scheduler.TryBegin(120, out int ticket));
        Assert.AreEqual(ClipboardCaptureCompletion.Exhausted, scheduler.Complete(ticket, false, 120));
    }

    [TestMethod]
    public void CancelledCaptureDoesNotRestartWhenItsCallbackCompletes()
    {
        var scheduler = new ClipboardCaptureScheduler();
        scheduler.Notify(1, 0);
        Assert.IsTrue(scheduler.TryBegin(50, out int ticket));
        scheduler.Cancel();

        Assert.AreEqual(ClipboardCaptureCompletion.Handled, scheduler.Complete(ticket, false, 60));
        Assert.IsFalse(scheduler.Pending);
    }

    [TestMethod]
    public void InvalidRetryBudgetIsRejected()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ClipboardCaptureScheduler(0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ClipboardCaptureScheduler(-1));
    }
}
