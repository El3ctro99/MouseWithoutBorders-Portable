// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class ClipboardSnapshotRpcTests
{
    [TestMethod]
    public async Task ActualSettingsEndpointExposesNumericClipboardStages()
    {
        var targetType = typeof(MouseWithoutBorders.Class.Program).GetNestedType("SettingsSyncHelper",
            System.Reflection.BindingFlags.NonPublic)!;
        var target = Activator.CreateInstance(targetType, nonPublic: true)!;
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        using (serverStream)
        using (clientStream)
        using (var server = JsonRpc.Attach(serverStream, target))
        using (var client = new JsonRpc(clientStream))
        {
            ClipboardDiagnostics.Record("TestSettingsEndpoint", 42, 10);
            client.StartListening();
            var result = await client.InvokeAsync<Dictionary<string, long>>("RequestClipboardDiagnosticsAsync")
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(42L, result["TestSettingsEndpoint.Sequence"]);
            Assert.AreEqual(10L, result["TestSettingsEndpoint.Bytes"]);
        }
    }

    [TestMethod]
    public async Task RealHelperProxyCarriesSequenceAndRetryAcknowledgement()
    {
        var (serverStream, clientStream) = FullDuplexStream.CreatePair();
        using (serverStream)
        using (clientStream)
        {
            var target = new SnapshotTarget();
            using var server = JsonRpc.Attach(serverStream, target);
            IClipboardHelper client = JsonRpc.Attach<IClipboardHelper>(clientStream);
            Assert.IsFalse(await client.SendClipboardSnapshot("TXTfixture", false, 42).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsTrue(await client.SendClipboardSnapshot("TXTfixture", false, 42).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(42u, target.Sequence);
            Assert.AreEqual("TXTfixture", target.Value.GetString());
            Assert.IsFalse(target.IsFile);
        }
    }

    public sealed class SnapshotTarget : IClipboardHelper
    {
        private int calls;
        public uint Sequence;
        public ByteArrayOrString Value;
        public bool IsFile;
        public Task<bool> SendClipboardSnapshot(ByteArrayOrString data, bool isFilePath, uint sequence)
        {
            Sequence = sequence;
            Value = data;
            IsFile = isFilePath;
            return Task.FromResult(++calls > 1);
        }
        public void SendLog(string value) { }
        public void SendDragFile(string value) { }
        public void SendDragFiles(string[] value) { }
        public void SendClipboardData(ByteArrayOrString data, bool isFilePath) { }
    }
}
