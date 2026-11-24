// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#nullable enable
using System;
using System.IO;

namespace MouseWithoutBorders.Core;

internal static class ClipboardPacketReader
{
    private const int DataSize = 48;
    private const int DataStart = Package.PACKAGE_SIZE_EX - DataSize;

    internal static byte[]? Read(
        DATA first,
        Func<DATA> receive,
        Action<DATA> processOther,
        Func<bool> clipboardEnabled,
        int maxBytes = (int)TransferHeader.MaxClipboardBytes)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(receive);
        ArgumentNullException.ThrowIfNull(processOther);
        ArgumentNullException.ThrowIfNull(clipboardEnabled);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ThrowIfBrokenFrame(first);
        if (first.Type is not PackageType.ClipboardText and not PackageType.ClipboardImage)
            throw new ArgumentException("The first frame must contain clipboard text or image data.", nameof(first));
        if (first.Src is ID.NONE or ID.ALL)
            throw new ArgumentException("The first frame must identify its source machine.", nameof(first));

        PackageType expectedType = first.Type;
        ID expectedSource = first.Src;
        using var payload = new MemoryStream();
        bool accepted = true;
        DATA frame = first;

        while (true)
        {
            ThrowIfBrokenFrame(frame);
            // Keep draining a rejected batch so the socket's next read starts at
            // the next message, rather than interpreting a tail as a new copy.
            accepted &= clipboardEnabled();
            switch (frame.Type)
            {
                case PackageType.ClipboardText:
                case PackageType.ClipboardImage:
                    accepted &= frame.Type == expectedType && frame.Src == expectedSource;
                    accepted &= payload.Length <= (long)maxBytes - DataSize;
                    if (accepted) payload.Write(frame.Bytes, DataStart, DataSize);
                    break;

                case PackageType.ClipboardDataEnd:
                    if (frame.Src == expectedSource)
                        return accepted ? payload.ToArray() : null;
                    accepted = false;
                    break;

                default:
                    // Input and heartbeat traffic can legitimately exceed one
                    // hundred frames while a larger clipboard copy is arriving.
                    processOther(frame);
                    break;
            }

            frame = receive();
        }
    }

    private static void ThrowIfBrokenFrame(DATA? frame)
    {
        if (frame is null || frame.Type is 0 or PackageType.Error or PackageType.Invalid)
            throw new IOException("The clipboard connection ended or returned an invalid frame.");
    }
}
