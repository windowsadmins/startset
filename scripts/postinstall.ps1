<#
.SYNOPSIS
    Post-installation script for StartSet package.

.DESCRIPTION
    This script runs after StartSet files have been deployed.
    It creates necessary directories and installs the StartSet Windows service.
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
    Write-Host "StartSet Post-Installation"
    Write-Host "=========================================="
    Write-Host ""

    # Define paths - matching StartSet.Core.Constants.Paths
    $startsetDataDir = "C:\ProgramData\ManagedState"
    $installDir = "C:\Program Files\StartSet"

    # Add install directory to system PATH if not already present
    Write-Host "Configuring system PATH..."
    $currentPath = [Environment]::GetEnvironmentVariable("PATH", [EnvironmentVariableTarget]::Machine)
    if ($currentPath -notlike "*$installDir*") {
        $newPath = "$currentPath;$installDir"
        [Environment]::SetEnvironmentVariable("PATH", $newPath, [EnvironmentVariableTarget]::Machine)
        Write-Host "  Added $installDir to system PATH"
    } else {
        Write-Host "  $installDir already in system PATH"
    }
    
    # Create required directories
    $directories = @(
        "$startsetDataDir\boot-every",
        "$startsetDataDir\boot-once",
        "$startsetDataDir\login-window",
        "$startsetDataDir\login-privileged-every",
        "$startsetDataDir\login-privileged-once",
        "$startsetDataDir\login-every",
        "$startsetDataDir\login-once",
        "$startsetDataDir\on-demand",
        "$startsetDataDir\on-demand-privileged",
        "$startsetDataDir\share",
        "$startsetDataDir\logs"
    )

    Write-Host "Creating StartSet directories..."
    foreach ($dir in $directories) {
        if (-not (Test-Path -Path $dir)) {
            New-Item -Path $dir -ItemType Directory -Force | Out-Null
            Write-Host "  Created: $dir"
        } else {
            Write-Host "  Exists: $dir"
        }
    }

    # Verify service executable exists
    $serviceExecutable = Join-Path $installDir "StartSetService.exe"
    if (-not (Test-Path -Path $serviceExecutable)) {
        throw "StartSetService.exe not found at $serviceExecutable"
    }
    Write-Host "  Service executable verified: $serviceExecutable"

    # Configure Windows service
    $serviceName = "StartSet"
    $serviceDisplayName = "StartSet Service"
    $serviceDescription = "StartSet - Script automation at boot, login, and on-demand."

    Write-Host ""
    Write-Host "Configuring Windows service..."

    # Remove existing service if present
    $existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($existingService) {
        Write-Host "  Found existing service, removing..."
        if ($existingService.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
            Write-Host "    Stopped service"
        }
        sc.exe delete $serviceName | Out-Null
        Start-Sleep -Seconds 1
        Write-Host "    Deleted existing service"
    }

    # Create the service
    Write-Host "  Creating Windows service..."
    New-Service -Name $serviceName `
                -BinaryPathName "`"$serviceExecutable`"" `
                -DisplayName $serviceDisplayName `
                -Description $serviceDescription `
                -StartupType Automatic | Out-Null
    Write-Host "    Service created: $serviceName"

    # Start the service
    Write-Host "  Starting service..."
    Start-Service -Name $serviceName
    Start-Sleep -Seconds 2
    
    $serviceStatus = Get-Service -Name $serviceName
    if ($serviceStatus.Status -eq 'Running') {
        Write-Host "    Service started successfully"
    } else {
        Write-Host "    Warning: Service status is $($serviceStatus.Status)" -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "=========================================="
    Write-Host "StartSet installation completed successfully!"
    Write-Host "=========================================="
    Write-Host ""

    exit 0
}
catch {
    Write-Host ""
    Write-Host "ERROR: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    
    exit 1
}
