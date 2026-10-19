// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#nullable enable
using System;
using System.Collections.Generic;

namespace MouseWithoutBorders.Class;

/// <summary>One DNS/connection attempt group per peer and socket owner.</summary>
internal sealed class PeerConnectionAttempts
{
    private readonly object sync = new();
    private readonly Dictionary<string, Attempt> peers = new(StringComparer.OrdinalIgnoreCase);
    private bool stopped;

    internal bool TryAcquire(string peer, out Lease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peer);
        peer = peer.Trim();
        lock (sync)
        {
            if (stopped || peers.ContainsKey(peer)) { lease = null; return false; }
            var attempt = new Attempt(peer);
            peers.Add(peer, attempt);
            lease = new Lease(this, attempt);
            return true;
        }
    }

    internal void Stop()
    {
        lock (sync) { stopped = true; }
    }

    internal sealed class Attempt(string peer)
    {
        internal readonly string Peer = peer;
        internal int References = 1;
    }

    internal sealed class Lease : IDisposable
    {
        private readonly PeerConnectionAttempts owner;
        private readonly Attempt attempt;
        private bool disposed;

        internal Lease(PeerConnectionAttempts owner, Attempt attempt)
        { this.owner = owner; this.attempt = attempt; }

        internal Lease? Fork()
        {
            lock (owner.sync)
            {
                if (disposed || owner.stopped) return null;
                attempt.References++;
                return new Lease(owner, attempt);
            }
        }

        public void Dispose()
        {
            lock (owner.sync)
            {
                if (disposed) return;
                disposed = true;
                if (--attempt.References == 0) owner.peers.Remove(attempt.Peer);
            }
        }
    }
}
