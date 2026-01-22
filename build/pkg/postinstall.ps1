<#
.SYNOPSIS
    Post-installation script for StartSet package.

.DESCRIPTION
    This script runs after StartSet files have been deployed.
    It creates necessary directories and installs the StartSet Windows service.
#>

$ErrorActionPreference = 'Stop'

try {
    Start-Transcript -Path "C:\ProgramData\StartSet\logs\postinstall_{{VERSION}}.log" -Append

    Write-Host "=========================================="
    Write-Host "StartSet {{VERSION}} Post-Installation"
    Write-Host "=========================================="
    Write-Host ""

    # Define paths - matching StartSet.Core.Constants.Paths
    $startsetDataDir = "C:\ProgramData\ManagedScripts"
    $installDir = "C:\Program Files\StartSet"
    
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

    Stop-Transcript
    exit 0
}
catch {
    Write-Host ""
    Write-Host "ERROR: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    
    try { Stop-Transcript } catch {}
    
    exit 1
}
