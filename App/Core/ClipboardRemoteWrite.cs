// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System;

namespace MouseWithoutBorders.Core;

internal static class ClipboardRemoteWrite
{
    internal static uint? Apply(ClipboardSequenceTracker tracker, Action write, Func<uint?> ownedSequence)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(ownedSequence);
        tracker.BeginRemoteWrite();
        uint? successfulSequence = null;
        try
        {
            write();
            successfulSequence = ownedSequence();
            return successfulSequence;
        }
        finally { tracker.CompleteRemoteWrite(successfulSequence); }
    }
}
