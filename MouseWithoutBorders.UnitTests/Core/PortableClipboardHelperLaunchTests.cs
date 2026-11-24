// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;
using static MouseWithoutBorders.Core.PortableClipboardHelperLaunch;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class PortableClipboardHelperLaunchTests
{
    private const string User = "S-1-5-21-1-2-3-1001";
    private const string OtherUser = "S-1-5-21-1-2-3-1002";
    private static LaunchIdentity Medium => new(User, 1, MediumIntegrity, false, false);
    private static LaunchIdentity High => new(User, 1, HighIntegrity, true, false);
    private static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    [TestMethod]
    public void ProductionRequestCanTargetOnlyOwnExeWithOneFixedHelperArgument()
    {
        HelperLaunchRequest request = CreateCurrentRequest();
        Assert.AreEqual(PortableApplication.CurrentExecutablePath, request.ApplicationName);
        Assert.AreEqual(Path.GetDirectoryName(PortableApplication.CurrentExecutablePath), request.WorkingDirectory);
        Assert.AreEqual(QuoteArgument(request.ApplicationName) + " " + PortableApplication.ClipboardHelperArgument, request.CommandLine);
        var arguments = ParseArguments(request.CommandLine);
        CollectionAssert.AreEqual(new[] { request.ApplicationName, PortableApplication.ClipboardHelperArgument }, arguments);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("simple")]
    [DataRow("C:\\Program Files\\MWB & Files\\MouseWithoutBorders.exe")]
    [DataRow("C:\\folder with space\\")]
    [DataRow("quote\" inside \\")]
    [DataRow("ékezetes név")]
    public void WindowsArgumentQuotingRoundTripsWithoutAddingArguments(string value)
    {
        CollectionAssert.AreEqual(new[] { "test.exe", value }, ParseArguments("test.exe " + QuoteArgument(value)));
    }

    [TestMethod]
    public void NullCharacterCannotTruncateTheNativeCommandLine()
    {
        Assert.ThrowsException<ArgumentException>(() => QuoteArgument("file\0 --other-mode"));
    }

    [TestMethod]
    public void OrdinaryCallerKeepsItsMediumContextWithoutOpeningExplorer()
    {
        var system = new FakeSystem(elevated: false);
        Assert.AreEqual(4242, StartCore(system));
        Assert.AreEqual(0, system.ParentOpens);
        Assert.IsNull(system.UsedParent);
        Assert.AreEqual(1, system.Child.Resumes);
        Assert.AreEqual(0, system.Child.Terminations);
        Assert.AreEqual(1, system.Child.Disposals);
        Assert.AreEqual(PortableApplication.CurrentExecutablePath, system.Request!.ApplicationName);
    }

    [TestMethod]
    public void ElevatedCallerUsesExplorerAndValidatesRunningChildWithoutNativeCreationOrResume()
    {
        var system = new FakeSystem(elevated: true);
        Assert.AreEqual(4242, StartCore(system));
        Assert.AreEqual(1, system.ParentOpens);
        Assert.AreSame(system.Parent, system.UsedParent);
        CollectionAssert.AreEqual(new[] { "LaunchThroughExplorer", "ReadChildToken", "ReadChildImage", "DisposeChild", "DisposeParent" }, system.Events);
        Assert.AreEqual(0, system.Creates);
        Assert.AreEqual(1, system.ShellLaunches);
        Assert.AreEqual(0, system.Child.Resumes);
        Assert.AreEqual(0, system.Child.Terminations);
        Assert.AreEqual(1, system.Parent.Disposals);
    }

    [TestMethod]
    public void InvalidDesktopSessionOrCallerNeverCreatesAnyProcess()
    {
        var normal = new LaunchContext(Medium, "WinSta0", "Default", true, false);
        foreach (LaunchContext invalid in new[]
        {
            normal with { Desktop = "Winlogon" }, normal with { Desktop = "Screen-saver" },
            normal with { WindowStation = "Service-0x0-3e7$" }, normal with { DesktopActive = false },
            normal with { SecureAgent = true }, normal with { Identity = Medium with { SessionId = 0 } },
            normal with { Identity = Medium with { Integrity = 0x1000 } },
            normal with { Identity = Medium with { Integrity = HighIntegrity } },
            normal with { Identity = High with { Integrity = 0x4000 } },
            normal with { Identity = Medium with { Elevated = true } },
            normal with { Identity = Medium with { UiAccess = true } },
            normal with { Identity = Medium with { UserSid = "" } },
            normal with { Identity = Medium with { UserSid = "S-1-5-18" } },
            normal with { Identity = Medium with { UserSid = "S-1-5-19" } },
            normal with { Identity = Medium with { UserSid = "S-1-5-20" } },
            normal with { Identity = Medium with { UserSid = "S-1-5-80-123" } },
        })
        {
            var system = new FakeSystem(false) { Context = invalid };
            Assert.ThrowsException<SecurityException>(() => StartCore(system));
            Assert.AreEqual(0, system.ParentOpens);
            Assert.AreEqual(0, system.Creates);
        }
        Assert.IsTrue(IsValidCaller(normal with { WindowStation = "winsta0", Desktop = "default" }));
    }

    [TestMethod]
    public void WrongExplorerAccountSessionIntegrityOrImageNeverCreatesAChild()
    {
        foreach (LaunchIdentity invalid in new[]
        {
            Medium with { UserSid = OtherUser }, Medium with { SessionId = 2 },
            Medium with { Integrity = 0x1000 }, Medium with { Integrity = HighIntegrity },
            Medium with { Elevated = true }, Medium with { UiAccess = true },
        })
        {
            var system = new FakeSystem(true);
            system.Parent.Identity = invalid;
            Assert.ThrowsException<SecurityException>(() => StartCore(system));
            Assert.AreEqual(0, system.Creates);
            Assert.AreEqual(1, system.Parent.Disposals);
        }
        foreach (string image in new[] { "explorer.exe", Path.Combine(Path.GetTempPath(), "explorer.exe"), Explorer + ".other.exe" })
        {
            var system = new FakeSystem(true);
            system.Parent.ImagePath = image;
            Assert.ThrowsException<SecurityException>(() => StartCore(system));
            Assert.AreEqual(0, system.Creates);
            Assert.AreEqual(1, system.Parent.Disposals);
        }
    }

    [TestMethod]
    public void NonCurrentShellIsRejectedBeforeCreation()
    {
        var system = new FakeSystem(true);
        system.Parent.Current = () => false;
        Assert.ThrowsException<SecurityException>(() => StartCore(system));
        Assert.AreEqual(0, system.Creates);
        Assert.AreEqual(1, system.Parent.Disposals);
    }

    [TestMethod]
    public void ElevatedCallerCannotFallbackWhenExplorerParentIsMissing()
    {
        var system = new FakeSystem(true) { MissingParent = true };
        Assert.ThrowsException<SecurityException>(() => StartCore(system));
        Assert.AreEqual(1, system.ParentOpens);
        Assert.AreEqual(0, system.Creates);
    }

    [TestMethod]
    public void ChangedShellAfterExplorerLaunchReleasesHandleWithoutKillingRunningChild()
    {
        var system = new FakeSystem(true);
        int queries = 0;
        system.Parent.Current = () => ++queries == 1;
        Assert.ThrowsException<SecurityException>(() => StartCore(system));
        Assert.AreEqual(0, system.Creates);
        Assert.AreEqual(1, system.ShellLaunches);
        Assert.AreEqual(0, system.Child.Terminations);
        Assert.AreEqual(0, system.Child.Resumes);
        Assert.AreEqual(1, system.Child.Disposals);
        Assert.AreEqual(1, system.Parent.Disposals);
    }

    [TestMethod]
    public void UnexpectedRunningChildTokenHasNoHighFallbackOrTermination()
    {
        foreach (LaunchIdentity invalid in new[]
        {
            High, Medium with { UserSid = OtherUser }, Medium with { SessionId = 2 },
            Medium with { Integrity = 0x1000 }, Medium with { Elevated = true }, Medium with { UiAccess = true },
        })
        {
            var system = new FakeSystem(true);
            system.Child.Token = invalid;
            Assert.ThrowsException<SecurityException>(() => StartCore(system));
            Assert.AreEqual(0, system.Creates, "Failure must not attempt an elevated native launch.");
            Assert.AreEqual(1, system.ShellLaunches);
            Assert.AreEqual(0, system.Child.Resumes);
            Assert.AreEqual(0, system.Child.Terminations);
            Assert.AreEqual(1, system.Child.Disposals);
            Assert.AreEqual(1, system.Parent.Disposals);
        }
    }

    [TestMethod]
    public void DifferentChildImageIsRejectedBeforeExecutingCode()
    {
        var system = new FakeSystem(false);
        system.Child.Image = Path.Combine(Path.GetTempPath(), "unrelated.exe");
        Assert.ThrowsException<SecurityException>(() => StartCore(system));
        Assert.AreEqual(0, system.Child.Resumes);
        Assert.AreEqual(1, system.Child.Terminations);
        Assert.AreEqual(1, system.Child.Disposals);
    }

    [TestMethod]
    public void RunningChildTokenQueryFailureReleasesHandlesWithoutTermination()
    {
        var system = new FakeSystem(true);
        system.Child.TokenError = new Win32Exception(5);
        Assert.ThrowsException<Win32Exception>(() => StartCore(system));
        Assert.AreEqual(0, system.Child.Resumes);
        Assert.AreEqual(0, system.Child.Terminations);
        Assert.AreEqual(1, system.Child.Disposals);
        Assert.AreEqual(1, system.Parent.Disposals);
    }

    [TestMethod]
    public void DifferentRunningImageIsRejectedWithoutKillingTheProcessOrElevatedFallback()
    {
        var system = new FakeSystem(true);
        system.Child.Image = Path.Combine(Path.GetTempPath(), "Other.exe");
        Assert.ThrowsException<SecurityException>(() => StartCore(system));
        Assert.AreEqual(0, system.Creates);
        Assert.AreEqual(1, system.ShellLaunches);
        Assert.AreEqual(0, system.Child.Resumes);
        Assert.AreEqual(0, system.Child.Terminations);
        Assert.AreEqual(1, system.Child.Disposals);
    }

    [TestMethod]
    public void ResumeFailureCleansOnlyNewSuspendedChildAndDoesNotFallback()
    {
        var system = new FakeSystem(false);
        system.Child.ResumeError = new Win32Exception(5);
        Assert.ThrowsException<Win32Exception>(() => StartCore(system));
        Assert.AreEqual(1, system.Creates);
        Assert.AreEqual(1, system.Child.Terminations);
        Assert.AreEqual(1, system.Child.Disposals);
        Assert.AreEqual(0, system.Parent.Disposals);
    }

    [DataTestMethod]
    [DataRow(5)]
    [DataRow(1314)]
    public void CreateFailureReleasesVerifiedParentWithoutTouchingOtherProcesses(int nativeError)
    {
        var system = new FakeSystem(true) { CreateError = new Win32Exception(nativeError) };
        Assert.ThrowsException<Win32Exception>(() => StartCore(system));
        Assert.AreEqual(0, system.Creates);
        Assert.AreEqual(1, system.ShellLaunches);
        Assert.AreEqual(0, system.Child.Resumes);
        Assert.AreEqual(0, system.Child.Terminations);
        Assert.AreEqual(0, system.Child.Disposals);
        Assert.AreEqual(1, system.Parent.Disposals);
    }

    [TestMethod]
    public void DiscoveryRequiresANewExactOwnMediumHelperBeforeCountingIt()
    {
        string own = PortableApplication.CurrentExecutablePath;
        Assert.IsTrue(IsNewHelper(High, Medium, own, own, 100, 100));
        Assert.IsFalse(IsNewHelper(High, Medium, own, own, 99, 100), "An existing helper window is not the launched process.");
        Assert.IsFalse(IsNewHelper(High, Medium, Path.Combine(Path.GetTempPath(), "Other.exe"), own, 101, 100));
        Assert.IsFalse(IsNewHelper(High, High, own, own, 101, 100));
        Assert.IsFalse(IsNewHelper(High, Medium with { UserSid = OtherUser }, own, own, 101, 100));
        Assert.IsFalse(IsNewHelper(High, Medium with { SessionId = 2 }, own, own, 101, 100));
    }

    [TestMethod]
    public void CurrentTokenCanBeReadWithoutStartingAHelperOrLoadingPreferences()
    {
        LaunchIdentity identity = ReadCurrentIdentity();
        Assert.IsFalse(string.IsNullOrWhiteSpace(identity.UserSid));
        Assert.IsTrue(identity.SessionId >= 0);
        Assert.IsTrue(identity.Integrity >= 0x1000);
    }

    [TestMethod]
    public void NativeFailureDiagnosticsAcceptOnlyFixedStagesAndNeverArbitraryData()
    {
        var error = new Win32Exception(5, "C:\\private\\file.exe --secret-value");
        Assert.IsNull(NativeFailureStage(error));
        foreach (string stage in new[] { "OpenCurrentToken", "OpenShellProcess", "OpenShellToken", "DuplicatePrimary",
            "CreateEnvironment", "CreateOrdinaryProcess", "CreateWithToken", "QueryChildToken", "QueryTokenInformation",
            "QueryProcessImage", "QueryDesktop", "ResumeChild", "TerminateSuspendedChild", "DiscoverHelperProcess", "QueryProcessTimes", "ExplorerAutomation" })
        {
            error.Data["ClipboardHelperLaunchStage"] = stage;
            Assert.AreEqual(stage, NativeFailureStage(error));
        }
        error.Data["ClipboardHelperLaunchStage"] = error.Message;
        Assert.IsNull(NativeFailureStage(error));
        error.Data["ClipboardHelperLaunchStage"] = 5;
        Assert.IsNull(NativeFailureStage(error));
    }

    private sealed class FakeSystem : ILaunchSystem
    {
        internal readonly List<string> Events = new();
        internal FakeParent Parent;
        internal FakeChild Child;
        internal int ParentOpens, Creates, ShellLaunches;
        internal Exception? CreateError;
        internal bool MissingParent;
        internal IShellParent? UsedParent;
        internal HelperLaunchRequest? Request;
        public LaunchContext Context { get; set; }
        internal FakeSystem(bool elevated)
        {
            Context = new(elevated ? High : Medium, "WinSta0", "Default", true, false);
            Parent = new(Events); Child = new(Events);
        }
        public IShellParent OpenShellParent() { ParentOpens++; return MissingParent ? null! : Parent; }
        public ISuspendedChild CreateSuspended(HelperLaunchRequest request, IShellParent? parent)
        {
            Creates++; Request = request; UsedParent = parent;
            if (CreateError != null) throw CreateError;
            Events.Add("CreateSuspended");
            return Child;
        }
        public IRunningChild LaunchThroughExplorer(HelperLaunchRequest request, IShellParent parent)
        {
            ShellLaunches++; Request = request; UsedParent = parent;
            if (CreateError != null) throw CreateError;
            Events.Add("LaunchThroughExplorer");
            return Child;
        }
    }
    private sealed class FakeParent(List<string> events) : IShellParent
    {
        public LaunchIdentity Identity { get; set; } = Medium;
        public string ImagePath { get; set; } = Explorer;
        internal Func<bool> Current = () => true;
        public bool IsCurrentShell => Current();
        internal int Disposals;
        public void Dispose() { Disposals++; events.Add("DisposeParent"); }
    }
    private sealed class FakeChild(List<string> events) : ISuspendedChild
    {
        internal LaunchIdentity Token = Medium;
        internal string Image = PortableApplication.CurrentExecutablePath;
        internal Exception? TokenError, ResumeError;
        internal int Resumes, Terminations, Disposals;
        public int Id => 4242;
        public LaunchIdentity Identity
        {
            get { events.Add("ReadChildToken"); if (TokenError != null) throw TokenError; return Token; }
        }
        public string ImagePath { get { events.Add("ReadChildImage"); return Image; } }
        public void Resume() { if (ResumeError != null) throw ResumeError; Resumes++; events.Add("Resume"); }
        public void TerminateIfSuspended() { Terminations++; events.Add("TerminateChild"); }
        public void Dispose() { Disposals++; events.Add("DisposeChild"); }
    }

    private static string[] ParseArguments(string command)
    {
        IntPtr pointer = CommandLineToArgvW(command, out int count);
        if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            return Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size))!).ToArray();
        }
        finally { _ = LocalFree(pointer); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr value);
}
