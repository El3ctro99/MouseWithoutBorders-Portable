# Optional sign-in and UAC support — 1.2.0-preview.3

This local development build adds an optional Windows service to the MIT-licensed
Mouse Without Borders Portable fork. The normal application still runs as the
signed-in user. The additional agent handles authenticated keyboard and mouse
input on the active console's Windows sign-in, lock and protected UAC desktop.
PowerToys is not required.

## Status

**Development preview.** Build and automated-test results are recorded in
`SECURE_DESKTOP_VALIDATION.md` in the source tree. Protected UAC control was
confirmed using a physical pair of computers with preview.2. Preview.3 adds
reconnect changes for returning to the normal desktop after UAC; a new physical
trial succeeded, and the user confirmed a noticeably shorter return freeze.
Lock/unlock, sign-in and sleep/wake remain untested.
Installing or starting the new service requires a separate administrator action.
No existing Garage or PowerToys service is disabled or removed by these scripts.

## Use the normal application first

1. Extract the package into a folder you own.
2. Run `portable/MouseWithoutBorders.exe` normally and configure its key and
   computer layout. Use the same preview version on the other computer; the old
   Garage release is not a verified compatible peer.
3. Exit other MWB variants before testing this copy, so they do not compete for
   the same network port. The installer does not stop them for you.
4. From a normal PowerShell window, export this computer's configured profile:

   ```powershell
   & .\portable\MouseWithoutBorders.exe --export-secure-desktop-profile "$PWD\secure-profile.json"
   ```

The export contains the shared secret. It is created with a private Windows ACL
and must stay local. A strong random shared key is recommended for remote sign-in
control. The installer never includes the profile in the public package.

## Full-screen apps and games

Preview.3 adds **Settings → Other Options → Block edge switching in full-screen
apps**. It is checked for new configurations and preserves the existing saved
value. To allow edge switching from a full-screen or borderless game, uncheck it
on the PC running the game; **Switch computers at screen edge** must also be on.

Games can still capture the pointer. In **Keyboard Shortcuts**, enable keyboard
shortcuts and select **Switch PCs, Ctrl+Alt: F1, F2, F3, F4**, then use
**Ctrl+Alt+F1–F4** for the corresponding computer. The user confirmed this hotkey
worked with Warframe running borderless on the other PC.

## UAC prerequisite

Keep UAC enabled and enable the Windows policy **User Account Control: Switch to
the secure desktop when prompting for elevation**. Its registry setting is the
DWORD `PromptOnSecureDesktop` set to **1** under
`HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System`.

With `PromptOnSecureDesktop=0`, UAC prompts remain on the ordinary `Default`
desktop. This preview's privileged agent runs only on `Winlogon`; it does not
control elevated prompts or applications on the ordinary desktop. The pointer
may still move while clicks are blocked by Windows privilege isolation.

The installer and service do not change this UAC policy automatically. Any
required policy change is a separate administrator action.

## Optional administrator installation

Review `install-secure-service.ps1` and `secure-service-common.ps1`. Open 64-bit
PowerShell 7 as administrator in the extracted package folder, then run:

```powershell
.\install-secure-service.ps1 -ProfilePath .\secure-profile.json -StartService
```

The reviewed installation:

- copies fixed application and service binaries/runtime files under
  `%ProgramFiles%\Mouse Without Borders Secure Desktop`;
- writes an administrator/SYSTEM-only input profile under
  `%ProgramData%\MouseWithoutBorders\SecureDesktop`;
- creates the `MouseWithoutBorders.SecureDesktop` automatic LocalSystem service;
- allows its input listener only on private-network local subnets (the configured
  base TCP port plus one; normally **15101**);
- starts it only when `-StartService` is explicitly supplied.

The service reads no user-writable portable preferences. Changes to the normal
app's shared key or layout require a new explicit export and administrator
installation. On errors the installer leaves the optional service stopped; it
does not promise rollback of an earlier development installation.

For a read-only package/profile check before installation:

```powershell
.\install-secure-service.ps1 -ProfilePath .\secure-profile.json -ValidateOnly
```

## Scope and limits

- The optional privileged agent is **input only**. Clipboard, files, screen
  capture, setup/settings windows and generic command IPC are disabled there.
  The ordinary user application retains its existing clipboard and transfers.
- Only the active physical console session is supported. RDP and multiple
  simultaneously active sessions are not an acceptance target.
- Ctrl+Alt+Del / Secure Attention Sequence is not implemented by this preview.
  Windows authentication, UAC and secure-desktop protections stay enabled.
- The service owns and terminates only the child it launched. It does not kill
  every same-named process. Binary/profile paths and desktop are fixed.
- A service does not guarantee operation while Windows suspends the whole
  machine in Modern Standby. The earlier screen-off disconnection is separate.

## Remove the optional service

From administrator PowerShell in the package folder:

```powershell
.\remove-secure-service.ps1
```

This removes only the new service registration and its firewall rule. Protected
binary files and the private profile remain for inspection/reinstallation. The
normal portable application and its preferences remain unchanged.

## License and source

Derived from [aeae1/MouseWithoutBorders-Portable](https://github.com/aeae1/MouseWithoutBorders-Portable)
and Microsoft PowerToys Mouse Without Borders, under the retained MIT license.
This local preview is not an official Microsoft or upstream release.
