// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#if PORTABLE_SINGLE_FILE
#nullable enable
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MouseWithoutBorders.Core;

/// <summary>
/// Keeps ordinary Explorer drag/drop and clipboard handling at Medium integrity
/// while the main app may run elevated. Requires the same interactive account;
/// it never borrows another account, changes UAC policy or launches arbitrary args.
/// </summary>
internal static class PortableClipboardHelperLaunch
{
    internal const int MediumIntegrity = 0x2000;
    internal const int HighIntegrity = 0x3000;
    private const int SystemIntegrity = 0x4000;

    internal sealed record LaunchIdentity(string UserSid, int SessionId, int Integrity, bool Elevated, bool UiAccess);
    internal sealed record LaunchContext(LaunchIdentity Identity, string WindowStation, string Desktop,
        bool DesktopActive, bool SecureAgent);
    internal sealed record HelperLaunchRequest(string ApplicationName, string CommandLine, string WorkingDirectory);

    internal interface IShellParent : IDisposable
    {
        LaunchIdentity Identity { get; }
        string ImagePath { get; }
        bool IsCurrentShell { get; }
    }
    internal interface IRunningChild : IDisposable
    {
        int Id { get; }
        LaunchIdentity Identity { get; }
        string ImagePath { get; }
    }
    internal interface ISuspendedChild : IRunningChild
    {
        void Resume();
        void TerminateIfSuspended();
    }
    internal interface ILaunchSystem
    {
        LaunchContext Context { get; }
        IShellParent OpenShellParent();
        ISuspendedChild CreateSuspended(HelperLaunchRequest request, IShellParent? parent);
        IRunningChild LaunchThroughExplorer(HelperLaunchRequest request, IShellParent parent);
    }

    internal static int Start()
    {
        try { return StartCore(new WindowsLaunchSystem()); }
        catch (Win32Exception error)
        {
            // Preserve only a fixed stage and native error number; never leak an environment,
            // command line, clipboard value, pairing key or credential in logs.
            string? stage = NativeFailureStage(error);
            var failure = new InvalidOperationException("The clipboard helper could not start with ordinary desktop permissions. Windows error "
                + error.NativeErrorCode + (stage == null ? "." : " (" + stage + ")."));
            if (stage != null) failure.Data["ClipboardHelperLaunchStage"] = stage;
            throw failure;
        }
        catch (COMException error)
        {
            var failure = new InvalidOperationException("Explorer could not start the ordinary clipboard helper. HRESULT " + error.HResult + ".");
            failure.Data["ClipboardHelperLaunchStage"] = "ExplorerAutomation";
            throw failure;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The clipboard helper could not start with ordinary desktop permissions for this Windows account.");
        }
    }

    internal static int StartCore(ILaunchSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        LaunchContext context = system.Context;
        if (!IsValidCaller(context)) throw new SecurityException("Clipboard helper launch requires the active ordinary desktop.");
        HelperLaunchRequest request = CreateCurrentRequest();
        using IShellParent? parent = context.Identity.Elevated ? system.OpenShellParent() : null;
        if (context.Identity.Elevated && parent == null)
            throw new SecurityException("The ordinary Explorer shell is unavailable.");
        if (parent != null && !IsValidShell(context.Identity, parent.Identity, parent.ImagePath, parent.IsCurrentShell))
            throw new SecurityException("The ordinary Explorer shell does not match this Windows account and session.");

        if (parent != null)
        {
            // Explorer's documented automation launch starts a running ordinary
            // process. Verify the resulting helper; never claim it was suspended
            // or terminate an already running process on validation failure.
            using IRunningChild running = system.LaunchThroughExplorer(request, parent);
            if (running.Id <= 0 || !IsValidChild(context.Identity, running.Identity)
                || !SamePath(running.ImagePath, request.ApplicationName) || !parent.IsCurrentShell)
                throw new SecurityException("The running clipboard helper has an unexpected identity or image.");
            return running.Id;
        }
        using ISuspendedChild child = system.CreateSuspended(request, parent);
        bool resumed = false;
        try
        {
            if (child.Id <= 0 || !IsValidChild(context.Identity, child.Identity)
                || !SamePath(child.ImagePath, request.ApplicationName))
                throw new SecurityException("The suspended clipboard helper has an unexpected identity or image.");
            if (parent != null && !parent.IsCurrentShell)
                throw new SecurityException("The Explorer shell changed during clipboard helper startup.");
            child.Resume();
            resumed = true;
            return child.Id;
        }
        finally
        {
            // The process is still suspended and has executed no application
            // code on failure. Never enumerate, terminate or modify other apps.
            if (!resumed) child.TerminateIfSuspended();
        }
    }

    internal static bool IsValidCaller(LaunchContext context)
    {
        LaunchIdentity caller = context.Identity;
        return !context.SecureAgent && IsInteractiveUser(caller.UserSid) && caller.SessionId > 0
            && context.DesktopActive && !caller.UiAccess
            && string.Equals(context.WindowStation, "WinSta0", StringComparison.OrdinalIgnoreCase)
            && string.Equals(context.Desktop, "Default", StringComparison.OrdinalIgnoreCase)
            && (caller.Elevated ? caller.Integrity >= HighIntegrity && caller.Integrity < SystemIntegrity
                : caller.Integrity == MediumIntegrity);
    }

    internal static bool IsValidShell(LaunchIdentity caller, LaunchIdentity shell, string image, bool currentShell) =>
        currentShell && IsValidChild(caller, shell) && SamePath(image,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));

    internal static bool IsValidChild(LaunchIdentity caller, LaunchIdentity child) =>
        IsInteractiveUser(child.UserSid) && string.Equals(caller.UserSid, child.UserSid, StringComparison.Ordinal)
        && caller.SessionId == child.SessionId && child.Integrity == MediumIntegrity
        && !child.Elevated && !child.UiAccess;

    private static bool IsInteractiveUser(string sid) => !string.IsNullOrWhiteSpace(sid)
        && sid is not ("S-1-5-18" or "S-1-5-19" or "S-1-5-20")
        && !sid.StartsWith("S-1-5-80-", StringComparison.Ordinal);

    private static bool SamePath(string first, string second) => Path.IsPathFullyQualified(first)
        && Path.IsPathFullyQualified(second) && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    internal static HelperLaunchRequest CreateCurrentRequest()
    {
        string executable = PortableApplication.CurrentExecutablePath;
        if (!Path.IsPathFullyQualified(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("The clipboard helper requires the current executable.");
        string command = QuoteArgument(executable) + " " + PortableApplication.ClipboardHelperArgument;
        if (command.Length >= 32767) throw new SecurityException("The clipboard helper path is too long.");
        return new(executable, command, Path.GetDirectoryName(executable)!);
    }

    // Quoting only, not an execution entry point. Production calls it only for
    // the already selected current executable; the sole argument is fixed above.
    internal static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Contains('\0')) throw new ArgumentException("A command argument contains a null character.", nameof(argument));
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char value in argument)
        {
            if (value == '\\') { slashes++; continue; }
            result.Append('\\', value == '"' ? (slashes * 2) + 1 : slashes);
            result.Append(value); slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    // Read-only fixture seam: an isolated host can report its real token without
    // entering MWB's preferences, input hooks, network or helper UI.
    internal static LaunchIdentity ReadCurrentIdentity()
    {
        using var token = OpenToken(Native.GetCurrentProcess(), duplicate: false);
        return ReadIdentity(token);
    }

    private sealed class WindowsLaunchSystem : ILaunchSystem
    {
        public LaunchContext Context => new(ReadCurrentIdentity(), ObjectName(Native.GetProcessWindowStation()),
            ObjectName(Native.GetThreadDesktop(Native.GetCurrentThreadId())),
            IsDefaultInputDesktop(), SecureDesktopAgent.IsActive);

        public IShellParent OpenShellParent()
        {
            IntPtr window = Native.GetShellWindow();
            if (window == IntPtr.Zero || Native.GetWindowThreadProcessId(window, out uint pid) == 0 || pid == 0)
                throw new SecurityException("An ordinary Explorer shell is unavailable.");
            var process = new KernelHandle(Native.OpenProcess(0x1000, false, pid)); // Query limited information only.
            if (process.IsInvalid) { var error = NativeError("OpenShellProcess"); process.Dispose(); throw error; }
            try { return new WindowsShellParent(process, pid); }
            catch { process.Dispose(); throw; }
        }

        public ISuspendedChild CreateSuspended(HelperLaunchRequest request, IShellParent? parent)
        {
            if (parent != null || ReadCurrentIdentity().Elevated)
                throw new SecurityException("Native clipboard helper creation requires an ordinary caller.");
            using var token = OpenToken(Native.GetCurrentProcess(), duplicate: true);
            if (!Native.CreateEnvironmentBlock(out IntPtr environmentPointer, token, false)) throw NativeError("CreateEnvironment");
            using var environment = new EnvironmentHandle(environmentPointer);
            using var desktop = NativeMemory.FromString(@"winsta0\default");
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = desktop.DangerousGetHandle(), Flags = 1, ShowWindow = 0,
            };
            uint flags = 0x00000004 | 0x00000400; // Suspended + Unicode environment.
            ProcessInformation info = default;
            KernelHandle? process = null;
            KernelHandle? thread = null;
            bool created = false;
            try
            {
                bool succeeded = Native.CreateProcessW(request.ApplicationName, new StringBuilder(request.CommandLine), IntPtr.Zero, IntPtr.Zero,
                    false, flags, environment.DangerousGetHandle(), request.WorkingDirectory, ref startup, out info);
                if (!succeeded) throw NativeError("CreateOrdinaryProcess");
                created = true;
                process = new KernelHandle(info.Process); info.Process = IntPtr.Zero;
                thread = new KernelHandle(info.Thread); info.Thread = IntPtr.Zero;
                var child = new WindowsChild(process, thread, checked((int)info.ProcessId));
                process = null; thread = null; // Ownership transferred only after successful construction.
                return child;
            }
            finally
            {
                if (process != null && !process.IsInvalid) _ = Native.TerminateProcess(process, 1);
                process?.Dispose(); thread?.Dispose();
                if (created && info.Process != IntPtr.Zero) { _ = Native.TerminateProcessRaw(info.Process, 1); _ = Native.CloseHandle(info.Process); }
                if (created && info.Thread != IntPtr.Zero) _ = Native.CloseHandle(info.Thread);
            }
        }

        public IRunningChild LaunchThroughExplorer(HelperLaunchRequest request, IShellParent parent)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new SecurityException("Explorer clipboard helper launch requires the existing STA desktop thread.");
            if (parent is not WindowsShellParent shell)
                throw new SecurityException("Unexpected shell parent implementation.");
            LaunchIdentity caller = ReadCurrentIdentity();
            if (!caller.Elevated || !IsValidShell(caller, shell.Identity, shell.ImagePath, shell.IsCurrentShell))
                throw new SecurityException("The ordinary Explorer shell changed before clipboard helper startup.");
            long launchedAt = DateTime.UtcNow.ToFileTimeUtc();
            ExecuteFromExplorer(request, shell);
            var timer = Stopwatch.StartNew();
            do
            {
                IRunningChild? child = FindLaunchedHelper(request, caller, launchedAt);
                if (child != null) return child;
                Thread.Sleep(25);
            }
            while (timer.ElapsedMilliseconds < 5000);
            throw new SecurityException("The ordinary clipboard helper did not expose a verified window in time.");
        }
    }

    private sealed class WindowsShellParent : IShellParent
    {
        internal readonly KernelHandle Process;
        private readonly uint id;
        internal WindowsShellParent(KernelHandle process, uint id) { Process = process; this.id = id; }
        public LaunchIdentity Identity { get { using var token = OpenToken(Process.DangerousGetHandle(), duplicate: false, stage: "OpenShellToken"); return ReadIdentity(token); } }
        public string ImagePath => ImageName(Process);
        public bool IsCurrentShell => Native.GetShellWindow() is var window && window != IntPtr.Zero
            && Native.GetWindowThreadProcessId(window, out uint current) != 0 && current == id
            && Native.GetExitCodeProcess(Process, out uint exit) && exit == 259;
        public void Dispose() => Process.Dispose();

        internal bool OwnsWindow(IntPtr window) => window != IntPtr.Zero
            && Native.GetWindowThreadProcessId(window, out uint owner) != 0 && owner == id && IsCurrentShell;
    }

    // https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643
    // Get the automation object from the desktop view hosted by the verified
    // Explorer, rather than creating Shell.Application in the elevated process.
    private static void ExecuteFromExplorer(HelperLaunchRequest request, WindowsShellParent shell)
    {
        var objects = new List<object>();
        try
        {
            object windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true)!)!;
            objects.Add(windows);
            object location = 0; // CSIDL_DESKTOP.
            object root = null!; // VT_EMPTY, not a missing optional argument.
            int desktopWindow;
            object dispatch = ((dynamic)windows).FindWindowSW(ref location, ref root, 8, out desktopWindow, 1);
            objects.Add(dispatch);
            Guid service = new("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser.
            Guid browserId = typeof(IShellBrowser).GUID;
            Marshal.ThrowExceptionForHR(((IComServiceProvider)dispatch).QueryService(ref service, ref browserId, out object browserObject));
            objects.Add(browserObject);
            var browser = (IShellBrowser)browserObject;
            Marshal.ThrowExceptionForHR(browser.GetWindow(out IntPtr browserWindow));
            if (!shell.OwnsWindow(browserWindow)) throw new SecurityException("The desktop automation host does not match Explorer.");
            Marshal.ThrowExceptionForHR(browser.QueryActiveShellView(out IShellView view));
            objects.Add(view);
            Marshal.ThrowExceptionForHR(view.GetWindow(out IntPtr viewWindow));
            if (!shell.OwnsWindow(viewWindow)) throw new SecurityException("The desktop automation view does not match Explorer.");
            Guid dispatchId = new("00020400-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(view.GetItemObject(0, ref dispatchId, out object folder)); // SVGIO_BACKGROUND.
            objects.Add(folder);
            object application = ((dynamic)folder).Application;
            objects.Add(application);
            if (!shell.IsCurrentShell) throw new SecurityException("The Explorer shell changed before automation launch.");
            // The executable, argument, working directory, verb and show state
            // are selected here; no arbitrary target or parameters are accepted.
            ((dynamic)application).ShellExecute(request.ApplicationName, PortableApplication.ClipboardHelperArgument,
                request.WorkingDirectory, "open", 0);
        }
        catch (SecurityException) { throw; }
        catch (COMException error)
        {
            error.Data["ClipboardHelperLaunchStage"] = "ExplorerAutomation";
            throw;
        }
        catch (Exception)
        {
            throw new SecurityException("Explorer could not launch the ordinary clipboard helper.");
        }
        finally
        {
            var released = new HashSet<object>(ReferenceEqualityComparer.Instance);
            for (int i = objects.Count - 1; i >= 0; i--)
                if (objects[i] != null && released.Add(objects[i]) && Marshal.IsComObject(objects[i]))
                    _ = Marshal.ReleaseComObject(objects[i]);
        }
    }

    internal static bool IsNewHelper(LaunchIdentity caller, LaunchIdentity child, string image,
        string ownImage, long createdAt, long launchedAt) => createdAt >= launchedAt
            && IsValidChild(caller, child) && SamePath(image, ownImage);

    private static IRunningChild? FindLaunchedHelper(HelperLaunchRequest request, LaunchIdentity caller, long launchedAt)
    {
        var children = new List<WindowsRunningChild>();
        var seen = new HashSet<uint>();
        try
        {
            Native.EnumWindowsCallback callback = (window, parameter) =>
            {
                var text = new StringBuilder(256);
                if (Native.GetWindowTextW(window, text, text.Capacity) == 0
                    || !string.Equals(text.ToString(), Helper.HELPER_FORM_TEXT, StringComparison.Ordinal)
                    || Native.GetWindowThreadProcessId(window, out uint pid) == 0 || pid == 0 || !seen.Add(pid)) return true;
                var process = new KernelHandle(Native.OpenProcess(0x1000, false, pid));
                if (process.IsInvalid) { process.Dispose(); return true; }
                bool retained = false;
                try
                {
                    string image = ImageName(process);
                    if (!SamePath(image, request.ApplicationName)) return true;
                    if (!Native.GetProcessTimes(process, out long createdAt, out _, out _, out _)) throw NativeError("QueryProcessTimes");
                    using var token = OpenToken(process.DangerousGetHandle(), false, stage: "QueryChildToken");
                    LaunchIdentity identity = ReadIdentity(token, "QueryChildToken");
                    if (IsNewHelper(caller, identity, image, request.ApplicationName, createdAt, launchedAt))
                    {
                        children.Add(new WindowsRunningChild(process, checked((int)pid)));
                        retained = true;
                    }
                }
                catch (Win32Exception) { /* Unrelated or disappearing windows are not launch evidence. */ }
                finally { if (!retained) process.Dispose(); }
                return true;
            };
            if (!Native.EnumWindows(callback, IntPtr.Zero)) throw NativeError("DiscoverHelperProcess");
            if (children.Count > 1) throw new SecurityException("More than one new ordinary clipboard helper was found.");
            if (children.Count == 0) return null;
            WindowsRunningChild result = children[0];
            children.Clear(); // Transfer exactly one query-only handle.
            return result;
        }
        finally { foreach (WindowsRunningChild child in children) child.Dispose(); }
    }

    private sealed class WindowsRunningChild(KernelHandle process, int id) : IRunningChild
    {
        public int Id => id;
        public LaunchIdentity Identity { get { using var token = OpenToken(process.DangerousGetHandle(), false, stage: "QueryChildToken"); return ReadIdentity(token, "QueryChildToken"); } }
        public string ImagePath => ImageName(process);
        public void Dispose() => process.Dispose();
    }

    private sealed class WindowsChild(KernelHandle process, KernelHandle thread, int id) : ISuspendedChild
    {
        private bool suspended = true;
        public int Id => id;
        public LaunchIdentity Identity { get { using var token = OpenToken(process.DangerousGetHandle(), duplicate: false, stage: "QueryChildToken"); return ReadIdentity(token, "QueryChildToken"); } }
        public string ImagePath => ImageName(process);
        public void Resume()
        {
            uint previous = Native.ResumeThread(thread);
            if (previous == uint.MaxValue) throw NativeError("ResumeChild");
            if (previous > 1) throw new SecurityException("The new clipboard helper thread has an unexpected suspend state.");
            // Zero means this already validated own child is running. Return its
            // PID normally instead of losing track of it or terminating it.
            suspended = false;
        }
        public void TerminateIfSuspended()
        {
            if (!suspended) return;
            if (!Native.TerminateProcess(process, 1)) throw NativeError("TerminateSuspendedChild");
            suspended = false;
        }
        public void Dispose() { thread.Dispose(); process.Dispose(); }
    }

    private static KernelHandle OpenToken(IntPtr process, bool duplicate, string stage = "OpenCurrentToken")
    {
        uint access = 0x0008; // TOKEN_QUERY.
        if (duplicate) access |= 0x0002;
        if (!Native.OpenProcessToken(process, access, out IntPtr token)) throw NativeError(stage);
        return new KernelHandle(token);
    }
    private static LaunchIdentity ReadIdentity(KernelHandle token, string stage = "QueryTokenInformation")
    {
        using var user = TokenInformation(token, 1, stage);
        using var session = TokenInformation(token, 12, stage);
        using var elevation = TokenInformation(token, 20, stage);
        using var integrity = TokenInformation(token, 25, stage);
        using var uiAccess = TokenInformation(token, 26, stage);
        string sid = new SecurityIdentifier(Marshal.ReadIntPtr(user.DangerousGetHandle())).Value;
        string label = new SecurityIdentifier(Marshal.ReadIntPtr(integrity.DangerousGetHandle())).Value;
        if (!int.TryParse(label[(label.LastIndexOf('-') + 1)..], out int level)) throw new SecurityException("Unexpected integrity label.");
        return new(sid, Marshal.ReadInt32(session.DangerousGetHandle()), level,
            Marshal.ReadInt32(elevation.DangerousGetHandle()) != 0, Marshal.ReadInt32(uiAccess.DangerousGetHandle()) != 0);
    }
    private static NativeMemory TokenInformation(KernelHandle token, int kind, string stage)
    {
        _ = Native.GetTokenInformation(token, kind, IntPtr.Zero, 0, out int size);
        if (size < 4 || size > 65536) throw NativeError(stage);
        var buffer = new NativeMemory(size);
        if (!Native.GetTokenInformation(token, kind, buffer.DangerousGetHandle(), size, out _))
        { var error = NativeError(stage); buffer.Dispose(); throw error; }
        return buffer;
    }
    private static string ImageName(KernelHandle process)
    {
        var name = new StringBuilder(32768); int size = name.Capacity;
        if (!Native.QueryFullProcessImageNameW(process, 0, name, ref size)) throw NativeError("QueryProcessImage");
        return name.ToString();
    }
    private static string ObjectName(IntPtr handle)
    {
        _ = Native.GetUserObjectInformationW(handle, 2, null, 0, out int bytes);
        if (bytes < 2 || bytes > 4096) throw NativeError("QueryDesktop");
        var name = new StringBuilder((bytes / 2) + 1);
        if (!Native.GetUserObjectInformationW(handle, 2, name, name.Capacity * 2, out _)) throw NativeError("QueryDesktop");
        return name.ToString();
    }
    private static bool IsDefaultInputDesktop()
    {
        using var desktop = new DesktopHandle(Native.OpenInputDesktop(0, false, 0x0001));
        return !desktop.IsInvalid && string.Equals(ObjectName(desktop.DangerousGetHandle()), "Default", StringComparison.OrdinalIgnoreCase);
    }
    internal static string? NativeFailureStage(Exception error) => error.Data["ClipboardHelperLaunchStage"] is string stage
        && stage is "OpenCurrentToken" or "OpenShellProcess" or "OpenShellToken" or "DuplicatePrimary"
            or "CreateEnvironment" or "CreateOrdinaryProcess" or "CreateWithToken" or "QueryChildToken"
            or "QueryTokenInformation" or "QueryProcessImage" or "QueryDesktop" or "ResumeChild" or "TerminateSuspendedChild"
            or "DiscoverHelperProcess" or "QueryProcessTimes" or "ExplorerAutomation"
                ? stage : null;

    private static Win32Exception NativeError(string stage)
    {
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        error.Data["ClipboardHelperLaunchStage"] = stage;
        return error;
    }

    private sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal KernelHandle(IntPtr value) : base(true) { SetHandle(value); }
        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }
    private sealed class NativeMemory : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal NativeMemory(int bytes) : base(true) { SetHandle(Marshal.AllocHGlobal(bytes)); }
        private NativeMemory(string value) : base(true) { SetHandle(Marshal.StringToHGlobalUni(value)); }
        internal static NativeMemory FromString(string value) => new(value);
        protected override bool ReleaseHandle() { Marshal.FreeHGlobal(handle); return true; }
    }
    private sealed class EnvironmentHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal EnvironmentHandle(IntPtr value) : base(true) { SetHandle(value); }
        protected override bool ReleaseHandle() => Native.DestroyEnvironmentBlock(handle);
    }
    private sealed class DesktopHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal DesktopHandle(IntPtr value) : base(true) { SetHandle(value); }
        protected override bool ReleaseHandle() => Native.CloseDesktop(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size;
        internal IntPtr Reserved, Desktop, Title;
        internal int X, Y, Width, Height, XCount, YCount, Fill;
        internal uint Flags;
        internal ushort ShowWindow, ReservedSize;
        internal IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
    }
    // Native vtable order includes IOleWindow. Unused slots deliberately have
    // no managed callers; only GetWindow / QueryActiveShellView are invoked.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr window);
        void ContextSensitiveHelp(); void InsertMenusSB(); void SetMenuSB(); void RemoveMenusSB();
        void SetStatusTextSB(); void EnableModelessSB(); void TranslateAcceleratorSB(); void BrowseObject();
        void GetViewStateStream(); void GetControlWindow(); void SendControlMsg();
        [PreserveSig] int QueryActiveShellView(out IShellView view);
    }
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr window);
        void ContextSensitiveHelp(); void TranslateAccelerator(); void EnableModeless(); void UIActivate(); void Refresh();
        void CreateViewWindow(); void DestroyViewWindow(); void GetCurrentInfo(); void AddPropertySheetPages();
        void SaveViewState(); void SelectItem();
        [PreserveSig] int GetItemObject(uint item, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    private static class Native
    {
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTokenInformation(KernelHandle token, int kind, IntPtr buffer, int size, out int required);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageNameW(KernelHandle process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(KernelHandle process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(KernelHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(KernelHandle process, uint code);
        [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcessRaw(IntPtr process, uint code);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
        [DllImport("userenv.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateEnvironmentBlock(out IntPtr environment, KernelHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
        [DllImport("userenv.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyEnvironmentBlock(IntPtr environment);
        [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        internal delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] internal static extern int GetWindowTextW(IntPtr window, StringBuilder text, int capacity);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(KernelHandle process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("user32.dll")] internal static extern IntPtr GetProcessWindowStation();
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetUserObjectInformationW(IntPtr handle, int kind, StringBuilder? information, int size, out int required);
    }
}
#endif
