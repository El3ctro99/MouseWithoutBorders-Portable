// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class SecureDesktopPacketPolicyTests
{
    [TestMethod]
    public void ProtectedDesktopAcceptsInputAndConnectionMaintenance()
    {
        foreach (PackageType type in new[]
        {
            PackageType.Keyboard, PackageType.Mouse,
            PackageType.Hello, PackageType.Hi, PackageType.ByeBye,
            PackageType.Handshake, PackageType.HandshakeAck, PackageType.Awake,
            PackageType.Heartbeat, PackageType.Heartbeat_ex,
            PackageType.Heartbeat_ex_l2, PackageType.Heartbeat_ex_l3,
        })
        {
            Assert.IsTrue(SecureDesktopAgent.AllowsPacket(type), type.ToString());
        }
    }

    [TestMethod]
    public void ProtectedDesktopRejectsAllClipboardFileAndScreenshotPackets()
    {
        foreach (PackageType type in new[]
        {
            PackageType.Clipboard, PackageType.ClipboardDragDrop,
            PackageType.ClipboardDragDropEnd, PackageType.ExplorerDragDrop,
            PackageType.ClipboardCapture, PackageType.CaptureScreenCommand,
            PackageType.ClipboardDragDropOperation, PackageType.ClipboardDataEnd,
            PackageType.MachineSwitched, PackageType.ClipboardAsk,
            PackageType.ClipboardPush, PackageType.ClipboardText, PackageType.ClipboardImage,
        })
        {
            Assert.IsFalse(SecureDesktopAgent.AllowsPacket(type), type.ToString());
        }
    }

    [TestMethod]
    public void ProtectedReceiverRejectsControllerAndLayoutCommands()
    {
        Assert.IsFalse(SecureDesktopAgent.AllowsPacket(PackageType.NextMachine));
        Assert.IsFalse(SecureDesktopAgent.AllowsPacket(PackageType.HideMouse));

        // Peers cannot replace the administrator-reviewed layout or turn this
        // receive-only worker into a controller that hides the physical pointer.
        for (int value = 0x80; value <= 0xff; value++)
        {
            Assert.IsFalse(SecureDesktopAgent.AllowsPacket((PackageType)value), $"0x{value:X2}");
        }
    }

    [TestMethod]
    public void UnknownAndMalformedPacketKindsFailClosed()
    {
        int[] permitted = { 2, 3, 4, 20, 21, 51, 52, 53, 122, 123, 126, 127 };
        for (int value = 0; value <= 255; value++)
        {
            if (Array.IndexOf(permitted, value) >= 0) continue;
            Assert.IsFalse(SecureDesktopAgent.AllowsPacket((PackageType)value), $"Unexpected packet {value} was admitted.");
        }

        foreach (int value in new[] { int.MinValue, -1, 256, 0x180, 0x10080, int.MaxValue })
        {
            Assert.IsFalse(SecureDesktopAgent.AllowsPacket((PackageType)value), $"Malformed packet {value} was admitted.");
        }
    }
}
