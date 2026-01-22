Start-Transcript -Path "C:\ProgramData\ManagedScripts\logs\install_log.txt" -Append
# Define paths - matching Paths.cs constants
$startsetDir = "C:\ProgramData\ManagedScripts"
$installDir = "C:\Program Files\StartSet"
$bootEveryDir = Join-Path $startsetDir "boot-every"
$bootOnceDir = Join-Path $startsetDir "boot-once"
$loginWindowDir = Join-Path $startsetDir "login-window"
$loginPrivilegedEveryDir = Join-Path $startsetDir "login-privileged-every"
$loginPrivilegedOnceDir = Join-Path $startsetDir "login-privileged-once"
$loginEveryDir = Join-Path $startsetDir "login-every"
$loginOnceDir = Join-Path $startsetDir "login-once"
$onDemandDir = Join-Path $startsetDir "on-demand"
$onDemandPrivilegedDir = Join-Path $startsetDir "on-demand-privileged"
$shareDir = Join-Path $startsetDir "share"
$logDir = Join-Path $startsetDir "logs"
# Create directories
$directories = @(
    $bootEveryDir, $bootOnceDir, $loginWindowDir,
    $loginPrivilegedEveryDir, $loginPrivilegedOnceDir,
    $loginEveryDir, $loginOnceDir, $onDemandDir, 
    $onDemandPrivilegedDir, $shareDir, $logDir
)
foreach ($dir in $directories) {
    if (-not (Test-Path -Path $dir)) {
        New-Item -Path $dir -ItemType Directory -Force | Out-Null
        Write-Host "Created directory: $dir"
    }
}
# Define the path to StartSetService.exe (not startset.exe)
$serviceExecutable = Join-Path $installDir "StartSetService.exe"
# Check if StartSetService.exe exists
if (-not (Test-Path -Path $serviceExecutable)) {
    Write-Error "StartSetService.exe not found at $serviceExecutable"
    exit 1
}
# Define service parameters
$serviceName = "StartSet"
$serviceDisplayName = "StartSet Service"
$serviceDescription = "StartSet - Script automation at boot, login, and on-demand."
# Remove the service if it exists
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existingService) {
    try {
        if ($existingService.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force
            Write-Host "Stopped existing service: $serviceName"
        }
        sc.exe delete $serviceName
        Write-Host "Deleted existing service: $serviceName"
    } catch {
        Write-Error "Failed to remove existing service: $serviceName. $_"
        exit 1
    }
}
# Create the service
try {
    New-Service -Name $serviceName `
                -BinaryPathName "`"$serviceExecutable`"" `
                -DisplayName $serviceDisplayName `
                -Description $serviceDescription `
                -StartupType Automatic
    Write-Host "Created service: $serviceName"
} catch {
    Write-Error "Failed to create service: $serviceName. $_"
    exit 1
}
# Start the service
try {
    Start-Service -Name $serviceName
    Write-Host "Started service: $serviceName"
} catch {
    Write-Error "Failed to start service: $serviceName. $_"
    exit 1
}
Stop-Transcript
