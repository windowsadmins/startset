<#
.SYNOPSIS
    Pre-installation script for StartSet package.

.DESCRIPTION
    This script runs before StartSet files are deployed.
    It stops the StartSet service if running and prepares for installation.
#>

# Output goes to stdout only. The packaging tool captures it and the client folds it
# into the managed-install session log, which is the record that gets collected.
# This used to Start-Transcript into a file under the StartSet data directory, which
# diverted the output away from that capture: the file sat where nothing reads it and
# the session log recorded nothing at all.
$ErrorActionPreference = 'Stop'

try {
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

    exit 0
}
catch {
    Write-Host ""
    Write-Host "ERROR: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    
    exit 1
}
