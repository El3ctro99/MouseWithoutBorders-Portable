// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class TcpConnectionRetryTests
{
    [TestMethod]
    public void ImmediateSuccessDoesNotWaitEvenAfterStartupBudgetExpired()
    {
        var clock = new RetryClock { LegacyNow = 60000 };
        int attempts = 0;

        bool connected = Run(() => attempts++, originalDeadline: 30000, clock, out var failure);

        Assert.IsTrue(connected);
        Assert.IsNull(failure);
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(0, clock.Delays.Count);
    }

    [DataTestMethod]
    [DataRow(SocketError.ConnectionRefused, -1L)]
    [DataRow(SocketError.ConnectionRefused, 30000L)]
    [DataRow(SocketError.ConnectionReset, -1L)]
    [DataRow(SocketError.ConnectionReset, 30000L)]
    public void HandoffFailureThenSuccessRetriesAfterFiftyMilliseconds(SocketError error, long originalDeadline)
    {
        var clock = new RetryClock();
        int attempts = 0;

        bool connected = Run(() =>
        {
            if (++attempts == 1) throw new SocketException((int)error);
        }, originalDeadline, clock, out var failure);

        Assert.IsTrue(connected);
        Assert.IsNull(failure);
        Assert.AreEqual(2, attempts);
        CollectionAssert.AreEqual(new[] { 50 }, clock.Delays);
        Assert.AreEqual(50L, clock.MonotonicNow);
    }

    [DataTestMethod]
    [DataRow(SocketError.ConnectionRefused)]
    [DataRow(SocketError.ConnectionReset)]
    public void SlowInitialConnectDoesNotConsumeHandoffRetryGrace(SocketError error)
    {
        var clock = new RetryClock();
        int attempts = 0;

        bool connected = Run(() =>
        {
            if (++attempts == 1)
            {
                clock.Advance(2100);
                throw new SocketException((int)error);
            }
        }, originalDeadline: -1, clock, out var failure);

        Assert.IsTrue(connected);
        Assert.IsNull(failure);
        Assert.AreEqual(2, attempts);
        CollectionAssert.AreEqual(new[] { 50 }, clock.Delays);
        Assert.AreEqual(2150L, clock.MonotonicNow);
    }

    [TestMethod]
    public void PermanentRefusalGetsOnlyOneSecondGraceAfterStartupBudgetExpired()
    {
        var clock = new RetryClock { LegacyNow = 60000 };
        var refused = new SocketException((int)SocketError.ConnectionRefused);
        int attempts = 0;

        bool connected = Run(() => { attempts++; throw refused; }, 30000, clock, out var failure);

        Assert.IsFalse(connected);
        Assert.AreSame(refused, failure);
        Assert.AreEqual(21, attempts);
        CollectionAssert.AreEqual(Enumerable.Repeat(50, 20).ToArray(), clock.Delays);
        Assert.AreEqual(1000L, clock.MonotonicNow);
    }

    [TestMethod]
    public void PermanentRefusalReturnsToOriginalBackoffAndDeadlineAfterGrace()
    {
        var clock = new RetryClock();
        var refused = new SocketException((int)SocketError.ConnectionRefused);
        int attempts = 0;

        bool connected = Run(() => { attempts++; throw refused; }, 3500, clock, out var failure);

        Assert.IsFalse(connected);
        Assert.AreSame(refused, failure);
        Assert.AreEqual(24, attempts);
        CollectionAssert.AreEqual(
            Enumerable.Repeat(50, 20).Concat(new[] { 1000, 1000, 500 }).ToArray(), clock.Delays);
        Assert.AreEqual(3500L, clock.LegacyNow);
    }

    [DataTestMethod]
    [DataRow(SocketError.AccessDenied, -1L, 1)]
    [DataRow(SocketError.AccessDenied, 2500L, 4)]
    [DataRow(SocketError.NetworkUnreachable, -1L, 1)]
    [DataRow(SocketError.NetworkUnreachable, 2500L, 4)]
    public void OtherSocketErrorsKeepOriginalBackoffWithoutFreshGrace(
        SocketError error, long originalDeadline, int expectedAttempts)
    {
        var clock = new RetryClock();
        var expected = new SocketException((int)error);
        int attempts = 0;

        bool connected = Run(() => { attempts++; throw expected; }, originalDeadline, clock, out var failure);

        Assert.IsFalse(connected);
        Assert.AreSame(expected, failure, "The caller must receive the original socket error.");
        Assert.AreEqual(expectedAttempts, attempts);
        CollectionAssert.AreEqual(originalDeadline > 0 ? new[] { 1000, 1000, 500 } : Array.Empty<int>(), clock.Delays);
    }

    [TestMethod]
    public void DisposedSocketExitsWithoutDelayOrAnotherConnect()
    {
        var clock = new RetryClock();
        var disposed = new ObjectDisposedException("client");
        int attempts = 0;

        var actual = Assert.ThrowsException<ObjectDisposedException>(() =>
            Run(() => { attempts++; throw disposed; }, 30000, clock, out _));

        Assert.AreSame(disposed, actual);
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(0, clock.Delays.Count);
    }

    [TestMethod]
    public void OwnerClosedDuringRetryDelayDoesNotConnectAgain()
    {
        var clock = new RetryClock();
        clock.AfterDelay = () => clock.Closed = true;
        int attempts = 0;

        bool connected = Run(() =>
        {
            attempts++;
            throw new SocketException((int)SocketError.ConnectionRefused);
        }, 30000, clock, out _);

        Assert.IsFalse(connected);
        Assert.AreEqual(1, attempts);
        CollectionAssert.AreEqual(new[] { 50 }, clock.Delays);
    }

    [TestMethod]
    public void ClosedOwnerNeverStartsAConnect()
    {
        var clock = new RetryClock { Closed = true };
        int attempts = 0;

        bool connected = Run(() => attempts++, 30000, clock, out _);

        Assert.IsFalse(connected);
        Assert.AreEqual(0, attempts);
        Assert.AreEqual(0, clock.Delays.Count);
    }

    [TestMethod]
    public void CloseRacingWithConnectSuccessDoesNotGrantProtocolOwnership()
    {
        var clock = new RetryClock();
        int attempts = 0;

        bool connected = Run(() => { attempts++; clock.Closed = true; }, 30000, clock, out _);

        Assert.IsFalse(connected);
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(0, clock.Delays.Count);
    }

    [TestMethod]
    public void FastRetryCountAlsoBoundsAnUnchangingClock()
    {
        var clock = new RetryClock { AdvanceClock = false };
        var refused = new SocketException((int)SocketError.ConnectionRefused);
        int attempts = 0;

        bool connected = Run(() => { attempts++; throw refused; }, -1, clock, out var failure);

        Assert.IsFalse(connected);
        Assert.AreSame(refused, failure);
        Assert.AreEqual(21, attempts);
        Assert.AreEqual(20, clock.Delays.Count);
    }

    private static bool Run(Action connect, long originalDeadline, RetryClock clock, out SocketException failure) =>
        TcpConnectionRetry.TryConnect(connect, originalDeadline, () => clock.Closed,
            () => clock.LegacyNow, () => clock.MonotonicNow, clock.Delay, out failure);

    private sealed class RetryClock
    {
        internal long LegacyNow { get; set; }
        internal long MonotonicNow { get; private set; }
        internal bool Closed { get; set; }
        internal bool AdvanceClock { get; set; } = true;
        internal List<int> Delays { get; } = new();
        internal Action? AfterDelay { get; set; }

        internal void Delay(int milliseconds)
        {
            Delays.Add(milliseconds);
            if (AdvanceClock)
            {
                Advance(milliseconds);
            }

            AfterDelay?.Invoke();
        }

        internal void Advance(int milliseconds)
        {
            LegacyNow += milliseconds;
            MonotonicNow += milliseconds;
        }
    }
}
