// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#nullable enable
using System;
using System.Net.Sockets;

namespace MouseWithoutBorders.Class;

internal static class MessageSocketRecovery
{
    internal const int ConnectBudgetMilliseconds = 30000;
    internal const int HandshakeBudgetMilliseconds = 30000;
    internal const int KeepAliveIdleSeconds = 30;
    internal const int KeepAliveIntervalSeconds = 5;
    internal const int KeepAliveProbeCount = 3;

    internal static bool TryConnect(Action connect, Func<bool> isClosed, out SocketException? failure) =>
        TryConnect(connect, isClosed, () => Environment.TickCount64, System.Threading.Thread.Sleep, out failure);

    internal static bool TryConnect(Action connect, Func<bool> isClosed,
        Func<long> monotonicClock, Action<int> delay, out SocketException? failure)
    {
        // Every IP worker gets its own fresh budget, including workers started
        // hours after their listener owner. Never compare against wall-clock time.
        long deadline = monotonicClock() + ConnectBudgetMilliseconds;
        return TcpConnectionRetry.TryConnect(connect, deadline, isClosed, monotonicClock,
            monotonicClock, delay, out failure);
    }

    internal static bool ConfigureKeepAlive(Socket socket) => ConfigureKeepAlive(
        (level, option, value) => socket.SetSocketOption(level, option, value));

    internal static bool ConfigureKeepAlive(Action<SocketOptionLevel, SocketOptionName, int> setOption)
    {
        bool complete = true;
        foreach (var option in new[]
        {
            (SocketOptionLevel.Socket, SocketOptionName.KeepAlive, 1),
            (SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, KeepAliveIdleSeconds),
            (SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, KeepAliveIntervalSeconds),
            (SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, KeepAliveProbeCount),
        })
        {
            try { setOption(option.Item1, option.Item2, option.Item3); }
            catch (SocketException) { complete = false; }
            catch (NotSupportedException) { complete = false; }
            catch (ObjectDisposedException) { return false; }
        }
        // Older socket providers may support only a subset. This is best-effort
        // transport liveness, never a reason to reject an otherwise valid peer.
        return complete;
    }
}

/// <summary>Closes only an untrusted handshake; authenticated idle peers have no read timeout.</summary>
internal sealed class SocketHandshakeDeadline : IDisposable
{
    private enum State { Pending, Authenticated, Expired, Disposed }
    private readonly object sync = new();
    private readonly Action expire;
    private readonly Func<long> clock;
    private readonly long started;
    private readonly int budget;
    private readonly System.Threading.Timer? timer;
    private State state;

    internal SocketHandshakeDeadline(Action expire, Func<long> clock, int budget, bool scheduleTimer = false)
    {
        ArgumentNullException.ThrowIfNull(expire);
        ArgumentNullException.ThrowIfNull(clock);
        if (budget <= 0) throw new ArgumentOutOfRangeException(nameof(budget));
        this.expire = expire; this.clock = clock; this.budget = budget; started = clock();
        if (scheduleTimer)
            timer = new System.Threading.Timer(_ => CheckExpired(), null, budget, System.Threading.Timeout.Infinite);
    }

    internal static SocketHandshakeDeadline Start(Socket socket) => new(
        () => { try { socket.Dispose(); } catch (SocketException) { } },
        () => Environment.TickCount64, MessageSocketRecovery.HandshakeBudgetMilliseconds, scheduleTimer: true);

    internal bool TryAuthenticate()
    {
        bool timedOut = false;
        lock (sync)
        {
            if (state != State.Pending) return false;
            if (clock() - started >= budget) { state = State.Expired; timedOut = true; }
            else state = State.Authenticated;
        }
        timer?.Dispose();
        if (timedOut) expire();
        return !timedOut;
    }

    internal void CheckExpired()
    {
        lock (sync)
        {
            if (state != State.Pending || clock() - started < budget) return;
            state = State.Expired;
        }
        // Changing the state under the same lock as authentication prevents a
        // queued timer callback from closing a newly authenticated connection.
        expire();
    }

    public void Dispose()
    {
        lock (sync) { if (state == State.Pending) state = State.Disposed; }
        timer?.Dispose();
    }
}
