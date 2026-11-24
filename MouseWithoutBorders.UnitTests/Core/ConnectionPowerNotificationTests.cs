// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class ConnectionPowerNotificationTests
{
    [DataTestMethod]
    [DataRow(0u)]
    [DataRow(1u)]
    [DataRow(2u)]
    public void SessionDisplayPayloadAcceptsOnlyTheDocumentedDwordValues(uint state)
    {
        Assert.IsTrue(MouseWithoutBorders.FrmScreen.TryParseConnectionDisplayPower(Payload(state), out uint parsed));
        Assert.AreEqual(state, parsed);
    }

    [TestMethod]
    public void InvalidLengthGuidDataSizeAndStateAreIgnored()
    {
        foreach (int length in new[] { 0, 16, 19, 20, 21, 23, 25, 32 })
            Assert.IsFalse(MouseWithoutBorders.FrmScreen.TryParseConnectionDisplayPower(new byte[length], out _));
        byte[] foreignGuid = Payload(1);
        Guid.NewGuid().TryWriteBytes(foreignGuid);
        Assert.IsFalse(MouseWithoutBorders.FrmScreen.TryParseConnectionDisplayPower(foreignGuid, out _));
        foreach (uint length in new[] { 0u, 1u, 3u, 5u, 4096u, uint.MaxValue })
        {
            byte[] wrongSize = Payload(1);
            BinaryPrimitives.WriteUInt32LittleEndian(wrongSize.AsSpan(16), length);
            Assert.IsFalse(MouseWithoutBorders.FrmScreen.TryParseConnectionDisplayPower(wrongSize, out _));
        }
        foreach (uint state in new[] { 3u, uint.MaxValue })
            Assert.IsFalse(MouseWithoutBorders.FrmScreen.TryParseConnectionDisplayPower(Payload(state), out _));
    }

    [TestMethod]
    public void NativeReaderCopiesOnlyTheCurrentProcessesValidPayload()
    {
        IntPtr address = Marshal.AllocHGlobal(24);
        try
        {
            byte[] bytes = Payload(1);
            Marshal.Copy(bytes, 0, address, bytes.Length);
            Assert.IsTrue(MouseWithoutBorders.FrmScreen.TryReadConnectionDisplayPower(address, out uint state));
            Assert.AreEqual(1u, state);
            bytes = Payload(4);
            Marshal.Copy(bytes, 0, address, bytes.Length);
            Assert.IsFalse(MouseWithoutBorders.FrmScreen.TryReadConnectionDisplayPower(address, out _));
        }
        finally { Marshal.FreeHGlobal(address); }
    }

    [TestMethod]
    public void NativeReaderSafelyRejectsNullAndUnreadableAddresses()
    {
        foreach (var address in new[] { IntPtr.Zero, (IntPtr)1, (IntPtr)(-1), unchecked((IntPtr)long.MaxValue) })
            Assert.IsFalse(MouseWithoutBorders.FrmScreen.TryReadConnectionDisplayPower(address, out _));
    }

    [TestMethod]
    public void RegistrationLeaseReleasesItsExactHandleOnlyOnce()
    {
        int releases = 0;
        IntPtr expected = (IntPtr)123;
        var lease = new MouseWithoutBorders.FrmScreen.DisplayPowerNotificationLease(expected, actual =>
        {
            Assert.AreEqual(expected, actual);
            Interlocked.Increment(ref releases);
        });
        Assert.IsTrue(lease.IsActive);
        Parallel.Invoke(lease.Dispose, lease.Dispose, lease.Dispose, lease.Dispose);
        lease.Dispose();
        Assert.AreEqual(1, releases);
        Assert.IsFalse(lease.IsActive);
    }

    [TestMethod]
    public void CleanupFailureIsNonfatalAndDoesNotRetryAnAlreadyReleasedLease()
    {
        int releases = 0;
        var lease = new MouseWithoutBorders.FrmScreen.DisplayPowerNotificationLease((IntPtr)123,
            _ => { releases++; throw new InvalidOperationException("The window already closed."); });
        lease.Dispose();
        lease.Dispose();
        Assert.AreEqual(1, releases);
        Assert.IsFalse(lease.IsActive);
        Assert.ThrowsException<ArgumentException>(() => new MouseWithoutBorders.FrmScreen.DisplayPowerNotificationLease(IntPtr.Zero, _ => { }));
    }

    private static byte[] Payload(uint state)
    {
        byte[] payload = new byte[24];
        MouseWithoutBorders.FrmScreen.SessionDisplayStatus.TryWriteBytes(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), state);
        return payload;
    }
}
