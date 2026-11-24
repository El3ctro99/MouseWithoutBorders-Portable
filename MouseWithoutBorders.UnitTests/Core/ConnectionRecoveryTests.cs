// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class ConnectionRecoveryTests
{
    [TestMethod]
    public void HealthyPeerDoesNotHideAnotherMissingPeer()
    {
        var queried = new List<string>();
        bool missing = ConnectionRecovery.HasMissingPeers(
            new[] { " LOCAL ", "Healthy", "Offline", "" }, "local", peer =>
            {
                queried.Add(peer);
                return peer == "Healthy";
            });

        Assert.IsTrue(missing);
        CollectionAssert.AreEqual(new[] { "Healthy", "Offline" }, queried);
    }

    [TestMethod]
    public void LocalAndUnconfiguredSlotsNeverStartOutboundConnections()
    {
        Assert.IsFalse(ConnectionRecovery.HasMissingPeers(
            new[] { " LOCAL ", "", null!, " " }, "local", _ =>
            throw new AssertFailedException("Local or empty slot was treated as a remote peer.")));
        Assert.IsFalse(ConnectionRecovery.HasMissingPeers(null!, "local", _ => false));
    }

    [TestMethod]
    public void FullyConnectedMatrixHasNoRecoveryWork()
    {
        Assert.IsFalse(ConnectionRecovery.HasMissingPeers(
            new[] { "local", "One", " Two ", "Three" }, "local", peer =>
            peer is "One" or "Two" or "Three"));
    }

    [TestMethod]
    public void PeerMatchingUsesNamesRatherThanOnlyTheNumberOfConnectedSockets()
    {
        Assert.IsTrue(ConnectionRecovery.HasMissingPeers(
            new[] { "local", "NewPeer" }, "local", peer => peer == "RemovedPeer"));
    }

    [TestMethod]
    public void FailedAttemptsOfOfflinePeerDoNotHideANewLossOrResetTheThrottle()
    {
        var tracker = new MissingPeerTracker();
        Assert.IsTrue(tracker.Observe(Missing("Offline")));
        for (int retry = 0; retry < 100; retry++) Assert.IsFalse(tracker.Observe(Missing("OFFLINE")));
        Assert.IsTrue(tracker.Observe(Missing("Offline", "PreviouslyHealthy")));
        Assert.IsFalse(tracker.Observe(Missing("Offline", "PreviouslyHealthy")));
        Assert.IsFalse(tracker.Observe(Missing("Offline")), "A successful reconnect is not a new loss.");
        Assert.IsTrue(tracker.Observe(Missing("Offline", "PreviouslyHealthy")), "A later new loss must expedite again.");
    }

    [TestMethod]
    public void MissingSnapshotContainsOnlyConfiguredRemoteNamesAndDoesNotAliasTheTracker()
    {
        var missing = ConnectionRecovery.GetMissingPeers(new[] { "LOCAL", "A", "A", "B", "" }, "local", name => name == "B");
        CollectionAssert.AreEquivalent(new[] { "A" }, missing.ToArray());
        var tracker = new MissingPeerTracker();
        Assert.IsTrue(tracker.Observe(missing));
        missing.Clear();
        Assert.IsFalse(tracker.Observe(Missing("a")));
        tracker.Reset();
        Assert.IsTrue(tracker.Observe(Missing("A")));
    }

    private static HashSet<string> Missing(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);
}
