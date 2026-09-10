<#
.SYNOPSIS
    Pre-installation script for StartSet package.

.DESCRIPTION
    This script runs before StartSet files are deployed.
    It stops the StartSet service if running and prepares for installation.
#>

# This file is the one copy. Both things that package StartSet read it from here:
# the packaging tool, which reads scripts/ out of the repository as checked out, and
# build.ps1, which stages it into the MSI and .nupkg it builds.
#
# There used to be a second copy under build/pkg/ that build.ps1 read instead. The
# two drifted, and because the packaging tool takes this one, a fix could be written,
# reviewed, merged and tagged in the copy build.ps1 used while every machine kept
# installing the old text -- which is exactly what happened with the bounded service
# stop below: it was released and did not reach a single machine.

# Output goes to stdout only. The packaging tool captures it and the client folds it
# into the managed-install session log, which is the record that gets collected.
# This used to Start-Transcript into a file under the StartSet data directory, which
# diverted the output away from that capture: the file sat where nothing reads it and
# the session log recorded nothing at all.
$ErrorActionPreference = 'Stop'

try {
    Write-Host "=========================================="
    Write-Host "StartSet Pre-Installation"
    Write-Host "=========================================="
    Write-Host ""

    # Stop the StartSet service if it is running.
    #
    # This step used to be a bare Stop-Service -Force, and that made StartSet
    # unable to upgrade itself in exactly the situation where an upgrade matters
    # most.
    #
    # Stop-Service blocks until the service actually stops, and the service
    # cannot stop while a login payload is still executing inside it. A payload
    # that hangs -- which is the class of bug these upgrades exist to fix -- keeps
    # the service alive, so the stop never returns, so the MSI never gets past
    # this action, so the fix never installs. -ErrorAction SilentlyContinue does
    # not help: it suppresses errors, not the wait.
    #
    # Measured on a lab workstation on 2026-09-09: two consecutive upgrades hung
    # here for over ten minutes each, and the machine was left with the old
    # version removed and the new one not installed -- no StartSet at all. Only a
    # power cycle cleared it.
    #
    # So the stop is bounded, and the thing that blocks it is cleared first.
    $serviceName = "StartSet"
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

    if (-not $service) {
        Write-Host "No existing StartSet service found"
    }
    elseif ($service.Status -eq 'Stopped') {
        Write-Host "  Service already stopped"
    }
    else {
        Write-Host "Found existing StartSet service..."

        $servicePid = 0
        try { $servicePid = [int](Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop).ProcessId } catch { }

        # The payloads are what hold the service open. They are the service's own
        # child processes, and a hung one is by definition not going to finish on
        # its own -- so clear them before asking the service to stop rather than
        # waiting on something that cannot happen.
        if ($servicePid -gt 0) {
            $payloads = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
                          Where-Object { $_.ParentProcessId -eq $servicePid })
            foreach ($p in $payloads) {
                Write-Host "  Ending payload still running under the service: pid $($p.ProcessId)"
                & taskkill.exe /PID $p.ProcessId /F /T 2>&1 | Out-Null
            }
        }

        Write-Host "  Stopping service..."

        # ServiceController.Stop is asynchronous, unlike Stop-Service, so the wait
        # is ours to bound rather than the cmdlet's to hold.
        $stopped = $false
        try {
            $controller = New-Object System.ServiceProcess.ServiceController $serviceName
            $controller.Stop()
            try {
                $controller.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped,
                                          [TimeSpan]::FromSeconds(45))
                $stopped = $true
            } catch [System.ServiceProcess.TimeoutException] {
                $stopped = $false
            }
        } catch {
            Write-Host "    Could not ask the service to stop: $($_.Exception.Message)"
        }

        if (-not $stopped) {
            # Last resort. An install that cannot proceed is worse than a service
            # killed outright: the service is about to be replaced on disk anyway,
            # and leaving the machine with neither version is the failure this
            # whole branch exists to avoid.
            Write-Host "    Service did not stop within 45s - ending its process so the install can proceed"
            if ($servicePid -gt 0) { & taskkill.exe /PID $servicePid /F /T 2>&1 | Out-Null }
            Start-Sleep -Seconds 3
        }

        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if (-not $service -or $service.Status -eq 'Stopped') {
            Write-Host "    Service stopped successfully"
        } else {
            # Deliberately not fatal. Windows Installer replaces a file that is in
            # use by scheduling it for the next reboot, so an install that proceeds
            # is still better than one that refuses.
            Write-Host "    Warning: service status is $($service.Status); continuing anyway"
        }
    }

    Write-Host ""
    Write-Host "Pre-installation checks complete"
    Write-Host ""

    exit 0
}
catch {
    Write-Host ""
    Write-Host "ERROR: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    
    exit 1
}
