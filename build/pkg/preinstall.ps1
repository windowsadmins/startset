<#
.SYNOPSIS
    Pre-installation script for StartSet package.

.DESCRIPTION
    This script runs before StartSet files are deployed.
    It stops the StartSet service if running and prepares for installation.
#>

$ErrorActionPreference = 'Stop'

try {
    $logDir = "C:\ProgramData\StartSet\logs"
    if (-not (Test-Path $logDir)) {
        New-Item -Path $logDir -ItemType Directory -Force | Out-Null
    }
    
    Start-Transcript -Path "$logDir\preinstall_{{VERSION}}.log" -Append

    Write-Host "=========================================="
    Write-Host "StartSet {{VERSION}} Pre-Installation"
    Write-Host "=========================================="
    Write-Host ""

    # Stop StartSet service if it's running
    $serviceName = "StartSet"
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    
    if ($service) {
        Write-Host "Found existing StartSet service..."
        
        if ($service.Status -eq 'Running') {
            Write-Host "  Stopping service..."
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
            
            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($service.Status -eq 'Stopped') {
                Write-Host "    Service stopped successfully"
            } else {
                Write-Host "    Warning: Service status is $($service.Status)" -ForegroundColor Yellow
            }
        } else {
            Write-Host "  Service already stopped"
        }
    } else {
        Write-Host "No existing StartSet service found"
    }

    Write-Host ""
    Write-Host "Pre-installation checks complete"
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
