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
C:\ProgramData\ManagedScripts\
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
├── Config.yaml             # Configuration file
└── logs\                   # Log files
    └── startset.log

C:\Program Files\StartSet\
├── startset.exe            # CLI tool
└── StartSetService.exe     # Windows Service
```

## Installation

### Manual Installation

1. Copy `startset.exe` and `StartSetService.exe` to `C:\Program Files\StartSet\`
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
startset boot

# Run login scripts for current user
startset login

# Run on-demand scripts
startset on-demand

# Run privileged on-demand scripts
startset on-demand --privileged

# List all scripts
startset list

# List scripts with execution status
startset list --show-executed

# Add a script to boot-every
startset add myscript.ps1 --type boot-every

# Remove a script
startset remove myscript.ps1 --type boot-every

# Manage ignored users (matching outset)
startset add-ignored-user bob jane
startset remove-ignored-user bob
startset list-ignored-users

# Manage script overrides (force re-run of run-once scripts)
startset add-override myscript.ps1
startset remove-override myscript.ps1 --clear-runonce
startset list-overrides

# Compute checksums (matching outset)
startset checksum myscript.ps1
startset checksum all --record

# Show version
startset --version
```

## Configuration

Create `C:\ProgramData\ManagedScripts\Config.yaml`:

```yaml
# Wait for network before running boot scripts
wait_for_network: true
network_timeout: 180  # seconds

# Continue even if network wait fails
ignore_network_failure: false

# Logging
verbose: false
debug: false

# Script execution
script_timeout: 3600  # seconds
parallel_execution: false

# Allowed script extensions
allowed_extensions:
  - .ps1
  - .cmd
  - .bat
  - .exe
  - .msi
  - .msix

# Checksum validation (for extra security)
checksum_validation: false

# Delay before login scripts (seconds)
login_delay: 0

# Log script output to individual files
log_script_output: true

# Users to ignore for login script execution
ignored_users: []
  # - serviceaccount
  # - kiosk

# Scripts to force re-run (override run-once tracking)
overrides: []
  # - myscript.ps1
```

## Trigger Files

Create these files to trigger script execution:

- `.startset.ondemand` - Triggers on-demand scripts
- `.startset.ondemand-privileged` - Triggers privileged on-demand scripts
- `.startset.login-privileged` - Triggers login-privileged scripts at next login
- `.startset.cleanup` - Triggers cleanup of trigger files

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
