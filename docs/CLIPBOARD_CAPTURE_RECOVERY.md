# Clipboard capture recovery — 1.2.0-preview.12 candidate

The user confirmed both PCs were on preview 11, but main-PC-to-laptop copying
still sometimes pasted the laptop's last local text. Reverse copying worked.
Copying fresh plain text sometimes succeeded; browser text from Vivaldi became
transferable after first pasting it elsewhere on the source PC. The running
laptop's main and helper windows answered bounded liveness probes, and its
connections remained established. This does not establish a frozen UI or a
broken network as the cause.

Windows permits clipboard formats to be rendered when requested rather than at
the initial notification. That documented behavior is one plausible explanation
for this source-application pattern, not a confirmed browser defect. See
[Microsoft's clipboard operations documentation](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-operations).

Two existing capture filters discarded events arriving within one second and
extended the suppression period without scheduling another read. Waiting after
a discarded event could never restore it. Preview 12 replaces that path for its
own helper with the following:

- An STA timer coalesces notifications with a fixed initial delay. Clipboard
  contention, unavailable advertised formats and changing sequences retain the
  latest pending update and retry even without another notification.
- One data object supplies Unicode/plain text, RTF and HTML. An unavailable text
  representation never becomes an image fallback. Materialized image data uses
  an exact PNG byte array. The existing first-file clipboard behavior remains.
- The helper awaits a sequence-aware acknowledgement through local IPC, so its
  STA continues processing clipboard/OLE messages. Reentrant/newer updates survive
  in-flight calls. Retries and acknowledgement waits are bounded.
- The main suppresses duplicate sequences and sequences from its own successful
  remote writes. It no longer uses a time or value filter for modern snapshots.
  An actual remote write is tracked separately from its queued UI request, and
  a new local copy immediately afterwards remains eligible.
- Legacy IPC remains available. Its duplicate cache stores the actual typed
  value and checks it before clearing pending data. Local user actions that copy
  text from the app remain shareable. Screenshot timing is separate.

The peer protocol, encryption, pairing and protected-input service are unchanged.
The corrected capture path requires updating the source PC too. Legacy one-second
handling remains only for the legacy local IPC method, not the new helper path.

## Diagnostics and validation limits

A read-only local settings endpoint exposes fixed stage names and numeric
counts/sequences/sizes/HRESULTs for helper snapshot decisions, completed batches
and actual UI writes. It retains no copied contents, paths, hashes of contents or
security keys. Helper retry/exhaustion notices use fixed text in the Windows
Application event log. Existing log files may still contain older private data
and must not be dumped unfiltered.

The Release x64 build and all 510 focused tests passed with no failures or skips.
The 38 additional cases cover deferred final updates, busy/delayed providers,
coherent formats, sequence changes, overlapping writes, echo suppression,
immediate local copies, bounded retry and disposal. They also exercise helper
acknowledgement through the actual proxy library and the actual read-only
settings RPC endpoint.

The reviewed preview 12 was installed on the laptop after human Windows approval
and authenticated, graceful shutdown. Pairing, startup and administrator-launch
choices were preserved. The original source and protected copies matched the
tested binary, and the existing own-user approved-launch task was updated. An
ordinary non-elevated entry-point launch started the protected elevated main,
with a same-user non-elevated clipboard helper launched through Explorer. The
native launcher and protected-input service were unchanged.

The read-only diagnostics endpoint worked against the running application.
Recorded stages showed received text being applied successfully, remote echoes
being suppressed and local snapshots being published. After the source-PC update
instructions, the user confirmed that text copied directly from Vivaldi on the
main PC pasted into the laptop without first pasting elsewhere on the source PC.

This confirms the initial browser-copy check, not resolution of every intermittent
failure. Longer Wi-Fi use, recurring-copy stability, images/files, lock/unlock and
sleep/wake remain physical acceptance checks. No public release or merge has been
made.

## Bitmap capture in preview 13

A small Snipping Tool capture was present on the laptop clipboard as Bitmap and
PNG. The helper exhausted its capture retries without handing the snapshot to the
main process. On that same clipboard sequence, the bundled WinForms runtime's
legacy bitmap read returned no image while its typed read succeeded.

Preview 13 uses the typed bitmap API on the captured data object, matching
WinForms `Clipboard.GetImage`. It accepts formats convertible to Bitmap and
prefers bitmap over accompanying HTML or RTF when there is no plain text.
See the [WinForms bitmap regression fix](https://github.com/dotnet/winforms/pull/14427).

All 515 focused Release x64 tests passed, including five new cases for typed
bitmap reads, conversion and format precedence.

The reviewed build was installed on the laptop and restarted through its
existing approved launch. The user confirmed that a new Snipping Tool capture
from the laptop pasted into Paint on the main PC. The laptop recorded completed
outgoing batches during the test. Reverse image transfer and browser images
remain separate physical checks.
