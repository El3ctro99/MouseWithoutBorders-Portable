// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Generic;

namespace MouseWithoutBorders.Core;

// Fixed stage names and numeric metadata only. No text, images, file paths,
// content hashes, security keys or source application identifiers are retained.
internal static class ClipboardDiagnostics
{
    private static readonly ConcurrentDictionary<string, long> Values = new();

    internal static void Record(string stage, uint sequence = 0, int bytes = 0)
    {
        Values.AddOrUpdate(stage + ".Count", 1, (_, count) => count + 1);
        Values[stage + ".Sequence"] = sequence;
        Values[stage + ".Bytes"] = bytes;
    }

    internal static void Failure(string stage, int hresult)
    {
        Record(stage);
        Values[stage + ".HResult"] = hresult;
    }

    internal static Dictionary<string, long> Snapshot() => new(Values);
}
