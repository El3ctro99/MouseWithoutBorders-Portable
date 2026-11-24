// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#if PORTABLE_SINGLE_FILE
#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MouseWithoutBorders.Core;

/// <summary>
/// An opt-in launch choice for this Windows user and this exact executable path.
/// It deliberately stays outside portable preferences and never changes startup,
/// application compatibility, service, scheduled-task, or Windows UAC policy.
/// </summary>
internal static class PortableElevation
{
    internal const string RegistryPath = @"Software\MouseWithoutBorders\Portable\AdministratorLaunch";
    private const int ElevationCancelled = 1223;

    internal static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key != null && IsEnabled(key, PortableApprovedLaunch.PreferenceExecutablePath);
        }
        catch (Exception)
        {
            // A restricted or unreadable local choice must not make MWB require
            // elevation or prevent the ordinary portable application from starting.
            return false;
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
                ?? throw new IOException("Windows did not provide access to the local administrator-launch setting.");
            SetEnabled(key, PortableApprovedLaunch.PreferenceExecutablePath, enabled: true);
        }
        else
        {
            // Do not create a key while disabling a missing choice, and leave all
            // other executable-path choices and unrelated values untouched.
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            if (key != null) SetEnabled(key, PortableApprovedLaunch.PreferenceExecutablePath, enabled: false);
        }
    }

    // Storage-only seams let tests use a fresh registry key without changing the
    // real launch setting. No executable path supplied here is ever launched.
    internal static bool IsEnabled(RegistryKey key, string executable)
    {
        ArgumentNullException.ThrowIfNull(key);
        string valueName = ValueName(executable);
        object? value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is int number && number == 1 && key.GetValueKind(valueName) == RegistryValueKind.DWord;
    }

    internal static void SetEnabled(RegistryKey key, string executable, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(key);
        string valueName = ValueName(executable);
        if (enabled) key.SetValue(valueName, 1, RegistryValueKind.DWord);
        else key.DeleteValue(valueName, throwOnMissingValue: false);
    }

    internal static string ValueName(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (!Path.IsPathFullyQualified(executable))
            throw new ArgumentException("The administrator-launch choice requires an absolute executable path.", nameof(executable));
        string canonical = Path.GetFullPath(executable).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static bool ShouldRelaunch(string[]? args, bool enabled, bool elevated, bool system)
    {
        // Ordinary launches have no arguments. All hidden helper, export,
        // installation, updater and secure-desktop modes stay on their own path.
        return args is { Length: 1 } && enabled && !elevated && !system;
    }

    internal static bool TryRelaunchElevated(string[] args)
    {
        // Check the argument shape first, before touching the user's local choice.
        if (args is not { Length: 1 } || SecureDesktopAgent.IsActive || SecureDesktopAgent.IsSystem)
            return false;
        if (!ShouldRelaunch(args, IsEnabled(), IsElevated(), system: false)) return false;
        return TryStartElevation(info =>
        {
            using var child = Process.Start(info);
            return child != null;
        }, message => MessageBox.Show(message, "Mouse Without Borders",
            MessageBoxButtons.OK, MessageBoxIcon.Information));
    }

    internal static ProcessStartInfo CreateCurrentLaunchStartInfo()
    {
        // Only the currently running EXE can be relaunched. No untrusted command,
        // shell text, arguments, or caller-selected target crosses this boundary.
        string executable = PortableApplication.CurrentExecutablePath;
        return new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = true,
            Verb = "runas",
        };
    }

    internal static bool TryStartElevation(Func<ProcessStartInfo, bool> start, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            if (start(CreateCurrentLaunchStartInfo())) return true;
            report("Windows did not confirm that Mouse Without Borders started as administrator. " +
                "This copy will continue without administrator permissions. You can try again by reopening the program.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == ElevationCancelled)
        {
            report("Administrator launch was cancelled. Mouse Without Borders will continue without administrator permissions. " +
                "The launch setting remains enabled for the next time you open the program.");
        }
        catch (Exception)
        {
            // Do not log arguments, preferences, or credentials, and never close
            // the usable normal copy merely because ShellExecute failed.
            report("Windows could not start Mouse Without Borders as administrator. " +
                "This copy will continue without administrator permissions. " +
                "You can change this choice in Settings > Installation.");
        }
        return false;
    }
}
#endif
