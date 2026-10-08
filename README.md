# StartSet

**Windows port of [macadmins/outset](https://github.com/macadmins/outset)** - Script automation at boot, login, and on-demand for Windows enterprise environments.

## Overview

StartSet provides a robust framework for running scripts at various points during the Windows lifecycle:

- **Boot scripts**: Run at system startup (before user login)
- **Login scripts**: Run when users log in (user or privileged context)
- **On-demand scripts**: Run when triggered manually or via trigger files

## Features

- Full parity with macadmins/outset functionality
- Run-once tracking with checksum validation
- Network connectivity wait before boot scripts
- PowerShell, batch, executable, and package (MSI/MSIX) support
- YAML-based configuration
- Windows Service for automatic trigger detection
- Event log integration
- Serilog logging with file rotation (30 days)
- Dual architecture support (x64 and ARM64)
- Code signing support

## Directory Structure

```
C:\ProgramData\ManagedState\
├── boot-once\              # Scripts run once at boot (deleted after)
├── boot-every\             # Scripts run every boot
├── login-window\           # Scripts run at login window (before auth)
├── login-once\             # Scripts run once per user at login
├── login-every\            # Scripts run every login
├── login-privileged-once\  # Elevated scripts run once per user
├── login-privileged-every\ # Elevated scripts run every login
├── on-demand\              # User-context on-demand scripts
├── on-demand-privileged\   # Elevated on-demand scripts
├── share\                  # Shared data directory
├── triggers\               # Trigger files; the only folder users may create files in
├── Config.yaml             # Legacy configuration file
└── logs\                   # Log files
    └── startset.log

C:\Program Files\StartSet\
├── managedstatekeeper.exe  # CLI / execution engine
└── StartSetService.exe     # Windows Service
```

## Installation

### Manual Installation

1. Copy `managedstatekeeper.exe` and `StartSetService.exe` to `C:\Program Files\StartSet\`
2. Register the Windows Service:
   ```powershell
   sc.exe create StartSet binPath="C:\Program Files\StartSet\StartSetService.exe" start=auto
   sc.exe description StartSet "StartSet - Script automation at boot, login, and on-demand"
   sc.exe start StartSet
   ```

### Via Intune/MDM

Deploy the MSI or .intunewin package through your MDM solution.

## CLI Usage

```powershell
# Run boot scripts
managedstatekeeper boot

# Run login scripts for current user
managedstatekeeper login

# Run on-demand scripts
managedstatekeeper on-demand

# Run privileged on-demand scripts
managedstatekeeper on-demand --privileged

# List all scripts
managedstatekeeper list

# List scripts with execution status
managedstatekeeper list --show-executed

# Add a script to boot-every
managedstatekeeper add myscript.ps1 --type boot-every

# Remove a script
managedstatekeeper remove myscript.ps1 --type boot-every

# Manage ignored users (matching outset)
managedstatekeeper add-ignored-user bob jane
managedstatekeeper remove-ignored-user bob
managedstatekeeper list-ignored-users

# Manage script overrides (force re-run of run-once scripts)
managedstatekeeper add-override myscript.ps1
managedstatekeeper remove-override myscript.ps1 --clear-runonce
managedstatekeeper list-overrides

# Compute checksums (matching outset)
managedstatekeeper checksum myscript.ps1
managedstatekeeper checksum all --record

# Show version
managedstatekeeper --version
```

## Configuration

Each setting is read from the first of these that sets it:

1. A one-off flag for this run (`--verbose`, `--debug`).
2. Policy: `HKLM\SOFTWARE\Policies\StartSet`, for Group Policy or MDM.
3. Machine settings: `HKLM\SOFTWARE\StartSet\Settings`, written by `managedstatekeeper add-ignored-user` and the other settings commands, or by an administrator.
4. The legacy `C:\ProgramData\ManagedState\Config.yaml`.
5. The built-in default.

Environment variables are not a source. Both registry keys are read in the 64-bit view.

Every setting can be set at every level. In the registry the value name is the setting's name below, with booleans and numbers as `REG_DWORD` and lists as `REG_MULTI_SZ`; the Config.yaml name (`wait_for_network`) is accepted as an alias.

| Setting | Config.yaml | Default |
|---|---|---|
| `WaitForNetwork` | `wait_for_network` | `1` |
| `NetworkTimeout` | `network_timeout` | `180` seconds |
| `IgnoreNetworkFailure` | `ignored_network_failure` | `0` |
| `Verbose` | `verbose` | `0` |
| `Debug` | `debug` | `0` |
| `LogLevel` | `log_level` | unset |
| `ChecksumValidation` | `checksum_validation` | `0` |
| `AllowedExtensions` | `allowed_extensions` | `.ps1 .cmd .bat .exe .msi .msix` |
| `ScriptTimeout` | `script_timeout` | `3600` seconds |
| `LoginScriptTimeout` | `login_script_timeout` | `120` seconds |
| `LoginBatchBudget` | `login_batch_budget` | `300` seconds |
| `ParallelExecution` | `parallel_execution` | `0` |
| `LoginDelay` | `login_delay` | `0` seconds |
| `ShellReadyTimeout` | `shell_ready_timeout` | `180` seconds |
| `ShellSettleDelay` | `shell_settle_delay` | `10` seconds |
| `LogonCatchUpGrace` | `logon_catch_up_grace` | `20` seconds |
| `LogScriptOutput` | `log_script_output` | `1` |
| `IgnoredUsers` | `ignored_users` | empty |
| `Overrides` | `overrides` | empty |

`resources/StartSet.admx` with `resources/en-US/StartSet.adml` is an administrative template for every setting above, for Group Policy (copy them to `C:\Windows\PolicyDefinitions` or the central store) or Intune (Imported Administrative templates). Each policy writes the value of the same name under `HKLM\SOFTWARE\Policies\StartSet`, and Managed State Keeper shows it as managed by policy and locks the field. The template's Security category also carries `ManifestSigningKey`, which StartSet reads from policy only and does not show on the Prefs tab.

To set a timeout by policy without the template:

```powershell
New-Item -Path 'HKLM:\SOFTWARE\Policies\StartSet' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\StartSet' -Name NetworkTimeout -Value 60 -Type DWord
```

## Permissions

The service runs as SYSTEM and executes what is under `C:\ProgramData\ManagedState`, so the installer makes that folder writable only by Administrators and SYSTEM (Users can read it), with inheritance from ProgramData turned off. The service applies the same ACL each time it starts.

Once the folder is locked, only an administrator can create a file in it, so a file there counts as written by an administrator whichever account owns it. A payload or `Config.yaml` is used when:

- every folder from the file up to `C:\ProgramData\ManagedState` is locked, so only SYSTEM, Administrators or TrustedInstaller can create, delete or change permissions there;
- no one else has write, delete, change-permissions or take-ownership rights on the file itself;
- and it is not a link.

Otherwise it is skipped, and the run log says why.

An owner always holds the right to change a file's permissions, so the service gives any file owned by an individual account to the Administrators group.

The first time the service locks a folder that was open before, any file in it whose owner is not an administrator could have come from a standard user. The service moves each such file to `C:\ProgramData\ManagedState\quarantine\<timestamp>\` and logs it; nothing is deleted. After that first lock, files are only given to Administrators.

## Trigger Files

Create one of these files to run payloads now. The service watches `C:\ProgramData\ManagedState\triggers`, the one folder a standard user may create files in, and the data root itself:

- `.startset.ondemand` - Triggers on-demand scripts
- `.startset.login` - Runs the login scripts now, in the signed-in user's session
- `.startset.ondemand-privileged` - Triggers privileged on-demand scripts
- `.startset.login-privileged` - Runs the login-privileged scripts now, as SYSTEM
- `.startset.cleanup` - Triggers cleanup of trigger files

The first two are honoured in either folder. The others run payloads as SYSTEM, so they are honoured only in the data root, which only administrators can write. One left in `triggers` is deleted and logged.

```powershell
New-Item -ItemType File 'C:\ProgramData\ManagedState\triggers\.startset.ondemand'
```

## Building from Source

### Prerequisites

- .NET 10 SDK
- Windows SDK (for code signing)
- Code signing certificate (for production builds)

### Build Commands

```powershell
# Full build with signing
.\build.ps1

# Development build (unsigned)
.\build.ps1 -AllowUnsigned

# Build specific architecture
.\build.ps1 -Architecture x64

# Clean build
.\build.ps1 -Clean

# Build with specific certificate
.\build.ps1 -Thumbprint "YOUR_CERT_THUMBPRINT"
```

## Project Structure

```
packages/StartSet/
├── src/
│   ├── StartSet.Core/          # Models, enums, constants
│   ├── StartSet.Infrastructure/ # Logging, config, network, validation
│   ├── StartSet.Engine/         # Script execution engine
│   ├── StartSet.CLI/            # Command-line interface
│   └── StartSet.Service/        # Windows Service
├── build.ps1                    # Build script
├── Directory.Build.props        # Shared build properties
└── StartSet.sln                 # Solution file
```

## License

MIT License - See LICENSE file for details.

## Credits

- Inspired by [macadmins/outset](https://github.com/macadmins/outset)
- Part of the [windowsadmins](https://github.com/windowsadmins) ecosystem
