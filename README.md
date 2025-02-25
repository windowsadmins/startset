# StartSet Documentation

StartSet is a Windows-based utility that runs scripts automatically at system boot, user login, and on demand. Inspired by the functionality of [Outset for macOS](https://github.com/macadmins/outset), StartSet bridges similar capabilities for Windows-based environments, enabling administrators to efficiently manage various startup and login-related tasks using PowerShell scripts.

---

## Overview

**StartSet** provides the following key features:

1. **Boot-Every** scripts: Scripts in this directory run every time the system boots.
2. **Boot-Once** scripts: Scripts in this directory run only once at the next system boot.
3. **Login-Window** scripts: Scripts in this directory run when the login screen is active.
4. **Login-Privileged-Every** scripts: Scripts in this directory run every time a user logs in, requiring admin privileges.
5. **Login-Privileged-Once** scripts: Scripts in this directory run once for an admin-level login.
6. **On-Demand** scripts: Scripts in this directory run on demand.
7. **Login-Every** scripts: Scripts in this directory run at every login (user-level).
8. **Login-Once** scripts: Scripts in this directory run once when a user logs in (user-level).

StartSet is installed to `C:\ProgramData\Startset`, which is also where the service executable (`startset.exe`) and log file (`startset.log`) reside.

---

## Directory Structure

Inside `C:\ProgramData\Startset`, the following directories are automatically created:

- `boot-every`
- `boot-once`
- `login-window`
- `login-privileged-every`
- `login-privileged-once`
- `on-demand`
- `login-every`
- `login-once`
- `logs`

**Note**: `logs` is used to store StartSet’s log output.

---

## Service Operation

By default, StartSet installs and runs as a Windows service named `StartsetService`. The service:

1. Initializes logging and ensures all directories exist.
2. Triggers the relevant PowerShell scripts on system boot.
3. Remains active to handle custom script triggers depending on your usage scenario.
4. Listens for stop or shutdown control commands.

When running interactively (not as a service), StartSet will accept command-line flags (for example, `--boot-every`, `--boot-once`, etc.) to process the associated directories manually.

---

## Usage

When you run the executable (`startset.exe`), you can use the following flags:

- `--boot-every` - Run scripts at every system boot (admin-level)
- `--boot-once` - Run scripts once at system boot (admin-level)
- `--login-window` - Run scripts at the login window (user-level)
- `--login-privileged-every` - Run privileged scripts every time a user logs in (admin-level)
- `--login-privileged-once` - Run privileged scripts once at login (admin-level)
- `--on-demand` - Run scripts on demand (user-level)
- `--login-every` - Run scripts every time a user logs in (user-level)
- `--login-once` - Run scripts once at login (user-level)

Example:
```
startset.exe --boot-once
```

This will run all `.ps1` scripts located in `C:\ProgramData\Startset\boot-once`.

---

## Installation & Deployment

1. **Copy** `startset.exe` to `C:\ProgramData\Startset`.
2. **Create** subdirectories if not already present (`boot-every`, `boot-once`, etc.).
3. **Register** StartSet as a Windows service using PowerShell or the included activation script:

```powershell
# Activation script outline
C:\ProgramData\Startset\activate.ps1
```

This script:

- Creates required directories (boot-every, boot-once, etc.) if they don't exist.
- Creates and starts the `StartsetService` (if it isn't already installed).

4. **Place** your desired `.ps1` scripts in the relevant StartSet subdirectories.
5. **Start** the service (or let the script start it for you). The service will run on system boot.

---

## Script Execution & Privileges

- **Admin-level** directories (`boot-every`, `boot-once`, `login-privileged-every`, `login-privileged-once`) require elevated privileges for script execution. If the current session lacks admin rights, StartSet attempts to launch PowerShell with admin privileges.
- **User-level** directories (`login-window`, `login-every`, `login-once`, `on-demand`) run in the user’s session without special elevation.

---

## Logging

- A single log file, `startset.log`, is created in `C:\ProgramData\Startset\startset.log`.
- All script execution attempts are logged, along with any error messages.

---

## Comparing StartSet to Outset

Although StartSet was inspired by Outset (a macOS utility), it is:

- **Platform-focused:** Written in Go for Windows, leveraging Windows-specific services and token checks.
- **Directory-based execution:** Similar directory structure for script management.
- **Admin vs. user-level**: Takes advantage of Windows service capabilities and tokens to differentiate admin-level script execution.

---

## Advanced Usage

- **Running scripts on demand**: Use the `--on-demand` flag. This runs any scripts located in `C:\ProgramData\Startset\on-demand`.
- **Combining flags**: You can combine flags for a single run, although typical usage is to set up the service and let it handle scripts.
- **Debugging**: You can enable debugging logs by setting `isDebug = true` in the Go source.

---

## Customization

The Go source can be modified to suit your specific workflows. Key sections to note:

- `processFlags()` function, which handles all command-line flags.
- `runScripts()` function, which processes `.ps1` files within a directory.
- `isAdmin()` check, which determines the user’s privilege level.
- `runWithAdminPrivileges()` to launch PowerShell with administrative rights.

---

## License

You are free to integrate and distribute StartSet within your organization under the terms of its chosen open-source license.

---

## Contact

For issues or improvements, consider opening a pull request or an issue on the relevant repository. Contributions are welcome to further refine this Windows-based startup script management solution.

---

*Last updated: 2025-02-25*

