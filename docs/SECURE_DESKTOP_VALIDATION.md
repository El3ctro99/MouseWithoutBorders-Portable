# Secure desktop preview validation — 2026-10-18

## Scope

Local branch `feature/secure-desktop-service`, version `1.2.0-preview.3`, based on
upstream Portable 1.1.1 commit `e9feba6da37b35a763a638a7e071c7189180cc14`.
The validation below was completed locally before preparing the GitHub fork and
preview release for publication.

The optional service is intended to add authenticated mouse/keyboard control on
the active console's Winlogon/UAC desktop. The existing user-mode app owns normal
clipboard/files. The privileged profile cannot enable those operations, generic
commands, screen capture, setup windows, IPC commands or user-owned executable
paths. SAS/Ctrl+Alt+Del is outside this preview's scope.

## Build environment and automated results

- Windows x64, .NET SDK 10.0.402; bundled/runtime packages 10.0.13.
- MessagePack updated from 3.1.7 to 3.1.9; .NET package baseline updated from
  10.0.11 to 10.0.13 after NuGet reported published security advisories.
- Release x64 app build succeeds. Two inherited `TransferCenter.Move` hiding
  warnings remain; no app compile errors.
- Optional service Release x64 build succeeds with warnings treated as errors:
  **0 warnings, 0 errors**.
- **26 focused secure-desktop unit tests pass**: strict profile/schema/identity
  checks, restricted settings projection, exact launch context and arguments,
  packet filtering, shared socket mutex ACLs, protected-path ACL rules including
  raw generic access masks and private profile-read restrictions. Four additional
  real-token file I/O tests prove atomic private export, no overwrite, preserved
  destination ACLs and cleanup after a failed publication. A packaged startup
  test initially found a WRITE_DAC error in export; that code was corrected to
  create the private staged file atomically before writing any key bytes.
- **36 installer boundary checks pass**: PowerShell parsing, native directory
  helper compilation, actual helper availability, valid/invalid profiles,
  unknown/missing/duplicate properties, port/mouse-mode/name/key bounds, oversized
  data, package checksum tampering, and package path traversal. Synthetic package
  validation runs with `-ValidateOnly`; its fake EXEs are never executed.
- Repository layout and whitespace checks pass. Full runtime and single-file app
  publishing are separate package checks, not evidence of a working SYSTEM agent.
- The final packaged EXE passes normal-user startup/export smoke tests with a
  synthetic key: unprivileged secure-agent, extra-argument and legacy Winlogon
  launches are rejected before portable settings are created; export succeeds
  with a private ACL; source preferences and existing exports are unchanged;
  the real package passes `-ValidateOnly`; service preflight rejects the missing
  protected installation. No network engine or SYSTEM service is started.

## Full test suite is not green

A full run before the last added ACL regression test reported **168 passed,
8 failed, 1 skipped (177 total)**. A subsequent full run reported **169 passed,
8 failed, 1 skipped (178 total)**, before the four additional export I/O tests.
These results are not advertised as an all-pass
release gate.

A separate clean worktree at the exact upstream commit reproduced all eight
initial failing cases, without source changes:

- `GroupSummaryCombinesActiveSpeedsAndDoesNotInventAnEtaForBlockedWork`: the
  assertion expects English `1.0`, while this Hungarian Windows machine renders
  `1,0`;
- `WindowsLoadsLargeTypeArtworkAndLayeredPreviewDoesNotTakeFocus`: native
  desktop-compositing pixel comparison;
- all six `MatrixSurfaceDragCommitsCancelsAndRepaintsCleanly` data rows: stale
  pixel comparisons. A later assertion in one modified run failed before the
  pixel assertion; that specific later assertion has not been independently
  isolated upstream.

Upstream focused runs varied from 6 failed/2 passed to 8 failed/0 passed; the
modified comparison reported 7 failed/1 passed. A later full run also encountered
a folder-window UI timeout. These visual/timing tests remain an unresolved
acceptance limitation, rather than being removed, relaxed or silently skipped.
Local reports are under `artifacts/baseline-comparison` and the baseline worktree.

## First physical UAC trial and recovery

The first preview was installed with the user's explicit approval. The service
ran as LocalSystem, its Winlogon child stayed alive, and its inbound firewall
rule was limited to the protected EXE, TCP 15101, Private profile and LocalSubnet.
The approved home Ethernet connection was changed from Public to Private.

The first elevation prompt appeared on Default because the host had
`PromptOnSecureDesktop=0`. The ordinary user copy retained its listener; pointer
movement worked, but elevated-window clicks did not. Temporarily enabling
protected UAC prompts confirmed socket handoff: the SYSTEM child owned both the
15101 listener and an established peer connection while consent.exe was active.
Nevertheless, the user reported difficult pointer movement with both local and
remote input, so this was **not a successful UAC acceptance test**.

The test service was then stopped and changed to Manual startup. Its owned child
exited, and the host's original `PromptOnSecureDesktop=0` preference was restored.
The user confirmed normal desktop mouse control worked again. The approved home
network profile remains Private. The old Garage service was not changed.

The trial also exposed a Windows-subsystem EXE launch bug in installer preflight:
direct invocation did not reliably set LASTEXITCODE. The installer now waits for
the exact process, checks its actual exit code, and bounds its lifetime to 20
seconds. Nine real WinExe fixture cases pass under StrictMode with unset and
stale LASTEXITCODE values: success, nonzero exit and timeout with child cleanup.

Preview 2 includes additional input/discovery/identity safeguards. These fix
source-level defects and remove local capture from the privileged receiver;
they do not establish a hardware root cause or successful UAC behavior. A fresh
two-computer trial is still required. Ordinary-desktop elevated windows remain
outside this preview's scope.

Preview 2 Release x64 builds successfully and **46 focused tests pass**: the
original 26 secure-boundary/export tests, five discovery/local-capture tests,
six destination-versus-local-identity regressions, five portable installation
tests and four ordinary settings I/O tests. An isolated pre-fix DLL reproduced
the identity corruption with synthetic preferences; the current user's exported
identity and stored local ID were checked without displaying any secret and
already matched, so no user-preference migration was performed.

The initial preview 2 physical trial still produced difficult remote pointer
movement. Its administrator wrapper bounded the trial to 90 seconds, stopped
the service, left startup Manual and restored the previous UAC desktop policy.
The normal installed app was updated to preview 2 with its pairing key and local
identity verified unchanged; the previous EXE is retained locally for rollback.

Subsequent inspection found an avoidable input-guard cost: every protected input
packet created a new Process object and queried SessionId. The validated worker
session is now cached once at startup. The live active-console and per-thread
input-desktop checks remain on every injection. The 46 focused tests pass again;
a benchmark against the actual old/new compiled guard methods is recorded under
`artifacts/guard-hotpath-benchmark`. On the normal host, 1,000 old checks took
2,282.4214 ms and allocated 3,192,000 bytes; the optimized checks took 0.3673 ms
and allocated zero bytes. Both methods passed all 1,000 active-console/desktop
checks. This measurement covers the guard, not the entire network/input path.

The next genuine protected UAC trial succeeded: the harmless elevated probe
completed, and the user explicitly confirmed that remote control worked. A
subsequent remote UAC approval also completed the persistent configuration. The
service now runs as LocalSystem with Automatic startup, the protected agent
matches the tested package, and protected UAC prompts remain enabled. The
temporary cleanup watchdog was retired after the successful trial; the home
network remains Private. This establishes UAC control for this physical pair,
not the untested sign-in, reboot, sleep or ordinary elevated-desktop scenarios.

## Return-to-normal-desktop latency follow-up

After successful UAC control, the user reported a brief pointer freeze when
closing UAC, after which the pointer continued at the same position. Source
review identified two conditional one-second waits in reconnect recovery: a
UI-thread delay before refreshing client connections, and a one-second sleep
after a refused connection while the other desktop's listener was opening.

Preview 3 targets these waits while preserving the existing authenticated wire
protocol and desktop/socket ownership. It removes the UI-thread wait before
refreshing clients and retries a refused/reset connection after 50 ms during a
bounded listener-start grace. The grace begins at the first observed refusal or
reset, has a 20-retry cap, and then retains the original deadline/backoff. Closing
an owner prevents new sockets from being registered or entering the protocol;
failed connections are disposed. Authentication and invalid-key handling remain
unchanged.

Release x64 builds successfully, and **65 focused tests pass**: the previous 46,
18 reconnect cases, and the extended real settings form test. The reconnect cases
include a slow native connection before the first refusal, an expired original
budget, close races, disposal, non-handoff errors and retry limits. The settings
test covers both saved values, master-option dependencies and normal/large-text
layout. Both rendered settings images were inspected; the normal window fits the
new option without a vertical scrollbar. The 36 installer boundary checks pass.

An isolated Windows .NET 10 loopback test invokes the exact compiled App helper
with the same dual-mode TcpClient and managed socket throughout. The first native
Connect took about 2,041 ms to report refusal. A listener opened 75 ms after the
refusal; the helper waited 50 ms, connected on its second attempt, exchanged a
test byte and disposed all sockets. An earlier version starting the grace before
the first Connect failed this check and was corrected before packaging. The
successful report is `artifacts/handoff-loopback/handoff-loopback-result.json`;
its assembly hash matches the tested App DLL. This proves socket reuse and retry
behavior, not a 50 ms total reconnect or physical input latency. A blocking native
Connect can still take longer than the grace interval.

Preview 3 was then installed on the laptop in both the protected service location
and the ordinary user location. Both installed EXE hashes match the reviewed
package; the user pairing key and local machine identity were verified unchanged
without displaying them. The service remains Running/Automatic/LocalSystem, with
protected UAC enabled and the approved home Ethernet profile still Private.

A genuine protected UAC probe completed successfully with the service running.
The user explicitly confirmed the return freeze was noticeably shorter. A local
20 ms read-only collector observed the normal listener 123 ms after Default
returned, and TCP ESTABLISHED after 141 ms. It recorded 706 samples, a maximum
sample gap of 34 ms and no IPv4/IPv6 table errors. The report is
`artifacts/handoff-measurement/preview3-protected-uac.json`. TCP state alone does
not prove authentication completion or end-to-end input latency; the user's
feedback is the physical input acceptance evidence for this trial.

An attempted preview 2 baseline report was not saved because the collector used
an unsupported PowerShell `Set-Content -NoClobber` parameter. Report publication
was corrected to use atomic CreateNew file creation and verified before the
preview 3 trial. No numerical before/after improvement is claimed. Lock/sign-in,
sleep/wake and other physical-pair acceptance items below remain untested.

## Switching from games

The existing full-screen edge-blocking preference is now exposed as **Other
Options > Block edge switching in full-screen apps**. Its default remains true,
and saved choices are preserved. Disabling it on the source/gaming PC permits the
existing edge-switch logic to run for full-screen foreground apps; it does not
release a cursor captured or recentered by a game. The user confirmed that
Ctrl+Alt+F1–F4 switching works from Warframe in borderless mode on the other PC.
After updating to preview 3 and disabling the full-screen edge block on that PC,
the user also confirmed that edge crossing works. This establishes both switching
methods for this physical pair and game configuration; other games may still
capture or recenter the pointer.

## Remaining hardware acceptance

The reviewed administrator installation and genuine protected-UAC control/return
trial have succeeded on this physical pair. Broader acceptance still requires
physical two-computer tests covering:

1. Normal user input, clipboard, file copy/paste and drag/drop.
2. Lock, peer-controlled sign-in and return to normal desktop.
3. Repeated protected-UAC mouse and keyboard trials beyond the successful pair
   tested here.
4. First launch before sign-in and SYSTEM-to-user socket-mutex handoff.
5. Console session changes, service stop/restart, no orphan agent and failed
   profile/path/ACL checks refusing to run.
6. Sleep/wake and reconnect; the prior Modern Standby disconnection is a separate
   issue and is not claimed fixed by this service.

Package scripts are reviewable in the source root and included with the preview.
