// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class MessageSocketRecoveryTests
{
    [TestMethod]
    public void PeerLeaseCoversDnsAndEveryChildUntilHandshakeOrFailure()
    {
        var gate = new PeerConnectionAttempts();
        Assert.IsTrue(gate.TryAcquire("Peer", out var dns));
        using var first = dns!.Fork();
        var second = dns.Fork();
        Assert.IsNotNull(first); Assert.IsNotNull(second);
        Assert.IsFalse(gate.TryAcquire(" peer ", out _));
        dns.Dispose();
        Assert.IsFalse(gate.TryAcquire("PEER", out _), "Returning DNS must not release active IP workers.");
        first.Dispose();
        Assert.IsFalse(gate.TryAcquire("peer", out _), "The other IP worker still owns the peer group.");
        second!.Dispose();
        Assert.IsTrue(gate.TryAcquire("peer", out var next));
        next!.Dispose();
    }

    [TestMethod]
    public void FinishedHandshakeCanReleaseItsLeaseBeforeLongLivedConnectionEnds()
    {
        var gate = new PeerConnectionAttempts();
        Assert.IsTrue(gate.TryAcquire("peer", out var dns));
        var connection = dns!.Fork();
        dns.Dispose();
        connection!.Dispose(); // Authentication releases the attempt, not the socket.
        Assert.IsNull(connection.Fork());
        Assert.IsTrue(gate.TryAcquire("peer", out var next));
        connection.Dispose(); // The old routine's finally cannot release the new lease.
        Assert.IsFalse(gate.TryAcquire("peer", out _));
        next!.Dispose();
    }

    [TestMethod]
    public void DifferentPeersAndDifferentOwnersAreIndependent()
    {
        var first = new PeerConnectionAttempts(); var other = new PeerConnectionAttempts();
        Assert.IsTrue(first.TryAcquire("one", out var one));
        Assert.IsTrue(first.TryAcquire("two", out var two));
        Assert.IsTrue(other.TryAcquire("one", out var separate));
        one!.Dispose(); two!.Dispose(); separate!.Dispose();
    }

    [TestMethod]
    public void ClosedOwnerRejectsNewAndLateDnsWorkersWithoutAffectingNewOwner()
    {
        var old = new PeerConnectionAttempts();
        Assert.IsTrue(old.TryAcquire("peer", out var dns));
        old.Stop(); old.Stop();
        Assert.IsNull(dns!.Fork(), "A late DNS result cannot add an IP worker after Close.");
        dns.Dispose();
        Assert.IsFalse(old.TryAcquire("peer", out _));
        var replacement = new PeerConnectionAttempts();
        Assert.IsTrue(replacement.TryAcquire("peer", out var current));
        current!.Dispose();
    }

    [TestMethod]
    public void ConcurrentRequestsCreateOnlyOnePeerAttempt()
    {
        var gate = new PeerConnectionAttempts();
        var leases = new System.Collections.Concurrent.ConcurrentBag<PeerConnectionAttempts.Lease>();
        Parallel.For(0, 64, _ => { if (gate.TryAcquire("peer", out var lease)) leases.Add(lease!); });
        Assert.AreEqual(1, leases.Count);
        foreach (var lease in leases) lease.Dispose();
        Assert.IsTrue(gate.TryAcquire("peer", out var next));
        next!.Dispose();
    }

    [TestMethod]
    public void LateAttemptGetsFreshThirtySecondBudgetForOrdinaryFailures()
    {
        long now = 10_000_000;
        long started = now;
        int connects = 0;
        bool result = MessageSocketRecovery.TryConnect(() =>
        { connects++; throw new SocketException((int)SocketError.NetworkUnreachable); }, () => false,
            () => now, delay => now += delay, out var failure);
        Assert.IsFalse(result);
        Assert.AreEqual(SocketError.NetworkUnreachable, failure!.SocketErrorCode);
        Assert.AreEqual(30_000L, now - started);
        Assert.AreEqual(31, connects);
        now += 1_000_000;
        started = now; connects = 0;
        Assert.IsTrue(MessageSocketRecovery.TryConnect(() =>
        { if (++connects == 1) throw new SocketException((int)SocketError.NetworkUnreachable); },
            () => false, () => now, delay => now += delay, out _));
        Assert.AreEqual(1000L, now - started, "A later call must not inherit the first call's expired deadline.");
    }

    [TestMethod]
    public void FreshBudgetPreservesFastDesktopHandoffAndOwnerCancellation()
    {
        long now = 1_000_000; int connects = 0;
        var waits = new List<int>();
        Assert.IsTrue(MessageSocketRecovery.TryConnect(() =>
        { if (++connects == 1) throw new SocketException((int)SocketError.ConnectionRefused); },
            () => false, () => now, delay => { now += delay; waits.Add(delay); }, out _));
        CollectionAssert.AreEqual(new[] { 50 }, waits);
        bool closed = false; connects = 0;
        Assert.IsFalse(MessageSocketRecovery.TryConnect(() =>
        { connects++; throw new SocketException((int)SocketError.ConnectionRefused); },
            () => closed, () => now, delay => { now += delay; closed = true; }, out _));
        Assert.AreEqual(1, connects);
    }

    [TestMethod]
    public void SilentHandshakeExpiresExactlyOnceAtDeadline()
    {
        long now = 7000; int closed = 0;
        using var deadline = new SocketHandshakeDeadline(() => closed++, () => now, 30000);
        now += 29999; deadline.CheckExpired(); Assert.AreEqual(0, closed);
        now++; deadline.CheckExpired(); deadline.CheckExpired();
        Assert.AreEqual(1, closed);
        Assert.IsFalse(deadline.TryAuthenticate());
    }

    [TestMethod]
    public void AuthenticationCancelsEvenQueuedTimeoutAndAllowsQuietPeers()
    {
        long now = 1000; int closed = 0;
        using var deadline = new SocketHandshakeDeadline(() => closed++, () => now, 30000);
        now += 29999;
        Assert.IsTrue(deadline.TryAuthenticate());
        now += 24 * 60 * 60 * 1000L;
        deadline.CheckExpired(); deadline.CheckExpired();
        Assert.AreEqual(0, closed, "Established idle sockets must never inherit the handshake read deadline.");
    }

    [TestMethod]
    public void LateAuthenticationCannotRacePastItsDeadline()
    {
        long now = 1000; int closed = 0;
        using var deadline = new SocketHandshakeDeadline(() => closed++, () => now, 30000);
        now += 30000;
        Assert.IsFalse(deadline.TryAuthenticate(), "Enforce the deadline even if the timer callback has not run yet.");
        deadline.CheckExpired();
        Assert.AreEqual(1, closed);
    }

    [TestMethod]
    public void DisposedHandshakeCannotBeClosedByLateTimerCallback()
    {
        long now = 0; int closed = 0;
        var deadline = new SocketHandshakeDeadline(() => closed++, () => now, 30000);
        deadline.Dispose(); now = 30000; deadline.CheckExpired(); deadline.Dispose();
        Assert.AreEqual(0, closed);
        Assert.IsFalse(deadline.TryAuthenticate());
    }

    [TestMethod]
    public void UnsupportedKeepAliveOptionsAreBestEffortWithoutSkippingOtherOptions()
    {
        var configured = new List<SocketOptionName>();
        Assert.IsFalse(MessageSocketRecovery.ConfigureKeepAlive((_, option, _) =>
        {
            configured.Add(option);
            if (option == SocketOptionName.TcpKeepAliveTime) throw new SocketException((int)SocketError.ProtocolOption);
        }));
        CollectionAssert.AreEqual(new[] { SocketOptionName.KeepAlive, SocketOptionName.TcpKeepAliveTime,
            SocketOptionName.TcpKeepAliveInterval, SocketOptionName.TcpKeepAliveRetryCount }, configured);
    }

    [TestMethod]
    public void WindowsLoopbackMessageSocketGetsKeepAliveWithoutReceiveTimeout()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndpoint);
        using var server = listener.AcceptSocket();
        Assert.IsTrue(MessageSocketRecovery.ConfigureKeepAlive(client));
        Assert.IsTrue(MessageSocketRecovery.ConfigureKeepAlive(server));
        foreach (var socket in new[] { client, server })
        {
            Assert.AreEqual(1, Convert.ToInt32(socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)));
            Assert.AreEqual(30, Convert.ToInt32(socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)));
            Assert.AreEqual(5, Convert.ToInt32(socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)));
            Assert.AreEqual(3, Convert.ToInt32(socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)));
            Assert.AreEqual(0, socket.ReceiveTimeout, "Quiet established peers must retain their unlimited read timeout.");
        }
    }
}
