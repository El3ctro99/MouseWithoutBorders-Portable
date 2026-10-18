# Mouse Without Borders — Portable 1.2.0-preview.3

This Windows x64 preview adds an optional service for controlling protected UAC
prompts while keeping the standalone app independent of PowerToys.

## Changes

- An optional LocalSystem service runs a restricted input agent on the active
  console's protected desktop. Mouse and keyboard control use the existing
  authenticated connection; clipboard and file sharing stay in the ordinary
  user app. The protected agent rejects file, clipboard, screen-capture and
  generic command operations.
- Returning from UAC avoids an unnecessary UI-thread delay and retries refused
  or reset connections sooner while the normal desktop listener starts. The
  user confirmed a noticeably shorter pause on the tested two-PC setup.
- **Settings > Other Options > Block edge switching in full-screen apps** now
  exposes the existing preference. Its default remains enabled and saved choices
  are preserved. Disable it on the gaming PC to allow edge switching; games
  that capture the pointer may still require **Ctrl+Alt+F1–F4** shortcuts.
  Both methods were confirmed with Warframe in borderless mode.

## Installation and validation

Download `MouseWithoutBorders.exe` for ordinary portable use, or choose the
compact ZIP for the optional service. Both are unsigned Windows x64 builds;
SHA-256 checksum files are included with the release assets.

The compact ZIP includes the self-contained portable app, optional service
payload, installation/removal scripts and English/Hungarian setup instructions.
Normal desktop use does not require the service. Service setup requires
administrator approval and a trusted Private network; read the included setup
instructions before installing it.

Release x64 builds pass, along with **65 focused tests** and **36 installer
boundary checks**. Real two-PC testing confirmed protected UAC control, a shorter
return pause and Warframe switching. An isolated Windows loopback test also
verified reconnect socket reuse. These checks do not measure complete
end-to-end input latency.

## Preview limitations

- The complete unit suite is not green: eight inherited visual/locale-sensitive
  failures were reproduced at the upstream baseline; a UI timeout was also
  observed. See `docs/SECURE_DESKTOP_VALIDATION.md` for the detailed evidence.
- Lock/sign-in, first launch before sign-in, sleep/wake, console-session changes
  and broader two-PC regression checks remain untested for this service preview.
- Ordinary elevated windows on the normal desktop, Ctrl+Alt+Del and RDP are
  outside this preview's supported scope. Modern Standby disconnections are not
  claimed fixed.
- Use matching current-generation builds on both PCs. Compatibility with the
  old Microsoft Garage standalone edition has not been established.
- The tested EXE's About/GitHub and manual-update links still target the upstream
  `aeae1/MouseWithoutBorders-Portable` repository. Download this fork's preview
  updates from `El3ctro99/MouseWithoutBorders-Portable` Releases; the EXE does not
  install updates automatically.
