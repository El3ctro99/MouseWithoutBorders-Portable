// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System;
using MouseWithoutBorders.Core;
using SystemClipboard = System.Windows.Forms.Clipboard;

namespace MouseWithoutBorders;

public partial class FormHelper
{
    private readonly ClipboardCaptureScheduler clipboardCapture = new();
    private System.Windows.Forms.Timer clipboardCaptureTimer;
    private uint ignoredHelperClipboardSequence;
    private bool clipboardRetryReported;
    private bool clipboardCaptureTickRunning;

    private void InitializeClipboardCapture()
    {
        clipboardCaptureTimer = new System.Windows.Forms.Timer { Interval = 25 };
        clipboardCaptureTimer.Tick += ClipboardCaptureTimerTick;
    }

    private void ScheduleClipboardCapture()
    {
        if (IsDisposed || Disposing || clipboardCaptureTimer == null) return;
        if (!clipboardCapture.Pending) clipboardRetryReported = false;
        clipboardCapture.Notify(ClipboardNativeState.Sequence, Environment.TickCount64);
        clipboardCaptureTimer.Start();
    }

    private void IgnoreCurrentHelperClipboardSequence()
    {
        // Right-click screenshot commands copy a local temporary path for their
        // own workflow. Ignore that write, not unrelated user copies afterwards.
        ignoredHelperClipboardSequence = ClipboardNativeState.SuccessfulOwnedSequence() ?? 0;
    }

    private async void ClipboardCaptureTimerTick(object sender, EventArgs e)
    {
        if (clipboardCaptureTickRunning || IsDisposed || Disposing) return;
        clipboardCaptureTickRunning = true;
        try
        {
            if (customScreenCaptureInProgress > 0)
            {
                if (Environment.TickCount64 - screenCaptureStartedTime < 10000) return;
                customScreenCaptureInProgress = 0;
            }

            if (!clipboardCapture.Pending)
            {
                clipboardCaptureTimer?.Stop();
                return;
            }

            clipboardCapture.Notify(ClipboardNativeState.Sequence, Environment.TickCount64);
            if (!clipboardCapture.TryBegin(Environment.TickCount64, out int ticket)) return;
            bool handled = false;
            try
            {
                var snapshot = ClipboardSnapshotReader.Read(() => ClipboardNativeState.Sequence, SystemClipboard.GetDataObject);
                if (IsDisposed || Disposing || clipboardCaptureTimer == null) return;
                if (snapshot.Sequence != clipboardCapture.Sequence)
                    clipboardCapture.Notify(snapshot.Sequence, Environment.TickCount64);
                if (snapshot.Status == ClipboardSnapshotStatus.Ready)
                {
                    handled = snapshot.Sequence != 0 && snapshot.Sequence == ignoredHelperClipboardSequence;
                    if (!handled)
                        handled = await remoteClipboardHelper.SendClipboardSnapshot(snapshot.Data, snapshot.IsFilePath, snapshot.Sequence)
                            .WaitAsync(TimeSpan.FromSeconds(3));
                }
                else if (snapshot.Status == ClipboardSnapshotStatus.TooLarge)
                {
                    SendLog("Trace: Clipboard capture exceeds the supported size limit.");
                    handled = true;
                }
            }
            catch (Exception)
            {
                // Error messages from providers can contain source data or file
                // paths. Keep this diagnostic fixed and leave the copy pending.
                ReportClipboardRetry();
            }
            finally
            {
                var completion = clipboardCapture.Complete(ticket, handled, Environment.TickCount64);
                if (completion == ClipboardCaptureCompletion.Waiting) ReportClipboardRetry();
                if (completion == ClipboardCaptureCompletion.Exhausted)
                    SendLog("Trace: Clipboard capture retry limit reached; waiting for the next clipboard update.");
                if (!clipboardCapture.Pending) clipboardCaptureTimer?.Stop();
            }
        }
        finally { clipboardCaptureTickRunning = false; }
    }

    private void ReportClipboardRetry()
    {
        if (clipboardRetryReported) return;
        clipboardRetryReported = true;
        SendLog("Trace: Clipboard capture deferred; retrying the latest snapshot.");
    }

    private void DisposeClipboardCapture()
    {
        clipboardCapture.Cancel();
        clipboardCaptureTimer?.Stop();
        clipboardCaptureTimer?.Dispose();
        clipboardCaptureTimer = null;
    }
}
