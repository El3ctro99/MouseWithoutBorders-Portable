// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class UpdaterDialogVisibilityTests
{
    [DataTestMethod]
    [DataRow(false, "no-update")]
    [DataRow(true, "no-update")]
    [DataRow(false, "available")]
    [DataRow(true, "available")]
    [DataRow(false, "failure")]
    [DataRow(true, "failure")]
    [DataRow(false, "cancelled")]
    [DataRow(true, "cancelled")]
    public async Task RealModalUpdaterStaysAboveItsOwnerWhileCheckingAndAfterCompletion(bool topMost, string outcome)
    {
        await OnSta(() =>
        {
            var response = new TaskCompletionSource<UpdateRelease>(TaskCreationOptions.RunContinuationsAsynchronously);
            int checks = 0;
            string? checkedVersion = null;
            using var owner = new MouseWithoutBorders.FrmAbout
            {
                TopMost = topMost,
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-32000, -32000),
            };
            using var update = new MouseWithoutBorders.UpdateForm((version, _) =>
            {
                checkedVersion = version;
                checks++;
                return response.Task;
            })
            {
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-32000, -32000),
            };
            using var observer = new System.Windows.Forms.Timer { Interval = 25 };
            var duration = Stopwatch.StartNew();
            Exception? failure = null;
            bool observedPending = false;
            bool observedCompleted = false;
            bool modalReturned = false;
            Label status = Descendants(update).OfType<Label>().Single(control => control.Name == "updateStatus");
            Button checkAgain = Descendants(update).OfType<Button>().Single(control => control.Name == "checkUpdates");

            void StopWindows()
            {
                if (!update.IsDisposed && update.IsHandleCreated) update.Close();
                if (!owner.IsDisposed && owner.IsHandleCreated) owner.Close();
            }

            observer.Tick += (_, _) =>
            {
                try
                {
                    if (duration.Elapsed > TimeSpan.FromSeconds(8))
                        throw new TimeoutException("The real updater modal flow did not complete within eight seconds.");
                    if (!update.Visible || checks != 1) return;

                    if (!observedPending)
                    {
                        Assert.AreEqual("Checking GitHub…", status.Text);
                        Assert.IsFalse(checkAgain.Enabled, "The first asynchronous check must still be pending.");
                        AssertVisibleOwnedDialog(owner, update, topMost, "while checking");
                        observedPending = true;
                        switch (outcome)
                        {
                            case "no-update": response.SetResult(null!); break;
                            case "available": response.SetResult(Candidate()); break;
                            case "failure": response.SetException(new InvalidOperationException("Deterministic update failure.")); break;
                            case "cancelled": response.SetCanceled(); break;
                            default: throw new InvalidOperationException("Unknown test outcome.");
                        }
                        return;
                    }

                    // Await the actual captured UI continuation, including its
                    // finally block, rather than pumping just the constructor.
                    if (!checkAgain.Enabled) return;
                    AssertVisibleOwnedDialog(owner, update, topMost, "after " + outcome);
                    switch (outcome)
                    {
                        case "no-update": Assert.AreEqual("You're up to date.", status.Text); break;
                        case "available": Assert.IsTrue(status.Text.StartsWith("Version 9.0.0 is available", StringComparison.Ordinal)); break;
                        case "failure": Assert.IsTrue(status.Text.StartsWith("Couldn't check for updates.", StringComparison.Ordinal)); break;
                        case "cancelled": Assert.IsTrue(status.Text.StartsWith("The update check was cancelled or timed out.", StringComparison.Ordinal)); break;
                    }
                    observedCompleted = true;
                    observer.Stop();
                    update.Close();
                }
                catch (Exception error)
                {
                    failure = error;
                    observer.Stop();
                    StopWindows();
                }
            };

            owner.Shown += (_, _) => owner.BeginInvoke((Action)(() =>
            {
                try
                {
                    // Exercise the same presentation method as the real About
                    // button, including ShowDialog(owner) and the Shown check.
                    owner.ShowPortableUpdateDialog(update);
                    modalReturned = true;
                    if (failure is null)
                    {
                        Assert.IsTrue(owner.Enabled, "Closing the updater must re-enable About.");
                        Assert.IsTrue(Native.IsWindowEnabled(owner.Handle), "The native owner must be enabled again.");
                    }
                }
                catch (Exception error) { failure = error; }
                finally { if (!owner.IsDisposed) owner.Close(); }
            }));

            try
            {
                observer.Start();
                _ = owner.ShowDialog();
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                Assert.IsTrue(observedPending, "The real modal dialog must be observed before the asynchronous check resolves.");
                Assert.IsTrue(observedCompleted, "The real modal dialog must remain visible after the asynchronous check resolves.");
                Assert.IsTrue(modalReturned, "Closing the child must return from the real ShowDialog call.");
                Assert.AreEqual(1, checks, "Showing one updater must perform exactly one check; the fixture never uses the network.");
                Assert.AreEqual(Application.ProductVersion, checkedVersion);
            }
            finally
            {
                observer.Stop();
                response.TrySetCanceled();
                StopWindows();
            }
        });
    }

    private static void AssertVisibleOwnedDialog(System.Windows.Forms.Form owner, System.Windows.Forms.Form update, bool topMost, string stage)
    {
        Assert.IsFalse(update.IsDisposed, stage);
        Assert.IsTrue(update.Visible && update.Modal, "The real modal updater must remain visible " + stage);
        Assert.IsTrue(Native.IsWindowVisible(update.Handle), "Windows must still show the updater " + stage);
        Assert.IsFalse(Native.IsIconic(update.Handle), "The updater must not become minimized " + stage);
        // ShowDialog disables the HWND directly; Control.Enabled retains its
        // managed value while the native modal loop owns the interaction.
        Assert.IsFalse(Native.IsWindowEnabled(owner.Handle), "About must remain disabled while its child is modal " + stage);
        Assert.AreSame(owner, update.Owner, "The managed modal owner must be About " + stage);
        Assert.AreEqual(owner.Handle, Native.GetWindow(update.Handle, 4), "The native modal owner must be About " + stage);
        Assert.IsTrue(update.Bounds.Width > 0 && update.Bounds.Height > 0, "The updater must retain nonempty bounds " + stage);
        Assert.AreEqual(topMost, update.TopMost, "The updater must retain its owner's intended topmost behavior " + stage);
        bool nativeTopMost = (Native.GetWindowLongPtr(update.Handle, -20).ToInt64() & 8) != 0;
        Assert.AreEqual(topMost, nativeTopMost, "The actual updater HWND must retain its owner's topmost band " + stage);
        int childIndex = ZOrder(update.Handle);
        int ownerIndex = ZOrder(owner.Handle);
        Assert.IsTrue(childIndex >= 0 && ownerIndex > childIndex,
            $"About must not cover the updater {stage}: child index {childIndex}, owner index {ownerIndex}.");
    }

    private static int ZOrder(IntPtr target)
    {
        IntPtr current = Native.GetTopWindow(IntPtr.Zero);
        for (int index = 0; index < 4096 && current != IntPtr.Zero; index++)
        {
            if (current == target) return index;
            current = Native.GetWindow(current, 2);
        }
        return -1;
    }

    private static UpdateRelease Candidate()
    {
        const string download = ManualUpdates.Releases + "/download/mwb-v9.0.0/";
        return new UpdateRelease("mwb-v9.0.0", "9.0.0", false,
            new Uri(ManualUpdates.Releases + "/tag/mwb-v9.0.0"),
            new UpdateAsset("MouseWithoutBorders.exe", 1048576, new Uri(download + "MouseWithoutBorders.exe"), null!),
            new UpdateAsset("MouseWithoutBorders.exe.sha256", 80, new Uri(download + "MouseWithoutBorders.exe.sha256"), null!));
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private static async Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, true);
                action();
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(12));
    }

    private static class Native
    {
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] internal static extern IntPtr GetTopWindow(IntPtr hwnd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    }
}
