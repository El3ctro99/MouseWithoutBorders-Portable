# Clipboard helper permissions — 1.2.0-preview.7

When **Run as administrator** is enabled, earlier previews also launched the
hidden clipboard helper with administrator permissions. Ordinary Explorer runs
with normal user permissions. Windows can reject an Explorer drag before the
elevated helper receives its OLE `DragEnter`, so neither a remote preview nor a
transfer window appears.

Preview 7 keeps the ordinary input application elevated but starts its hidden
clipboard helper as the same Windows user with **Medium** integrity. This is
still the same distributed EXE in `--mwb-clipboard-helper` mode. It does not
install a separate helper or change the transfer protocol. A normal-user main
application continues to launch a normal-user helper.

## Launch checks

For an elevated interactive application, the launcher verifies the Windows
Explorer shell's executable, user, session and token. It asks the desktop's
Explorer automation object to launch the current EXE with the fixed helper
argument, following Microsoft's
[documented Explorer launch](https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643/).
It then identifies a newly created helper window and verifies its process image,
creation time, user, session, Medium integrity and absence of elevation/UIAccess.
It rejects ambiguous matches and never terminates a running process if this
verification fails. The trusted helper has already started by that point; this
route does not claim to validate a suspended child before execution.

The Explorer call runs synchronously on the existing STA UI thread. Ordinary
Medium launches continue to use a suspended same-token child, verified before
resuming. Neither path accepts a caller-selected executable or arbitrary
arguments. No privilege, profile, credential or Windows security setting changes.

The launcher refuses another account, a different session, SYSTEM, a secure
desktop, an elevated shell, UIAccess or an unexpected child token. It never
falls back to an elevated helper. Failed creation leaves the main input app
running and reports that clipboard sharing is unavailable.

The existing same-user named-pipe IPC and its protected access rules remain in
place. UAC policy, UIAccess, pairing, firewall rules, startup preferences and
the optional protected-desktop service are unchanged.

## Validation

The reported failure was a drag from an elevated preview 6 laptop to a
preview 4 desktop, with no preview or transfer window. The preview 4 and
preview 6 file-transfer code is unchanged; version mismatch alone is not an
established cause. The reverse direction was not reported as tested.

The Release x64 build and local self-contained publish passed, together with
349 targeted tests (including 25 helper-launch cases and existing IPC, transfer,
administrator-launch, updater, protected-service and reconnect regressions).
The metadata tests and three-project repository check also passed. The full
inherited test suite is not claimed as passing.

Both real Medium/Medium and High/Medium isolated process/IPC fixtures passed.
The Medium child could query its token, open its own process, verify the High
pipe server and perform a benign JSON-RPC round trip through the existing
same-user pipe access rules. The fixture uses a hidden helper-titled window to
exercise the actual Explorer process-discovery path, without MWB input hooks,
preferences, clipboard monitoring or network connections.

The installed laptop was upgraded to preview 7 on October 19, 2026
(Europe/Budapest). Its main app was confirmed High and its actual clipboard
helper Medium, both without UIAccess. Pairing, matrix, administrator opt-in and
startup registration were retained. The protected service and its preview 3
payload were unchanged; Windows UAC and the secure approval desktop stayed on.
The previous preview 6 EXE was retained for rollback.

The installed single-file EXE is 88,182,890 bytes, SHA-256
`A260B6CD624FD4B68643A9CAFF40FEC04066C35EFE89B89FBB6C6FB260EB9A0C`.
Earlier native launch attempts failed the High/Medium fixture, including Windows
error 5 from `CreateProcessWithTokenW`; those guards aborted before stopping or
replacing the running application. The successful Explorer-automation fixture
was required before the actual replacement.

After the upgrade, the user confirmed that dragging a file from the laptop's
ordinary Explorer to the preview 4 desktop successfully copied the file. This
validates the reported laptop-to-desktop case with preview 7 and preview 4.
The confirmation did not include an independent file-hash comparison.

The reverse direction, text/image clipboard sharing, cancellation, remote input
into Task Manager, and normal shutdown/restart still need acceptance for this
preview. Lock/unlock, sleep/wake and reconnect remain separate real-machine
checks.
