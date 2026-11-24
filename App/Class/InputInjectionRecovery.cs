// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System.Runtime.InteropServices;
using System.Threading;

namespace MouseWithoutBorders.Class;

internal interface IInputInjectionBackend
{
    bool WinRtAvailable { get; }

    bool TryInjectMouse(NativeMethods.MOUSEINPUT input);

    void DisableWinRt(int? failureHResult);

    uint SendNative(NativeMethods.INPUT input);

    uint SendNative64(NativeMethods.INPUT64 input);
}

/// <summary>Retains native input delivery when optional WinRT mouse injection fails.</summary>
internal sealed class InputInjectionRecovery
{
    private int winRtDisabled;

    internal uint SendInput(NativeMethods.INPUT input, bool is64Bit, IInputInjectionBackend backend)
    {
        if (input.type == 0 && (input.mi.dwFlags & (int)NativeMethods.MOUSEEVENTF.MOVE) != 0
            && Volatile.Read(ref winRtDisabled) == 0 && backend.WinRtAvailable)
        {
            try
            {
                if (backend.TryInjectMouse(input.mi)) return 0;
                DisableWinRt(backend, null);
            }
            catch (COMException error)
            {
                // Retry this same event natively, and keep later events off the
                // failed WinRT object. Only the HRESULT crosses into the log.
                DisableWinRt(backend, error.HResult);
            }
        }

        if (!is64Bit) return backend.SendNative(input);

        NativeMethods.INPUT64 input64 = default;
        input64.type = input.type;
        if (input.type == 1)
        {
            input64.ki.wVk = input.ki.wVk;
            input64.ki.wScan = input.ki.wScan;
            input64.ki.dwFlags = input.ki.dwFlags;
            input64.ki.time = input.ki.time;
            input64.ki.dwExtraInfo = input.ki.dwExtraInfo;
        }
        else
        {
            input64.mi = input.mi;
        }

        return backend.SendNative64(input64);
    }

    private void DisableWinRt(IInputInjectionBackend backend, int? failureHResult)
    {
        if (Interlocked.Exchange(ref winRtDisabled, 1) == 0)
            backend.DisableWinRt(failureHResult);
    }
}
