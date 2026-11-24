// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class ClipboardDiagnosticsTests
{
    [TestMethod]
    public void SnapshotContainsNumericStagesAndCannotMutateCounters()
    {
        ClipboardDiagnostics.Record("TestApply", 42, 100);
        var first = ClipboardDiagnostics.Snapshot();
        long count = first["TestApply.Count"];
        Assert.AreEqual(42L, first["TestApply.Sequence"]);
        Assert.AreEqual(100L, first["TestApply.Bytes"]);
        first["TestApply.Count"] = -1;
        Assert.AreEqual(count, ClipboardDiagnostics.Snapshot()["TestApply.Count"]);
        ClipboardDiagnostics.Failure("TestFailure", unchecked((int)0x800401D0));
        Assert.AreEqual((long)unchecked((int)0x800401D0), ClipboardDiagnostics.Snapshot()["TestFailure.HResult"]);
    }
}
