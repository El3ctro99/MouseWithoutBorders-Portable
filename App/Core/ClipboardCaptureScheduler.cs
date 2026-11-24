// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System;

namespace MouseWithoutBorders.Core;

internal enum ClipboardCaptureCompletion
{
    Handled,
    Waiting,
    Exhausted,
}

// The caller owns the STA timer. A notification changes the pending snapshot,
// never the deadline of an already scheduled capture.
internal sealed class ClipboardCaptureScheduler
{
    internal const int CaptureDelayMilliseconds = 50;
    internal const int RetryDelayMilliseconds = 100;
    private readonly int maxAttempts;
    private int revision;
    private int attempts;
    private long due;
    private bool capturing;

    internal ClipboardCaptureScheduler(int maxAttempts = 50)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        this.maxAttempts = maxAttempts;
    }

    internal bool Pending { get; private set; }

    internal uint Sequence { get; private set; }

    internal void Notify(uint sequence, long now)
    {
        if (!Pending)
        {
            Pending = true;
            due = now + CaptureDelayMilliseconds;
            Sequence = sequence;
            attempts = 0;
            revision++;
        }
        else if (sequence != Sequence)
        {
            Sequence = sequence;
            attempts = 0;
            revision++;
        }
    }

    internal bool TryBegin(long now, out int ticket)
    {
        ticket = revision;
        if (!Pending || capturing || now < due) return false;
        capturing = true;
        attempts++;
        return true;
    }

    internal ClipboardCaptureCompletion Complete(int ticket, bool handled, long now)
    {
        capturing = false;
        if (!Pending) return ClipboardCaptureCompletion.Handled;
        if (ticket != revision)
        {
            due = now + CaptureDelayMilliseconds;
            return ClipboardCaptureCompletion.Waiting;
        }

        if (handled)
        {
            Pending = false;
            return ClipboardCaptureCompletion.Handled;
        }

        if (attempts >= maxAttempts)
        {
            Pending = false;
            return ClipboardCaptureCompletion.Exhausted;
        }

        due = now + RetryDelayMilliseconds;
        return ClipboardCaptureCompletion.Waiting;
    }

    internal void Cancel()
    {
        Pending = false;
        capturing = false;
        revision++;
    }
}
