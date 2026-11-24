// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class InputInjectionRecoveryTests
{
    [TestMethod]
    public void StartupDoesNotInjectSyntheticMouseMovement()
    {
        MethodInfo init = typeof(InitAndCleanup).GetMethod("Init", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.IsNotNull(init);

        // Inspect compiled calls without executing initialization or real input.
        Assert.IsFalse(CalledMethods(init).Any(method => method.DeclaringType == typeof(InputSimulation)
            && method.Name == nameof(InputSimulation.MoveMouseRelative)),
            "Startup must not probe optional injection by injecting native mouse movement.");
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void WinRtComFailureDeliversSameMouseInputNatively(bool is64Bit, bool absolute)
    {
        var recovery = new InputInjectionRecovery();
        var backend = new FakeBackend { WinRtFailure = new COMException("private diagnostic", unchecked((int)0x8007007A)), NativeResult = 7 };
        NativeMethods.INPUT input = MouseMove();
        if (!absolute) input.mi.dwFlags = (int)NativeMethods.MOUSEEVENTF.MOVE;

        Assert.AreEqual(7u, recovery.SendInput(input, is64Bit, backend));

        Assert.AreEqual(1, backend.WinRtCalls);
        Assert.AreEqual(1, backend.Failures.Count);
        Assert.AreEqual(unchecked((int)0x8007007A), backend.Failures[0]);
        AssertMouseEqual(input.mi, backend.LastWinRtInput);
        AssertNativeMouse(input, is64Bit, backend);
    }

    [TestMethod]
    public void WinRtFailureDisablesFurtherAttemptsAndLogsOnlyOnce()
    {
        var recovery = new InputInjectionRecovery();
        var backend = new FakeBackend { WinRtFailure = new COMException("private diagnostic", unchecked((int)0x8007007A)) };

        recovery.SendInput(MouseMove(), true, backend);
        recovery.SendInput(MouseMove(), true, backend);
        recovery.SendInput(MouseMove(), true, backend);

        // The fake deliberately leaves its availability flag true. The recovery
        // latch must protect later events even if another initializer re-enables it.
        Assert.AreEqual(1, backend.WinRtCalls);
        Assert.AreEqual(1, backend.Failures.Count);
        Assert.AreEqual(3, backend.Native64Calls);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingInjectorFallsBackAndDisablesFurtherWinRtAttempts(bool is64Bit)
    {
        var recovery = new InputInjectionRecovery();
        var backend = new FakeBackend { HasInjector = false };
        NativeMethods.INPUT input = MouseMove();

        recovery.SendInput(input, is64Bit, backend);
        backend.HasInjector = true;
        recovery.SendInput(input, is64Bit, backend);

        Assert.AreEqual(1, backend.WinRtCalls);
        Assert.AreEqual(1, backend.Failures.Count);
        Assert.IsNull(backend.Failures[0]);
        Assert.AreEqual(2, backend.NativeCalls + backend.Native64Calls);
        AssertMouseEqual(input.mi, is64Bit ? backend.LastNative64Input.mi : backend.LastNativeInput.mi);
    }

    [TestMethod]
    public void SuccessfulWinRtInjectionDoesNotDuplicateInputNatively()
    {
        var backend = new FakeBackend();
        NativeMethods.INPUT input = MouseMove();

        Assert.AreEqual(0u, new InputInjectionRecovery().SendInput(input, true, backend));

        Assert.AreEqual(1, backend.WinRtCalls);
        Assert.AreEqual(0, backend.NativeCalls + backend.Native64Calls);
        Assert.AreEqual(0, backend.Failures.Count);
        AssertMouseEqual(input.mi, backend.LastWinRtInput);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DisabledWinRtRetainsNativeReturnValueAndMouseFields(bool is64Bit)
    {
        var backend = new FakeBackend { WinRtAvailable = false, NativeResult = 0 };
        NativeMethods.INPUT input = MouseMove();

        Assert.AreEqual(0u, new InputInjectionRecovery().SendInput(input, is64Bit, backend));

        Assert.AreEqual(0, backend.WinRtCalls);
        Assert.AreEqual(0, backend.Failures.Count);
        AssertNativeMouse(input, is64Bit, backend);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void KeyboardInputUsesNativeBackendAndPreservesFields(bool is64Bit)
    {
        var backend = new FakeBackend { NativeResult = 3 };
        NativeMethods.INPUT input = default;
        input.type = 1;
        input.ki.wVk = 0x41;
        input.ki.wScan = 0x1E;
        input.ki.dwFlags = 2;
        input.ki.time = 1234;
        input.ki.dwExtraInfo = new IntPtr(5678);

        Assert.AreEqual(3u, new InputInjectionRecovery().SendInput(input, is64Bit, backend));

        Assert.AreEqual(0, backend.WinRtCalls);
        Assert.AreEqual(is64Bit ? 0 : 1, backend.NativeCalls);
        Assert.AreEqual(is64Bit ? 1 : 0, backend.Native64Calls);
        NativeMethods.KEYBDINPUT actual = is64Bit ? backend.LastNative64Input.ki : backend.LastNativeInput.ki;
        Assert.AreEqual(input.ki.wVk, actual.wVk);
        Assert.AreEqual(input.ki.wScan, actual.wScan);
        Assert.AreEqual(input.ki.dwFlags, actual.dwFlags);
        Assert.AreEqual(input.ki.time, actual.time);
        Assert.AreEqual(input.ki.dwExtraInfo, actual.dwExtraInfo);
    }

    [TestMethod]
    public void MouseButtonsUseNativeBackendWithoutDisablingWinRt()
    {
        var backend = new FakeBackend();
        NativeMethods.INPUT input = MouseMove();
        input.mi.dwFlags = (int)NativeMethods.MOUSEEVENTF.LEFTDOWN;

        new InputInjectionRecovery().SendInput(input, true, backend);

        Assert.AreEqual(0, backend.WinRtCalls);
        Assert.AreEqual(0, backend.Failures.Count);
        AssertNativeMouse(input, true, backend);
    }

    [TestMethod]
    public void UnexpectedWinRtExceptionsAreNotSilentlySwallowed()
    {
        var backend = new FakeBackend { WinRtFailure = new InvalidOperationException("unexpected") };

        Assert.ThrowsException<InvalidOperationException>(() => new InputInjectionRecovery().SendInput(MouseMove(), true, backend));

        Assert.AreEqual(0, backend.NativeCalls + backend.Native64Calls);
        Assert.AreEqual(0, backend.Failures.Count);
    }

    private static NativeMethods.INPUT MouseMove()
    {
        NativeMethods.INPUT input = default;
        input.type = 0;
        input.mi.dx = 12345;
        input.mi.dy = -23456;
        input.mi.mouseData = 120;
        input.mi.dwFlags = (int)(NativeMethods.MOUSEEVENTF.MOVE | NativeMethods.MOUSEEVENTF.ABSOLUTE);
        input.mi.time = 3456;
        input.mi.dwExtraInfo = new IntPtr(4567);
        return input;
    }

    private static IEnumerable<MethodBase> CalledMethods(MethodInfo method)
    {
        var opCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => opCode.Value);
        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
        for (int offset = 0; offset < il.Length;)
        {
            short value = il[offset++];
            if (value == 0xFE) value = unchecked((short)(0xFE00 | il[offset++]));
            OpCode opCode = opCodes[value];
            if (opCode.OperandType == OperandType.InlineMethod)
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(il, offset))!;
            offset += opCode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
                    or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
                    or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, offset)),
                _ => throw new InvalidOperationException("Unsupported IL operand: " + opCode.OperandType),
            };
        }
    }

    private static void AssertNativeMouse(NativeMethods.INPUT input, bool is64Bit, FakeBackend backend)
    {
        Assert.AreEqual(is64Bit ? 0 : 1, backend.NativeCalls);
        Assert.AreEqual(is64Bit ? 1 : 0, backend.Native64Calls);
        Assert.AreEqual(input.type, is64Bit ? backend.LastNative64Input.type : backend.LastNativeInput.type);
        AssertMouseEqual(input.mi, is64Bit ? backend.LastNative64Input.mi : backend.LastNativeInput.mi);
    }

    private static void AssertMouseEqual(NativeMethods.MOUSEINPUT expected, NativeMethods.MOUSEINPUT actual)
    {
        Assert.AreEqual(expected.dx, actual.dx);
        Assert.AreEqual(expected.dy, actual.dy);
        Assert.AreEqual(expected.mouseData, actual.mouseData);
        Assert.AreEqual(expected.dwFlags, actual.dwFlags);
        Assert.AreEqual(expected.time, actual.time);
        Assert.AreEqual(expected.dwExtraInfo, actual.dwExtraInfo);
    }

    private sealed class FakeBackend : IInputInjectionBackend
    {
        public bool WinRtAvailable { get; set; } = true;
        internal bool HasInjector { get; set; } = true;
        internal Exception? WinRtFailure { get; set; }
        internal uint NativeResult { get; set; } = 1;
        internal int WinRtCalls { get; private set; }
        internal int NativeCalls { get; private set; }
        internal int Native64Calls { get; private set; }
        internal List<int?> Failures { get; } = new();
        internal NativeMethods.MOUSEINPUT LastWinRtInput { get; private set; }
        internal NativeMethods.INPUT LastNativeInput { get; private set; }
        internal NativeMethods.INPUT64 LastNative64Input { get; private set; }

        public bool TryInjectMouse(NativeMethods.MOUSEINPUT input)
        {
            WinRtCalls++;
            LastWinRtInput = input;
            if (WinRtFailure != null) throw WinRtFailure;
            return HasInjector;
        }

        public void DisableWinRt(int? failureHResult) => Failures.Add(failureHResult);

        public uint SendNative(NativeMethods.INPUT input)
        {
            NativeCalls++;
            LastNativeInput = input;
            return NativeResult;
        }

        public uint SendNative64(NativeMethods.INPUT64 input)
        {
            Native64Calls++;
            LastNative64Input = input;
            return NativeResult;
        }
    }
}
