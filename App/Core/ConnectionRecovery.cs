// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.Core;

// Callbacks only record intent. Socket ownership remains on the existing UI
// timer, including the normal/protected desktop handoff.
internal static class ConnectionRecovery
{
    private static readonly ConnectionRecoveryPolicy Policy = new();
    private static readonly MissingPeerTracker MissingPeers = new();
    private static volatile bool running;
    private static int clearAddressCache;
    private static bool desktopWasInactive;

    internal static bool IsRunning => running;

    internal static void Start()
    {
        if (SecureDesktopAgent.IsActive) return;
        clearAddressCache = 0;
        desktopWasInactive = false;
        MissingPeers.Reset();
        Policy.Start(Environment.TickCount64);
        running = true;
    }

    internal static void Stop()
    {
        running = false;
        Policy.Stop();
        Interlocked.Exchange(ref clearAddressCache, 0);
    }

    internal static void Suspend()
    {
        if (running) Policy.Suspend();
    }

    internal static void Resume()
    {
        if (!running) return;
        Interlocked.Exchange(ref clearAddressCache, 1);
        Policy.Resume(Environment.TickCount64);
    }

    internal static void NetworkChanged()
    {
        if (!running) return;
        Interlocked.Exchange(ref clearAddressCache, 1);
        Policy.Request(Environment.TickCount64);
    }

    internal static void DisplayActivated()
    {
        if (running) Policy.Request(Environment.TickCount64);
    }

    internal static void DesktopInactive()
    {
        desktopWasInactive = true;
    }

    internal static void DesktopActivated()
    {
        if (!running || !desktopWasInactive) return;
        desktopWasInactive = false;
        Policy.DesktopActivated(Environment.TickCount64);
    }

    internal static bool CanAttemptListener() => SecureDesktopAgent.IsActive ||
        (running && Policy.CanAttemptListener(Environment.TickCount64));

    internal static void ListenerAttemptStarting()
    {
        if (running) Policy.ListenerAttemptStarting(Environment.TickCount64);
    }

    internal static void ListenerAttemptCompleted(bool succeeded)
    {
        if (running) Policy.ListenerAttemptCompleted(Environment.TickCount64, succeeded);
    }

    internal static void MaintainActiveDesktop()
    {
        if (!running) return;

        if (Interlocked.Exchange(ref clearAddressCache, 0) != 0)
        {
            // Recheck reverse DNS after an address change; retain the configured
            // mapping, authentication state and host DNS cache.
            SocketStuff.ClearBadIPs();
        }

        SocketStuff owner = Common.Sk;
        bool available = owner != null && !owner.IsClosed;
        HashSet<string> missing = available && !SocketStuff.InvalidKeyFound ? GetMissingPeers(
            MachineStuff.MachineMatrix, Common.MachineName, Common.IsConnectedByAClientSocketTo) : new(StringComparer.OrdinalIgnoreCase);
        long now = Environment.TickCount64;
        if (available && MissingPeers.Observe(missing)) Policy.PeerBecameMissing(now);
        ConnectionRecoveryAction action = Policy.TakeDueAction(
            now, desktopActive: true, ownerAvailable: available, peersMissing: missing.Count != 0);

        if (!running) return;
        if (action == ConnectionRecoveryAction.RecreateOwner)
        {
            Logger.LogDebug("Connection recovery: refreshing sockets after resume.");
            Common.ReopenSockets(false);
            ListenerAttemptCompleted(Common.Sk != null && !Common.Sk.IsClosed);
        }
        else if (action == ConnectionRecoveryAction.RetryPeers && ReferenceEquals(owner, Common.Sk) && !owner.IsClosed)
        {
            // UpdateTCPClients starts work only for missing peers. It does not
            // close a healthy peer, listener, clipboard channel or input hook.
            MachineStuff.UpdateClientSockets("ConnectionRecovery");
        }
    }

    internal static bool HasMissingPeers(IEnumerable<string> peers, string localName, Func<string, bool> connected) =>
        GetMissingPeers(peers, localName, connected).Count != 0;

    internal static HashSet<string> GetMissingPeers(IEnumerable<string> peers, string localName, Func<string, bool> connected)
    {
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (peers == null) return missing;
        foreach (string entry in peers)
        {
            string peer = entry?.Trim();
            if (!string.IsNullOrEmpty(peer) && !string.Equals(peer, localName?.Trim(), StringComparison.OrdinalIgnoreCase)
                && !connected(peer)) missing.Add(peer);
        }
        return missing;
    }
}

// Distinguish a newly lost peer from a failed retry of an already offline peer.
// This keeps UAC handoffs fast even while a different configured PC is offline.
internal sealed class MissingPeerTracker
{
    private HashSet<string> previous = new(StringComparer.OrdinalIgnoreCase);

    internal bool Observe(HashSet<string> missing)
    {
        bool newlyMissing = false;
        foreach (string peer in missing)
            if (!previous.Contains(peer)) newlyMissing = true;
        previous = new(missing, StringComparer.OrdinalIgnoreCase);
        return newlyMissing;
    }

    internal void Reset() => previous.Clear();
}
