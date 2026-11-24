// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System;
using System.Runtime.InteropServices;

namespace MouseWithoutBorders.Core;

internal static class ClipboardNativeState
{
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    internal static uint Sequence => GetClipboardSequenceNumber();

    internal static uint? SuccessfulOwnedSequence()
    {
        uint before = Sequence;
        GetWindowThreadProcessId(GetClipboardOwner(), out uint owner);
        uint after = Sequence;
        return before != 0 && before == after && owner == (uint)Environment.ProcessId ? after : null;
    }
}
