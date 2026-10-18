# Optional secure desktop service

This project is an opt-in companion to the portable application. It requires an
administrator-protected installation, and is never installed or started by a
portable launch or by the build/tests.

The design follows Microsoft PowerToys' MIT-licensed Mouse Without Borders
[service/desktop launch architecture](https://github.com/microsoft/PowerToys/tree/main/src/modules/MouseWithoutBorders/App/Service).
It deliberately keeps the ordinary desktop process under the interactive user's
token, and starts only the input agent on `winsta0\winlogon` as LocalSystem.
It does not reference PowerToys projects or require the PowerToys application.

## UAC prerequisite and desktop scope

UAC must remain enabled, and **User Account Control: Switch to the secure desktop
when prompting for elevation** must be enabled. The corresponding registry DWORD
is `PromptOnSecureDesktop=1` under
`HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System`.

When that value is `0`, elevation prompts remain on the ordinary `Default`
desktop. This design never launches a SYSTEM agent on that desktop, so elevated
applications and UAC prompts shown there are unsupported by this preview. Pointer
movement without working clicks can occur because the ordinary user process
cannot inject clicks across Windows privilege isolation.

Neither the service nor its installer changes UAC policy automatically. Enabling
the secure-desktop policy, when necessary, is a separate administrator action.

## Fixed installation contract

- Service name: `MouseWithoutBorders.SecureDesktop`
- Protected binaries: `%ProgramFiles%\Mouse Without Borders Secure Desktop`
- Service binary: `MouseWithoutBorders.SecureDesktop.Service.exe`
- Agent binary: `MouseWithoutBorders.exe`
- Only agent argument: `--secure-desktop-agent`
- Private profile: `%ProgramData%\MouseWithoutBorders\SecureDesktop\MouseWithoutBorders.secure.json`
- Private bundled-agent extraction cache: `runtime` beneath that profile directory

The optional package publishes the service as a self-contained **non-single-file**
application. Its runtime DLLs must be installed in the protected binaries directory
along with the two named EXEs. This prevents service-host bundle extraction into a
shared temporary directory before managed validation can execute. The agent remains
the same single-file EXE as the portable application, but its extraction cache and
temporary directory are explicitly restricted to the private service directory.

All product files/directories must be owned by Administrators or SYSTEM, without
untrusted write access; the profile and extraction directory also refuse untrusted
data-read access. Ancestors must be trusted and cannot grant replacement/delete-child
or ACL/ownership modification to an untrusted identity. No reparse points or null
DACLs are accepted. The installer must create these ACLs before copying data. The
worker never repairs or weakens them silently. The profile is size-limited, parsed
read-only, and semantically validated by the application agent before networking.

The service accepts no launch requests or path/session/command IPC. It duplicates
its own SYSTEM token, changes the token's session to the Windows active physical
console, and calls `CreateProcessAsUserW` with an explicit fixed application name.
The agent is created suspended, assigned to an unnamed kill-on-close job limited
to one process, and resumed only after a final console-session check. The worker
keeps its own process and job handles, never scans/kills matching process names,
and disposes the agent on service stop, console disconnect, logoff, or session
replacement. Retries are bounded to one launch per ten seconds. The inactive
Winlogon agent yields the existing MWB socket mutex to the user's default desktop.

`--validate-installation` is a read-only installer preflight that validates only
the fixed protected locations. It never installs/starts a service, launches an
agent or requests elevation. Running with no arguments is reserved for SCM under
LocalSystem; passing other arguments fails.

The secure agent permits authenticated input/control/handshake packets only. It
does not load user-writable settings, show settings dialogs or a tray menu, launch helpers, share the
clipboard, or receive/write files. The standard user's portable application keeps
normal clipboard/file transfers on the ordinary desktop. This feature does not
enable software Ctrl+Alt+Delete policy or alter Windows credential/UAC security.

Build and policy tests alone cannot establish sign-in/UAC interoperability.
Before distributing a supported service release, validate two PCs, lock/unlock,
fresh boot before sign-in, protected UAC, desktop handoff, sleep/wake/reconnect,
service stop, and uninstall on real Windows machines. No such physical validation
is performed merely by compiling this project.
