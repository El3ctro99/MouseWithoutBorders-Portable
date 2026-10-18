# Administrator launch — 1.2.0-preview.5

Preview 5 adds an optional **Run as administrator** launch choice in
**Settings → Installation**, with a separate indication of the current process's
actual permission status. The choice is **off by default** and applies to the
whole ordinary MWB app. It is separate from the protected-UAC service and does
not turn that service on or install it.

## Turn it on or off

1. On the PC whose elevated windows you want to control, open **Settings → Installation** and select **Run as administrator**.
2. Finish any active transfers, exit MWB and reopen the same EXE or its shortcut.
3. Approve the Windows UAC prompt. The Installation page reports the permissions
   of the running process, rather than treating the checkbox as proof that it
   already has administrator rights.

The checkbox saves a launch choice; checking it does not elevate the process
already running. If UAC approval is cancelled or Windows cannot start the
elevated copy, the normal copy continues without administrator permissions. The
launch choice remains enabled, so opening it again can request approval again.
An already elevated ordinary launch does not request a second elevation.

Use the approval for your current Windows account. Providing credentials for a
different administrator account can change the account's registry/profile and
is not covered by the same-user acceptance below.

To turn it off, clear the checkbox, exit MWB and open the same EXE from ordinary
Explorer. Clearing the checkbox does not remove permissions from an existing
process. An update helper and the application it restarts inherit the updater's
existing token, so an updater restart is not a way to demote an elevated copy.

The existing **Start with Windows** checkbox stays independent and unchanged.
When an opted-in ordinary copy launches at sign-in, it requests the normal UAC
approval. No scheduled task, background approval, automatic credential entry,
compatibility flag or UAC-policy change is used to avoid that approval. Actual
sign-in/startup behavior remains part of the validation checklist below.

## What receives administrator permissions

The whole normal application runs with the approved administrator token,
including input handling, clipboard, networking and file-transfer operations.
This can allow input into elevated windows on the ordinary desktop, such as Task
Manager. A session-only manual administrator launch already allowed Task Manager
clicks on the user's physical pair; that earlier result does not validate every
preview 5 launch scenario.

Protected UAC prompts and the sign-in desktop remain the responsibility of the
separate optional secure-desktop service. Administrator launch does not change
its protected payload, private profile, service startup or firewall rules. Read
the [service setup and limits](SECURE_DESKTOP_PREVIEW.md) when using that feature.

Windows [UIPI restrictions](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/user-account-control-allow-uiaccess-applications-to-prompt-for-elevation-without-using-the-secure-desktop)
limit interactions across process integrity levels. Elevated MWB and ordinary Explorer may therefore behave differently
from the normal-user combination. Explorer drops, cross-PC drag-and-drop,
clipboard and file-transfer behavior must be checked on real PCs after enabling
the option; no new compatibility result is claimed here.

## Local storage and launch routing

The choice is stored under the current Windows user's registry hive at
`HKCU\Software\MouseWithoutBorders\Portable\AdministratorLaunch`, as a DWORD
value for the canonical EXE path. Its value name is the SHA-256 of the full,
case-normalized path. An absent or unreadable value means the option is off.
Disabling it removes only that path's value and preserves choices for other
copies. It is not stored in `MouseWithoutBorders.prefs.json`, a pairing key,
shared settings export or service profile.

Replacing the EXE at the same path preserves the local choice. Moving or copying
the EXE to a different directory uses a different choice; another Windows user
or another PC does not inherit it through a copied portable folder. The default
per-user installation has its own choice when its path differs from the original
portable copy. No account-wide `RUNASADMIN` compatibility setting or manifest
requiring elevation is added.

An ordinary no-argument launch checks the choice before settings initialization,
input capture or networking and requests `runas` for the currently executing EXE
with no forwarded arguments. The original process exits only after Windows
confirms creation of the elevated copy. It does not accept a caller-selected
program or shell command to elevate.

Clipboard helpers, installation/launch helpers, update-apply helpers, profile
exports, secure-desktop agents and service/SYSTEM invocations follow their own
existing startup routes. They do not invoke this self-elevation path; helpers
retain the token given by their parent. Ordinary SYSTEM startup remains refused
outside the validated service-agent route. The startup-registration setting,
portable preferences, service settings and Windows UAC policy are not changed
by enabling this option.

## Validation status

On October 19, 2026 (Europe/Budapest), the Release build, single-file publish,
125 focused tests and 65 existing regression checks passed. The actual installed
copy was upgraded with a retained old EXE, its exact-path choice enabled, and
the normal startup requested and received Windows UAC approval. The user
confirmed remote clicks in Task Manager and ordinary-desktop control.

| Check | Status |
| --- | --- |
| Release x64 build and repository checks | Passed; self-contained x64 EXE published locally and three-project repository check passed |
| Isolated default-off/path-specific registry storage tests | Passed in the 13 core module tests, using fresh test keys |
| Normal/elevated/SYSTEM/helper invocation routing tests | Passed module decision/argument tests; existing secure-route regressions also passed |
| Successful launch, UAC cancellation and failed-start behavior tests | Passed simulated launch outcomes; genuine approval also confirmed below |
| Installation checkbox and current-permission status at normal/large text | 10 UI cases passed; both PNGs visually inspected |
| Existing updater, UAC, connection and preference regressions | 102 updater/About cases and 65 existing regressions passed; included in the 125 + 65 total |
| Installed user's exact-path choice enabled and verified | Passed; exact-path DWORD 1 in current-user storage, same owned startup entry and pairing identity retained |
| Real normal launch → approved UAC → elevated app | Passed; normal parent launched a High-integrity main app and High clipboard helper after user approval |
| Real UAC cancellation → normal app remains usable | Pending physical acceptance |
| Checkbox cleared → ordinary Explorer launch has normal permissions | Pending physical acceptance |
| Start with Windows and sign-in UAC behavior | Pending physical acceptance |
| Task Manager clicks and ordinary-desktop remote control | User confirmed both working after the preview 5 upgrade |
| Clipboard and file transfers between two PCs | Not revalidated as part of this administrator-launch acceptance |
| Explorer and cross-PC drag-and-drop across integrity levels | Pending real-PC verification |

Test registry seams must use a fresh isolated test key rather than changing the
real user preference. Published evidence must not include pairing keys, adjacent
private preferences or secure-service profiles. Earlier service and updater
results remain in their respective validation documents and are not counted as
preview 5 administrator-launch tests.

The installed EXE is version `1.2.0-preview.5`, 88,164,748 bytes, SHA-256
`A83C18B42DF9D1CB3E9925E22FBE93346744C4374FE7DA4CFBEB5D9AE69F0A76`.
The bootstrap used an exact PID/start-time/session/path/user check and verified
the shutdown pipe before requesting a graceful exit. It waited for ordinary
helpers, atomically replaced only the reviewed EXE and retained preview 4 for
rollback. It did not force-kill a process or rewrite preferences, startup,
protected-service files, firewall rules or UAC policy. `EnableLUA` and
`PromptOnSecureDesktop` remained enabled; the protected service remained running.

Private local reports are under `artifacts/admin-launch-preview5/`:
`install-result.json`, `runtime-verification.json` and the two TRX files in
`tests/`. UI previews are `ui/mwb-ui-previews/administrator-launch-normal.png`
and `administrator-launch-large.png`. These reports contain pass/fail evidence,
not pairing keys. Genuine UAC cancellation, a new normal launch after clearing
the checkbox, sign-in startup and integrity-crossing drag/drop remain untested.
