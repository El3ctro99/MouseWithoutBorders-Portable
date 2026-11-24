// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#if PORTABLE_SINGLE_FILE
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders;

internal partial class FrmScreen
{
    internal const int ConnectionPowerBroadcastMessage = 0x0218;
    internal const int ConnectionPowerSettingChanged = 0x8013;
    internal static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    private DisplayPowerNotificationLease connectionPowerNotification;
    private bool connectionPowerNotificationsStopped;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (SecureDesktopAgent.IsActive || Volatile.Read(ref connectionPowerNotificationsStopped)
            || IsDisposed || Disposing || !IsHandleCreated) return;
        try
        {
            Guid setting = SessionDisplayStatus;
            IntPtr registration = RegisterPowerSettingNotification(Handle, ref setting, 0);
            if (registration == IntPtr.Zero)
            {
                Debug.WriteLine("Display power notifications unavailable: " + Marshal.GetLastWin32Error());
                return;
            }
            var lease = new DisplayPowerNotificationLease(registration, UnregisterDisplayPowerSetting);
            if (Volatile.Read(ref connectionPowerNotificationsStopped) || IsDisposed || Disposing || !IsHandleCreated)
            {
                lease.Dispose();
                return;
            }
            Interlocked.Exchange(ref connectionPowerNotification, lease)?.Dispose();
            // Cleanup can race a registration from another thread. Whichever
            // side owns the lease releases it; neither may leave it attached.
            if (Volatile.Read(ref connectionPowerNotificationsStopped)) ReleaseConnectionPowerNotification();
        }
        catch (Exception error)
        {
            // Power notifications are an extra recovery signal. Registration
            // failure must not prevent MWB starting or change its power policy.
            Debug.WriteLine("Display power registration failed: " + error.GetType().Name);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        ReleaseConnectionPowerNotification();
        base.OnHandleDestroyed(e);
    }

    internal void StopConnectionPowerNotifications()
    {
        Volatile.Write(ref connectionPowerNotificationsStopped, true);
        ReleaseConnectionPowerNotification();
    }

    private void ReleaseConnectionPowerNotification() =>
        Interlocked.Exchange(ref connectionPowerNotification, null)?.Dispose();

    // Called from the existing WndProc. No socket, input, transfer or service
    // action is performed by a display-off/dim message.
    private bool TryHandleConnectionPowerMessage(ref Message message)
    {
        if (SecureDesktopAgent.IsActive || Volatile.Read(ref connectionPowerNotificationsStopped)
            || Volatile.Read(ref connectionPowerNotification)?.IsActive != true
            || message.Msg != ConnectionPowerBroadcastMessage
            || message.WParam != (IntPtr)ConnectionPowerSettingChanged
            || !TryReadConnectionDisplayPower(message.LParam, out uint state)) return false;
        if (state == 1)
        {
            try { ConnectionRecovery.DisplayActivated(); }
            catch (Exception error) { Debug.WriteLine("Display recovery notification ignored: " + error.GetType().Name); }
        }
        message.Result = (IntPtr)1;
        return true;
    }

    internal static bool TryParseConnectionDisplayPower(ReadOnlySpan<byte> payload, out uint state)
    {
        state = 0;
        // POWERBROADCAST_SETTING has a 16-byte GUID, DWORD DataLength, then
        // DataLength bytes of data. This setting's data is exactly one DWORD.
        if (payload.Length != 24 || !IsSessionDisplayHeader(payload.Slice(0, 20))) return false;
        uint parsed = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(20, 4));
        if (parsed > 2) return false;
        state = parsed;
        return true;
    }

    private static bool IsSessionDisplayHeader(ReadOnlySpan<byte> header) => header.Length == 20
        && new Guid(header.Slice(0, 16)) == SessionDisplayStatus
        && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4)) == 4;

    internal static bool TryReadConnectionDisplayPower(IntPtr pointer, out uint state)
    {
        state = 0;
        if (pointer.ToInt64() <= 0 || pointer.ToInt64() > long.MaxValue - 20) return false;
        try
        {
            byte[] header = new byte[20];
            // ReadProcessMemory validates readability and fails normally for an
            // invalid message pointer. Direct Marshal reads could terminate the
            // process with an unmanaged access violation. Only our own process
            // pseudo-handle is used; no external process is opened or inspected.
            if (!ReadOwnMemory(pointer, header) || !IsSessionDisplayHeader(header)) return false;
            byte[] data = new byte[4];
            if (!ReadOwnMemory(IntPtr.Add(pointer, 20), data)) return false;
            Span<byte> payload = stackalloc byte[24];
            header.AsSpan().CopyTo(payload);
            data.AsSpan().CopyTo(payload.Slice(20));
            return TryParseConnectionDisplayPower(payload, out state);
        }
        catch (Exception error) when (error is ArgumentException or OverflowException or ExternalException)
        {
            return false;
        }
    }

    private static bool ReadOwnMemory(IntPtr pointer, byte[] bytes) =>
        ReadProcessMemory((IntPtr)(-1), pointer, bytes, (nuint)bytes.Length, out nuint read)
        && read == (nuint)bytes.Length;

    private static void UnregisterDisplayPowerSetting(IntPtr registration)
    {
        if (!UnregisterPowerSettingNotification(registration))
            Debug.WriteLine("Display power notification cleanup failed: " + Marshal.GetLastWin32Error());
    }

    internal sealed class DisplayPowerNotificationLease : IDisposable
    {
        private readonly Action<IntPtr> release;
        private IntPtr handle;
        internal bool IsActive => Volatile.Read(ref handle) != IntPtr.Zero;

        internal DisplayPowerNotificationLease(IntPtr handle, Action<IntPtr> release)
        {
            if (handle == IntPtr.Zero) throw new ArgumentException("A power notification handle is required.", nameof(handle));
            this.release = release ?? throw new ArgumentNullException(nameof(release));
            this.handle = handle;
        }

        public void Dispose()
        {
            IntPtr owned = Interlocked.Exchange(ref handle, IntPtr.Zero);
            if (owned == IntPtr.Zero) return;
            try { release(owned); }
            catch (Exception error) { Debug.WriteLine("Display power notification cleanup failed: " + error.GetType().Name); }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr registration);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] destination,
        nuint size, out nuint bytesRead);
}
#endif
