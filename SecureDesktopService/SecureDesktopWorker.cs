// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Security;
using System.ServiceProcess;
using System.Text.Json;

namespace MouseWithoutBorders.SecureDesktop;

/// <summary>
/// Owns one Winlogon-desktop agent in the active physical console session.
/// The existing ordinary-desktop app keeps its user token and normal file/UI work.
/// </summary>
internal sealed class SecureDesktopWorker : ServiceBase
{
    private readonly object gate = new();
    private System.Threading.Timer? timer;
    private NativeSession.OwnedAgent? agent;
    private bool stopping;
    private long nextLaunchTicks;
    private long lastFailureLogTicks;

    internal SecureDesktopWorker()
    {
        ServiceName = ServiceTrust.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanHandleSessionChangeEvent = true;
        AutoLog = false;
    }

    internal static void ValidateProfile()
    {
        ServiceTrust.ValidateInstallation();
        using var stream = new FileStream(ServiceTrust.ProfilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > ServiceTrust.MaximumProfileBytes)
        {
            throw new SecurityException("The protected input profile has an invalid size.");
        }

        using JsonDocument profile = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
        if (profile.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new SecurityException("The protected input profile must be an object.");
        }

        // The agent owns the strict semantic whitelist and refuses unsupported
        // schema, wrong-machine keys/matrix, or clipboard/file-enabled profiles.
        // This supervisor never deserializes profile paths, commands or plugins.
    }

    protected override void OnStart(string[] args)
    {
        // SCM start parameters are deliberately ignored; no caller may choose a
        // command, executable, desktop, profile path or session through them.
        ServiceTrust.RequireLocalSystem();
        ValidateProfile();
        lock (gate)
        {
            stopping = false;
            nextLaunchTicks = 0;
            timer = new System.Threading.Timer(Tick, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }
    }

    private void Tick(object? state)
    {
        // No overlapping launches, and OnStop waits until the owned process has
        // either been placed in its kill-on-close job or disposed after failure.
        lock (gate)
        {
            if (stopping) return;
            try
            {
                uint sessionId = NativeSession.WTSGetActiveConsoleSessionId();
                if (agent != null && (agent.SessionId != sessionId || agent.HasExited))
                {
                    agent.Dispose();
                    agent = null;
                }

                if (!ServiceTrust.IsInteractiveSession(sessionId) || agent != null || Environment.TickCount64 < nextLaunchTicks) return;
                nextLaunchTicks = Environment.TickCount64 + 10_000;
                ValidateProfile();
                agent = NativeSession.Launch(sessionId);
            }
            catch (Exception exception)
            {
                // Fail closed and retry at a bounded rate. Do not delete or modify
                // a suspect profile/binary and never fall back to a portable copy.
                agent?.Dispose();
                agent = null;
                if (Environment.TickCount64 - lastFailureLogTicks >= 60_000)
                {
                    Trace.TraceError("MWB secure desktop agent unavailable ({0}, 0x{1:X8}).", exception.GetType().Name, exception.HResult);
                    lastFailureLogTicks = Environment.TickCount64;
                }
            }
        }
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        lock (gate)
        {
            if (stopping) return;
            if (agent != null && changeDescription.SessionId == agent.SessionId &&
                (changeDescription.Reason == SessionChangeReason.SessionLogoff ||
                 changeDescription.Reason == SessionChangeReason.ConsoleDisconnect ||
                 changeDescription.Reason == SessionChangeReason.SessionRemoteControl))
            {
                agent.Dispose();
                agent = null;
            }

            // Windows supplies the session ID; it is never obtained from IPC.
            // Re-evaluate trusted WTS active-console state on the worker timer.
            nextLaunchTicks = 0;
        }
    }

    protected override void OnStop() => StopOwnedAgent();

    protected override void OnShutdown() => StopOwnedAgent();

    private void StopOwnedAgent()
    {
        lock (gate)
        {
            stopping = true;
            timer?.Dispose();
            timer = null;
            agent?.Dispose();
            agent = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) StopOwnedAgent();
        base.Dispose(disposing);
    }
}
