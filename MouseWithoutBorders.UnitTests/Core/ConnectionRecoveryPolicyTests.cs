// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class ConnectionRecoveryPolicyTests
{
    [TestMethod]
    public void ConstructorDoesNotStartRecoveryAndStartAllowsImmediateInitialListener()
    {
        var policy = new ConnectionRecoveryPolicy();
        Assert.IsFalse(policy.IsStarted);
        Assert.IsFalse(policy.IsSuspended);
        Assert.IsFalse(policy.CanAttemptListener(0));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 0, owner: false));
        policy.Start(10);
        Assert.IsTrue(policy.IsStarted);
        Assert.IsTrue(policy.CanAttemptListener(10));
        Assert.AreEqual(ConnectionRecoveryAction.RecreateOwner, Take(policy, 10, owner: false));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 11, owner: false));
        policy.ListenerAttemptCompleted(20, succeeded: true);
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 20));
    }

    [TestMethod]
    public void SoftNetworkRequestWaitsForQuietPeriodAndNeverRecreatesAHealthyOwner()
    {
        var policy = Started();
        policy.Request(0);
        policy.Request(300);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 799));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 800));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 801));
    }

    [TestMethod]
    public void ContinuousNotificationStormCannotPostponeRecoveryBeyondTwoSeconds()
    {
        var policy = Started();
        for (int now = 0; now < 2000; now += 100)
        {
            policy.Request(now);
            Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, now));
        }
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 2000));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 2001));
    }

    [TestMethod]
    public void MissingPeerRetriesContinueIndefinitelyWithBoundedBackoff()
    {
        var policy = Started();
        long now = 0;
        int[] delays = { 1000, 2000, 5000, 10000, 30000 };
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, now));
            int delay = delays[Math.Min(attempt, delays.Length - 1)];
            Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, now + delay - 1));
            now += delay;
        }
        Assert.IsTrue(now > 9L * 30000, "Recovery must continue after the old startup retry budget has expired.");
    }

    [TestMethod]
    public void AHealthyPeerDoesNotSuppressRecoveryOfAnotherMissingPeer()
    {
        var policy = Started();
        // peersMissing is true when any configured peer is missing, even when
        // another peer already has an authenticated healthy connection.
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 0, missing: true));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 1000, missing: true));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 1001, missing: false));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 1002, missing: true));
    }

    [TestMethod]
    public void AllHealthySnapshotsDoNoWorkAndResetTheMissingPeerBackoff()
    {
        var policy = Started();
        foreach (long now in new long[] { 0, 1000, 3000, 8000, 18000 })
            Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, now));
        policy.Request(19000);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 19000, missing: false));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 50000, missing: false));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 50001));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 51000));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 51001));
    }

    [TestMethod]
    public void NewNetworkRequestExpeditesAnExistingThirtySecondPeerBackoff()
    {
        var policy = Started();
        foreach (long now in new long[] { 0, 1000, 3000, 8000, 18000 }) Take(policy, now);
        policy.Request(19000);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 19499));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 19500));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 19501));
    }

    [TestMethod]
    public void NewlyMissingPeerDoesNotInheritAnotherOfflinePeersThirtySecondBackoff()
    {
        var policy = Started();
        foreach (long now in new long[] { 0, 1000, 3000, 8000, 18000 })
            Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, now));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 19000));
        policy.PeerBecameMissing(19000);
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 19000));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 19999));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 20000));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 21999));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 22000));
    }

    [TestMethod]
    public void NewlyMissingPeerPreservesAnExternalAddressChangeSettlingDeadline()
    {
        var policy = Started();
        policy.Request(0);
        policy.PeerBecameMissing(400);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 499));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 500));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 1499));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 1500));
    }

    [TestMethod]
    public void NewlyMissingPeerCannotConsumeOrPostponeAPendingResumeOwnerReset()
    {
        var policy = Started();
        policy.Suspend();
        policy.Resume(100);
        policy.PeerBecameMissing(500);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 599));
        Assert.AreEqual(ConnectionRecoveryAction.RecreateOwner, Take(policy, 600));
        policy.ListenerAttemptCompleted(600, succeeded: true);
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 601));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 602));
    }

    [TestMethod]
    public void NewlyMissingPeerCannotStartWorkWhileSuspendedOrOnAnInactiveDesktop()
    {
        var policy = Started();
        policy.Suspend();
        policy.PeerBecameMissing(100);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 100));
        Assert.IsFalse(policy.CanAttemptListener(100));
        policy.Resume(200);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 700, active: false));
        Assert.AreEqual(ConnectionRecoveryAction.RecreateOwner, Take(policy, 700));
        policy.ListenerAttemptCompleted(700, succeeded: true);
        policy.PeerBecameMissing(800);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 800, active: false));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 800));
    }

    [TestMethod]
    public void LateMissingPeerNotificationCannotRestartAStoppedPolicy()
    {
        var policy = Started();
        policy.Stop();
        policy.PeerBecameMissing(100);
        Assert.IsFalse(policy.IsStarted);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 100, owner: false));
        policy.Start(200);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 200, missing: false));
    }

    [TestMethod]
    public void SuspendSuppressesWorkAndResumeRequestsOneDebouncedOwnerReset()
    {
        var policy = Started();
        policy.Request(0);
        policy.Suspend();
        Assert.IsTrue(policy.IsSuspended);
        Assert.IsFalse(policy.CanAttemptListener(10000));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 10000, owner: false));
        policy.Resume(10000);
        Assert.IsFalse(policy.IsSuspended);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 10499, missing: false));
        Assert.AreEqual(ConnectionRecoveryAction.RecreateOwner, Take(policy, 10500, missing: false));
        policy.ListenerAttemptCompleted(10500, succeeded: true);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 10501, missing: false));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 20000, missing: false));
    }

    [TestMethod]
    public void RepeatedResumeNotificationsStillRespectTheTwoSecondDebounceLimit()
    {
        var policy = Started();
        policy.Suspend();
        for (int now = 0; now < 2000; now += 100)
        {
            policy.Resume(now);
            Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, now, missing: false));
        }
        Assert.AreEqual(ConnectionRecoveryAction.RecreateOwner, Take(policy, 2000, missing: false));
    }

    [TestMethod]
    public void OwnerResetSurvivesSoftNotificationsAndAnAllHealthySnapshot()
    {
        var policy = Started();
        policy.Request(0, resetOwner: true);
        policy.Request(200);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 699, missing: false));
        Assert.AreEqual(ConnectionRecoveryAction.RecreateOwner, Take(policy, 700, missing: false));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 701, missing: false));
    }

    [TestMethod]
    public void ImmediateNewListenerConsumesEarlierPendingResumeReset()
    {
        var policy = Started();
        policy.Suspend();
        policy.Resume(100);
        Assert.IsTrue(policy.CanAttemptListener(100));
        policy.ListenerAttemptStarting(100);
        policy.ListenerAttemptCompleted(110, succeeded: true);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 600, missing: false));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 601));
    }

    [TestMethod]
    public void NotificationArrivingDuringReservedListenerCreationIsNotLost()
    {
        var policy = Started();
        policy.Request(0, resetOwner: true);
        Assert.AreEqual(ConnectionRecoveryAction.RecreateOwner, Take(policy, 500, missing: false));
        policy.Request(501, resetOwner: true);
        policy.ListenerAttemptCompleted(502, succeeded: true);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 1000, missing: false));
        Assert.AreEqual(ConnectionRecoveryAction.RecreateOwner, Take(policy, 1001, missing: false));
    }

    [TestMethod]
    public void NewNotificationDuringDirectListenerCreationIsRetained()
    {
        var policy = Started();
        policy.Request(0, resetOwner: true);
        Assert.IsTrue(policy.CanAttemptListener(10));
        policy.ListenerAttemptStarting(10);
        policy.Request(11, resetOwner: true);
        policy.ListenerAttemptCompleted(12, succeeded: true);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 510, missing: false));
        Assert.AreEqual(ConnectionRecoveryAction.RecreateOwner, Take(policy, 511, missing: false));
    }

    [TestMethod]
    public void DirectListenerCreationConsumesOnlyOlderPendingWork()
    {
        var policy = Started();
        policy.Request(0, resetOwner: true);
        policy.ListenerAttemptStarting(10);
        policy.ListenerAttemptCompleted(20, succeeded: true);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 600, missing: false));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 2000, missing: false));
    }

    [TestMethod]
    public void AddressChangeDuringListenerCreationKeepsItsSettlingIntervalForMissingPeers()
    {
        var policy = Started();
        policy.ListenerAttemptStarting(0);
        policy.Request(1);
        policy.ListenerAttemptCompleted(2, succeeded: true);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 500));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 501));
    }

    [TestMethod]
    public void DirectListenerFailuresRetainTheirBackoffFromCompletionTime()
    {
        var policy = Started();
        policy.ListenerAttemptStarting(0);
        Assert.IsFalse(policy.CanAttemptListener(499));
        policy.ListenerAttemptCompleted(500, succeeded: false);
        Assert.IsFalse(policy.CanAttemptListener(1499));
        Assert.IsTrue(policy.CanAttemptListener(1500));
        policy.ListenerAttemptStarting(1500);
        policy.ListenerAttemptCompleted(2000, succeeded: false);
        Assert.IsFalse(policy.CanAttemptListener(3999));
        Assert.IsTrue(policy.CanAttemptListener(4000));
    }

    [TestMethod]
    public void InactiveDesktopRetainsPendingWorkWithoutCreatingOrRetryingSockets()
    {
        var policy = Started();
        policy.Request(0, resetOwner: true);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 2000, active: false));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 3000, owner: false, active: false));
        policy.DesktopActivated(3000);
        Assert.AreEqual(ConnectionRecoveryAction.RecreateOwner, Take(policy, 3000));
    }

    [TestMethod]
    public void DesktopReturnClearsListenerFailureDelayWithoutDiscardingPendingReset()
    {
        var policy = Started();
        policy.ListenerAttemptCompleted(0, succeeded: false);
        policy.ListenerAttemptCompleted(1000, succeeded: false);
        Assert.IsFalse(policy.CanAttemptListener(1100));
        policy.Request(1100, resetOwner: true);
        policy.DesktopActivated(1200);
        Assert.IsTrue(policy.CanAttemptListener(1200));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 1599, missing: false));
        Assert.AreEqual(ConnectionRecoveryAction.RecreateOwner, Take(policy, 1600, missing: false));
    }

    [TestMethod]
    public void ListenerFailuresHaveIndependentBackoffAndCannotSpin()
    {
        var policy = Started();
        long now = 0;
        int[] delays = { 1000, 2000, 5000, 10000, 30000, 30000, 30000 };
        foreach (int delay in delays)
        {
            Assert.AreEqual(ConnectionRecoveryAction.RecreateOwner, Take(policy, now, owner: false));
            policy.ListenerAttemptCompleted(now, succeeded: false);
            Assert.IsFalse(policy.CanAttemptListener(now + delay - 1));
            Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, now + delay - 1, owner: false));
            now += delay;
        }
        policy.ListenerAttemptCompleted(now, succeeded: true);
        policy.ListenerAttemptCompleted(now + 1, succeeded: false);
        Assert.IsFalse(policy.CanAttemptListener(now + 1000));
        Assert.IsTrue(policy.CanAttemptListener(now + 1001));
    }

    [TestMethod]
    public void MissingOwnerIsImmediateEvenWhenASoftRequestIsStillDebouncing()
    {
        var policy = Started();
        policy.Request(0);
        Assert.AreEqual(ConnectionRecoveryAction.RecreateOwner, Take(policy, 0, owner: false));
        policy.ListenerAttemptCompleted(0, succeeded: true);
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 1));
    }

    [TestMethod]
    public void StopDiscardsPendingWorkAndIgnoresLateResumeAndRequests()
    {
        var policy = Started();
        policy.Request(0, resetOwner: true);
        policy.Stop();
        policy.Resume(10000);
        policy.Request(10000);
        Assert.IsFalse(policy.IsStarted);
        Assert.IsFalse(policy.IsSuspended);
        Assert.IsFalse(policy.CanAttemptListener(10000));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 10000, owner: false));
        policy.Start(10000);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 10000, missing: false));
        Assert.AreEqual(ConnectionRecoveryAction.RecreateOwner, Take(policy, 10000, owner: false));
    }

    [TestMethod]
    public void DuplicateStartCannotResetAnActiveRetryThrottle()
    {
        var policy = Started();
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 0));
        policy.Start(10);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 999));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 1000));
    }

    [TestMethod]
    public void RegressingClockSamplesCannotCauseAnEarlyRetryOrResetTheDebounceWindow()
    {
        var policy = Started(1000);
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 1000));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 500));
        policy.Request(400);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 1499));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 1500));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 1200));
        Assert.AreEqual(ConnectionRecoveryAction.RetryPeers, Take(policy, 3500));
    }

    [TestMethod]
    public void ConcurrentNotificationsCoalesceWithoutLosingAnOwnerReset()
    {
        var policy = Started();
        Parallel.For(0, 100, index => policy.Request(index, resetOwner: index == 42));
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 598, missing: false));
        Assert.AreEqual(ConnectionRecoveryAction.RecreateOwner, Take(policy, 599, missing: false));
        policy.ListenerAttemptCompleted(600, succeeded: true);
        Assert.AreEqual(ConnectionRecoveryAction.None, Take(policy, 2000, missing: false));
    }

    private static ConnectionRecoveryPolicy Started(long now = 0)
    {
        var policy = new ConnectionRecoveryPolicy();
        policy.Start(now);
        return policy;
    }

    private static ConnectionRecoveryAction Take(ConnectionRecoveryPolicy policy, long now, bool owner = true, bool missing = true, bool active = true) =>
        policy.TakeDueAction(now, active, owner, missing);
}
