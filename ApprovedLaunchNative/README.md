# Approved-launch native contract

This optional bootstrap is separate from portable startup and from the secure
desktop service. It performs no installation, UAC changes or task registration.

The protected task action is `MwbApprovedLauncher.exe` with **no arguments**.
The required location is the Known Folder Program Files directory followed by
`Mouse Without Borders Approved Launch\<current-token-user-SID>\`.
The bootstrap starts only the adjacent `MouseWithoutBorders.exe` with the sole
argument `--mwb-approved-main`, an explicit working directory and no shell.
It never reads `source.txt`, portable preferences or caller-selected commands.

Both EXEs and the `runtime`, `runtime\temp` and `runtime\bundle` directories
must already exist. Their owners and every effective allow ACE granting write,
delete, ownership or permission-change access must be Administrators or SYSTEM.
Ordinary read/execute ACEs are allowed; OWNER RIGHTS may grant READ_CONTROL only.
Unknown allow-ACE forms and unknown rights fail closed. The fixed Program Files
and drive ancestors also allow TrustedInstaller ownership and unrelated child
creation, but never untrusted replacement, delete-child or security changes.
All checked objects must be on a local fixed drive and refuse reparse points.
Handles are retained with read sharing only through process creation, including
the target executable, preventing write/delete replacement during that window.

The calling token must already be elevated, High-integrity, in an interactive
session, with Administrators enabled and no UIAccess; SYSTEM and service accounts
are refused. No privileges are enabled or credentials requested. The clean child
environment contains only SystemRoot/WINDIR, System32 PATH/COMSPEC, the current
token's user/profile locations and account name, plus the three fixed runtime
paths. It does not inherit .NET startup hooks, profiler configuration, COMPlus,
COR variables, PATH entries or temporary-directory overrides. The managed
approved-main route remains responsible for validating its protected source
record and applying the intended settings/startup redirection.

Compile as a Unicode Windows C executable (`wWinMain` / Windows subsystem avoids
a transient console; `wmain` / console subsystem is also supported); link `advapi32`,
`shell32`, `ole32` and `userenv` in addition to the normal Windows/CRT libraries.
For Zig/MinGW use `-municode -Wl,--subsystem,windows`; MSVC supports the source
in C11 mode (`/std:c11`). A release distribution should use static CRT linkage.
No installation or task operation belongs in the native build step.

Exit status is numeric only: `0` process creation confirmed; `10` arguments;
`11` caller token; `12` reserved path failure; `13` protected path/ACL checks;
`14` environment construction; `15` child creation; `16` DLL search policy.
The launcher emits no paths, SID, preferences, pairing keys or native error text.
`0` does not assert that the managed app later initialized successfully.
