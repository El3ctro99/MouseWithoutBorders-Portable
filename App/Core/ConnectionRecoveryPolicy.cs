// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System;

namespace MouseWithoutBorders.Core;

internal enum ConnectionRecoveryAction
{
    None,
    RetryPeers,
    RecreateOwner,
}

/// <summary>
/// Schedules recovery without opening, probing or closing any socket. Callers
/// supply monotonic milliseconds and a snapshot of desktop/connection state.
/// </summary>
internal sealed class ConnectionRecoveryPolicy
{
    private static readonly int[] RetryDelays = { 1000, 2000, 5000, 10000, 30000 };
    private const int QuietMilliseconds = 500;
    private const int MaximumDebounceMilliseconds = 2000;
    private readonly object sync = new();
    private bool started;
    private bool suspended;
    private bool clockObserved;
    private long lastNow;
    private long nextPeerAttempt;
    private long nextListenerAttempt;
    private int peerAttempts;
    private int listenerFailures;
    private bool pending;
    private bool resetOwner;
    private long firstRequest;
    private long requestDue;
    private long requestGeneration;
    private long listenerGeneration;
    private bool listenerReserved;

    internal bool IsStarted { get { lock (sync) return started; } }
    internal bool IsSuspended { get { lock (sync) return suspended; } }

    internal void Start(long now)
    {
        lock (sync)
        {
            if (started) return;
            now = Observe(now);
            started = true;
            suspended = false;
            peerAttempts = listenerFailures = 0;
            nextPeerAttempt = nextListenerAttempt = now;
            listenerReserved = false;
        }
    }

    internal void Stop()
    {
        lock (sync)
        {
            started = false;
            suspended = false;
            listenerReserved = false;
            ClearRequest();
            // A later explicit start is a new lifecycle. Late shutdown
            // callbacks must not queue work for it or start recovery themselves.
        }
    }

    internal void Suspend()
    {
        lock (sync)
        {
            if (started) suspended = true;
        }
    }

    internal void Resume(long now)
    {
        lock (sync)
        {
            if (!started) return;
            now = Observe(now);
            bool wasSuspended = suspended;
            suspended = false;
            // A real resume needs a fresh settling interval, even if a network
            // notification was queued before suspend. Repeated resume events
            // otherwise share the same bounded debounce window.
            QueueRequest(now, reset: true, restartWindow: wasSuspended);
        }
    }

    internal void Request(long now, bool resetOwner = false)
    {
        lock (sync)
        {
            if (!started) return;
            QueueRequest(Observe(now), resetOwner, restartWindow: false);
        }
    }

    internal void PeerBecameMissing(long now)
    {
        lock (sync)
        {
            if (!started) return;
            // The runtime calls this only when a configured peer newly enters
            // the missing set. An unrelated offline peer's long backoff must
            // not delay this loss or a normal/protected desktop handoff.
            peerAttempts = 0;
            nextPeerAttempt = Observe(now);
            // An external address-change settling interval or deliberate
            // resume reset remains pending and retains its original deadline.
        }
    }

    internal void DesktopActivated(long now)
    {
        lock (sync)
        {
            if (!started) return;
            now = Observe(now);
            listenerFailures = 0;
            nextListenerAttempt = now;
            // Returning to the owning desktop must not wait for a previous
            // listener failure, or discard a distinct pending resume request.
        }
    }

    internal bool CanAttemptListener(long now)
    {
        lock (sync)
        {
            return started && !suspended && Observe(now) >= nextListenerAttempt;
        }
    }

    internal void ListenerAttemptStarting(long now)
    {
        lock (sync)
        {
            if (!started) return;
            // The normal null-owner path and explicit manual reopen do not go
            // through TakeDueAction. Capture their notification generation
            // before construction, so completion cannot erase a newer event.
            ReserveListener(Observe(now));
        }
    }

    internal void ListenerAttemptCompleted(long now, bool succeeded)
    {
        lock (sync)
        {
            if (!started) return;
            now = Observe(now);
            if (succeeded)
            {
                listenerFailures = peerAttempts = 0;
                nextListenerAttempt = nextPeerAttempt = now;
                // Creating a listener directly (initial launch/desktop return)
                // already fulfils an earlier request to recreate its owner.
                // Do not consume a notification queued after a reserved action.
                if (!listenerReserved || requestGeneration <= listenerGeneration) ClearRequest();
            }
            else
            {
                nextListenerAttempt = After(now, NextDelay(ref listenerFailures));
            }

            listenerReserved = false;
        }
    }

    internal ConnectionRecoveryAction TakeDueAction(long now, bool desktopActive, bool ownerAvailable, bool peersMissing)
    {
        lock (sync)
        {
            if (!started) return ConnectionRecoveryAction.None;
            now = Observe(now);
            if (suspended || !desktopActive) return ConnectionRecoveryAction.None;

            // Initial creation and desktop handoff are immediate. An absent
            // owner needs no extra debounce; its new listener consumes the reset.
            if (!ownerAvailable)
            {
                return now >= nextListenerAttempt ? ReserveListener(now) : ConnectionRecoveryAction.None;
            }

            if (pending && resetOwner)
            {
                return now >= requestDue && now >= nextListenerAttempt
                    ? ReserveListener(now) : ConnectionRecoveryAction.None;
            }

            if (!peersMissing)
            {
                peerAttempts = 0;
                nextPeerAttempt = now;
                ClearRequest();
                return ConnectionRecoveryAction.None;
            }

            if (pending)
            {
                if (now < requestDue) return ConnectionRecoveryAction.None;
                ClearRequest();
                nextPeerAttempt = now;
            }

            if (now < nextPeerAttempt) return ConnectionRecoveryAction.None;
            nextPeerAttempt = After(now, NextDelay(ref peerAttempts));
            return ConnectionRecoveryAction.RetryPeers;
        }
    }

    private ConnectionRecoveryAction ReserveListener(long now)
    {
        listenerGeneration = requestGeneration;
        listenerReserved = true;
        ClearRequest();
        // Completion normally runs immediately after the runtime action. This
        // fallback also prevents a forgotten failure report from causing a spin.
        nextListenerAttempt = After(now, RetryDelays[0]);
        return ConnectionRecoveryAction.RecreateOwner;
    }

    private void QueueRequest(long now, bool reset, bool restartWindow)
    {
        if (!pending || restartWindow)
        {
            firstRequest = now;
            pending = true;
        }

        resetOwner |= reset;
        requestDue = Math.Min(After(now, QuietMilliseconds), After(firstRequest, MaximumDebounceMilliseconds));
        requestGeneration++;
    }

    private void ClearRequest()
    {
        pending = resetOwner = false;
    }

    private long Observe(long now)
    {
        if (!clockObserved || now > lastNow) lastNow = now;
        clockObserved = true;
        return lastNow;
    }

    private static int NextDelay(ref int attempt)
    {
        int result = RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)];
        if (attempt < RetryDelays.Length - 1) attempt++;
        return result;
    }

    private static long After(long now, int milliseconds) => now > long.MaxValue - milliseconds ? long.MaxValue : now + milliseconds;
}
