// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System;

namespace MouseWithoutBorders.Core;

internal static class ClipboardPacketWriter
{
    internal const int DataSize = 48;

    // The callback belongs to one captured set of connections. A failed or
    // replaced connection must never receive the tail of an earlier batch.
    internal static bool Write(byte[] payload, bool image, ID source, Func<DATA, bool> send)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(send);
        if (payload.Length == 0) return false;

        for (int offset = 0; offset < payload.Length; offset += DataSize)
        {
            byte[] bytes = new byte[Package.PACKAGE_SIZE_EX];
            Array.Copy(payload, offset, bytes, Package.PACKAGE_SIZE_EX - DataSize,
                Math.Min(DataSize, payload.Length - offset));
            DATA packet = new(bytes)
            {
                Type = image ? PackageType.ClipboardImage : PackageType.ClipboardText,
                Src = source,
                Des = ID.ALL,
            };
            if (!send(packet)) return false;
        }

        return send(new DATA { Type = PackageType.ClipboardDataEnd, Src = source, Des = ID.ALL });
    }
}
