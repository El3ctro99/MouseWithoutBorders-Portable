# Connection recovery — 1.2.0-preview.6

This development preview improves the normal application's attempts to reconnect
after sleep, a network interruption or a network-address change. A display
turning off by itself does not request a disconnect. Recovery runs while Windows
allows the application to execute; it cannot maintain mouse, keyboard or file
sharing while the operating system or network adapter is suspended.

On October 19, 2026 (Europe/Budapest), the Release x64 build, single-file publish,
74 recovery/connection checks and 231 existing regressions passed locally.
Physical two-PC display-off, sleep/wake, network-change and UAC acceptance for
this preview remain pending. Earlier preview results do not establish this
preview's physical connection behavior.

## Notifications and retry policy

The normal app combines these signals with its existing main-window timer:

- `SystemEvents.PowerModeChanged` records suspend/resume. Resume requests a
  fresh socket owner after the desktop becomes eligible again.
- `NetworkChange.NetworkAvailabilityChanged` and `NetworkAddressChanged` record
  availability and IP-address changes, including changes that leave another
  network interface available.
- The main window registers for `GUID_SESSION_DISPLAY_STATUS`. A valid display-on
  event requests a missing-peer check. Display-off and dim events do not close
  connections, pause transfers or mark the machine suspended.

The Windows [session-display notification documentation](https://learn.microsoft.com/en-us/windows/win32/power/power-setting-guids)
defines off, on and dim states separately. Network availability alone means that
an eligible interface is up; it does not prove that a configured peer can be
reached. See Microsoft's [network availability](https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.networkchange.networkavailabilitychanged?view=net-10.0)
and [network address-change](https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.networkchange.networkaddresschanged?view=net-10.0)
documentation.

Events are coalesced with a 500 ms quiet interval and a two-second maximum
debounce window. The timer handles recovery only on the active physical console
and the application's active desktop. Pending work survives an inactive desktop
so returning from UAC or locking does not discard a resume request. An existing
healthy listener is retained for ordinary missing-peer checks; actual resume
requests a replacement of stale transport state.

Missing-peer and listener-failure retries use independent delays of 1, 2, 5, 10
and then 30 seconds, continuing at the capped delay. Peers are checked by their
configured names: one working connection does not hide another missing PC.
An independently tracked newly missing name starts an immediate attempt, so an
unrelated offline PC's 30-second backoff does not delay a fresh UAC handoff.
Repeated failures of the same already missing peer do not reset that backoff.
An owner-local attempt lease prevents duplicate DNS/connect/handshake work for
the same peer, and late workers from a closed owner cannot join its replacement.

Address changes invalidate MWB's in-memory rejected-address cache. They do not
edit user name/IP mappings, flush the Windows DNS cache, change the shared key or
modify the machine layout. Successful recovery still requires a valid peer
address, matching authentication and an allowed network path.

## Socket and shutdown limits

Each outgoing IP worker receives a fresh 30-second retry budget rather than
inheriting a deadline from an old listener. This budget does not preempt a
blocking operating-system connect call, so it is not a hard wall-clock limit on
that call. Unauthenticated handshakes have a separate 30-second deadline.
Authentication cancels that deadline; an established idle peer does not acquire
a blanket receive timeout.

Message sockets request TCP keepalive with 30 seconds of idle time, five-second
probe intervals and three probes. These options are best effort: unsupported
provider options do not reject an otherwise valid connection. A successful send
or the socket's `Connected` property alone does not prove that the peer received
an input event. Keepalive is transport liveness, not an application-level input
acknowledgement, and its timing is not a guarantee while Windows is suspended.

Shutdown stops recovery, detaches the static power/network handlers and releases
the window's power-notification registration. The native notification parser
checks the message kind, setting GUID, exact DWORD payload size and state range.
Its bounded memory reads inspect only this process's notification payload; they
do not open or inspect another process. Handle recreation renews the registration,
and repeated cleanup releases each owned registration at most once.

No power plan, display timeout, network-adapter setting, UAC policy, firewall rule
or Windows service configuration is changed by this feature. It makes no request
to prevent system sleep or keep the display on. Microsoft describes Modern
Standby as a screen-off period containing distinct active and sleep segments;
desktop applications resume when Windows returns them to active operation.
See [Modern Standby](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/modern-standby).

## Deployment and protected desktop scope

This preview is intended to update the **normal application only**. The
already installed protected-UAC service and its preview.3 agent remain separate
and unchanged. Their reviewed profile, binary location, startup mode and firewall
rule are not replaced by this update.

The normal app continues yielding its sockets when its desktop becomes inactive.
The protected service retains ownership of its own Winlogon worker. Network
recovery must not create a competing normal-desktop listener while a protected
UAC or lock desktop is active. Retest that handoff separately from normal network
reconnection; existing preview.3 acceptance is recorded in
[SECURE_DESKTOP_VALIDATION.md](SECURE_DESKTOP_VALIDATION.md).

## Validation record

| Check | Result for preview.6 |
| --- | --- |
| Release x64 build and single-file publish | Passed; version 1.2.0-preview.6 |
| Retry policy, notification coalescing and lifecycle tests | 29 policy and 6 peer-state tests passed |
| Per-peer attempts, connect/handshake deadlines and loopback keepalive tests | 13 new cases and 18 existing TCP retry cases passed |
| Native display-payload parsing and notification-handle cleanup tests | 8 cases passed, including invalid native pointers and concurrent cleanup |
| Existing service, settings, updater, elevation, encryption and transfer regressions | 231 focused cases passed |
| Repository layout and release metadata | Three-project check and 3 metadata cases passed |
| Local installation on the affected laptop | Deferred; the prepared EXE was not applied and preview.5 remains installed |
| Physical display-off, sleep/wake and network-change acceptance | Pending |
| Protected-UAC handoff with the existing preview.3 service | Pending |

Record the tested source revision, normal-app EXE hash, versions on both PCs and
the separately installed service version with the results. Do not include real
pairing keys, private service profiles or transfer journals in public artifacts.

The local self-contained x64 EXE is 88,171,931 bytes, SHA-256
`637C01155A03A4AD05C2E3C581FDA4F60AF3DEB5672F66079B3DC3B152BE047F`.
The 305 targeted checks above are not a claim that the full inherited suite
passes. Known inherited locale/visual failures remain outside this validation.
Private local logs and TRX files are under `artifacts/reconnect-preview6/`.

## Two-PC acceptance checklist

1. **Baseline:** establish a connection and record the normal-app version on
   both PCs. Verify pointer movement, typing, text/image clipboard sharing and a
   small file transfer before changing display or network state.
2. **Display off for ten minutes:** leave the laptop's display off while Windows
   remains running. Distinguish this from an actual sleep/Modern Standby test.
   Check that a healthy connection is not deliberately torn down merely because
   the monitor turned off. Restore the display and verify both directions again.
3. **Actual sleep/wake:** suspend and wake the laptop using its normal controls.
   Complete any Windows unlock normally. Measure reconnection from the point
   when Windows and the network are active; check pointer, keyboard, clipboard
   and transfer controls after recovery. Record failures rather than assuming a
   fixed recovery time on all hardware.
4. **Network change:** interrupt and restore the test machine's network, then
   exercise a controlled address change where practical. Verify recovery without
   editing the pairing key or forcing a manual reconnect. With more configured
   PCs, keep one healthy peer connected while another returns.
5. **Healthy transfer:** repeat a display-only transition during a transfer. The
   display event should not interrupt it. After genuine sleep/network loss,
   inspect the existing recovery state and use Resume/Retry if it is paused;
   confirm the final file and absence of duplicate or corrupted output.
6. **UAC and shutdown:** exercise a protected UAC prompt using the existing
   service, return to the normal desktop and verify remote input. Repeat an
   event burst followed by Exit; no late callback should recreate sockets or
   application windows after shutdown.

These are acceptance steps, not completed results. A localhost socket test or a
synthetic notification does not demonstrate actual laptop sleep, NIC recovery,
peer authentication or protected-desktop input.
