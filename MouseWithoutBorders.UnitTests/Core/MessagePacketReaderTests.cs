// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class MessagePacketReaderTests
{
    [DataTestMethod]
    [DataRow(124)]
    [DataRow(125)]
    [DataRow(76)]
    public void ExtendedPacketEofNeverReturnsAnApparentlyCompletePacket(int typeValue)
    {
        var type = (PackageType)typeValue;
        byte[] bytes = Frame(type);
        for (int cutoff = 0; cutoff < bytes.Length; cutoff++)
        {
            using var stream = new MemoryStream(bytes[..cutoff]);
            DATA packet = SocketStuff.ReadMessagePacket(stream, out _);
            Assert.AreNotEqual(type, packet.Type, $"EOF at byte {cutoff}");
            Assert.IsTrue(packet.Type is PackageType.Invalid or PackageType.Error, $"EOF at byte {cutoff}");
        }
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(17)]
    [DataRow(32)]
    public void FragmentedReadsPreserveExtendedFrameAndNextFrame(int maximumRead)
    {
        byte[] first = Frame(PackageType.ClipboardText);
        byte[] second = Frame(PackageType.Keyboard);
        using var stream = new FragmentedStream(first.Concat(second).ToArray(), maximumRead);
        DATA packet = SocketStuff.ReadMessagePacket(stream, out _);
        Assert.AreEqual(PackageType.ClipboardText, packet.Type);
        CollectionAssert.AreEqual(first[32..], packet.Bytes[32..]);
        Assert.AreEqual(PackageType.Keyboard, SocketStuff.ReadMessagePacket(stream, out _).Type);
        Assert.AreEqual(stream.Length, stream.Position);
    }

    private static byte[] Frame(PackageType type)
    {
        var packet = new DATA { Type = type, Src = (ID)42, Des = ID.ALL };
        byte[] bytes = packet.Bytes;
        if (bytes.Length == 64)
            for (int i = 32; i < 64; i++) bytes[i] = (byte)i;
        uint magic = Encryption.MagicNumber;
        bytes[3] = (byte)(magic >> 24);
        bytes[2] = (byte)(magic >> 16);
        bytes[1] = 0;
        for (int i = 2; i < 32; i++) bytes[1] = unchecked((byte)(bytes[1] + bytes[i]));
        return bytes;
    }

    private sealed class FragmentedStream(byte[] bytes, int maximumRead) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, maximumRead));
    }
}
