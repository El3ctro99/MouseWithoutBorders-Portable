// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#nullable enable
using System;
using System.Collections.Generic;

namespace MouseWithoutBorders.Core;

internal enum ClipboardSequenceDecision
{
    Accept,
    RemoteWriteInProgress,
    Stale,
    RemoteEcho,
    AlreadyForwarded,
}

// Sequence equality identifies one actual Windows clipboard update. It does
// not suppress a newer local copy merely because it followed a remote write.
internal sealed class ClipboardSequenceTracker
{
    private const int RemoteSequenceLimit = 16;
    private readonly List<uint> remoteSequences = new(RemoteSequenceLimit);
    private uint? lastForwarded;
    private int remoteWritesInProgress;

    // The caller can hold this lock across Decide, publication and MarkForwarded.
    // Native/OLE clipboard writes must run outside it so callbacks cannot deadlock.
    internal object SyncRoot { get; } = new();

    internal void BeginRemoteWrite()
    {
        lock (SyncRoot)
        {
            checked { remoteWritesInProgress++; }
        }
    }

    internal void CompleteRemoteWrite(uint? successfulOwnedSequence)
    {
        lock (SyncRoot)
        {
            if (remoteWritesInProgress == 0)
                throw new InvalidOperationException("No remote clipboard write is in progress.");

            remoteWritesInProgress--;
            if (successfulOwnedSequence is uint sequence)
            {
                // Repeated completion for the same native sequence refreshes
                // its position without evicting another recent sequence.
                remoteSequences.Remove(sequence);
                remoteSequences.Add(sequence);
                if (remoteSequences.Count > RemoteSequenceLimit)
                    remoteSequences.RemoveAt(0);
            }
        }
    }

    internal ClipboardSequenceDecision Decide(uint captured, uint current)
    {
        lock (SyncRoot)
        {
            if (remoteWritesInProgress != 0)
                return ClipboardSequenceDecision.RemoteWriteInProgress;
            if (captured != current)
                return ClipboardSequenceDecision.Stale;
            if (remoteSequences.Contains(captured))
                return ClipboardSequenceDecision.RemoteEcho;
            if (lastForwarded == captured)
                return ClipboardSequenceDecision.AlreadyForwarded;
            return ClipboardSequenceDecision.Accept;
        }
    }

    internal void MarkForwarded(uint sequence)
    {
        lock (SyncRoot)
        {
            lastForwarded = sequence;
        }
    }
}
