Start-Transcript -Path "C:\ProgramData\Startset\install_log.txt" -Append
# Define paths
$startsetDir = "C:\ProgramData\Startset"
$bootEveryDir = Join-Path $startsetDir "boot-every"
$bootOnceDir = Join-Path $startsetDir "boot-once"
$loginWindowDir = Join-Path $startsetDir "login-window"
$loginPrivilegedEveryDir = Join-Path $startsetDir "login-privileged-every"
$loginPrivilegedOnceDir = Join-Path $startsetDir "login-privileged-once"
$loginEveryDir = Join-Path $startsetDir "login-every"
$loginOnceDir = Join-Path $startsetDir "login-once"
$onDemandDir = Join-Path $startsetDir "on-demand"
$logDir = Join-Path $startsetDir "logs"
# Create directories
$directories = @(
    $bootEveryDir, $bootOnceDir, $loginWindowDir,
    $loginPrivilegedEveryDir, $loginPrivilegedOnceDir,
    $loginEveryDir, $loginOnceDir, $onDemandDir, $logDir
)
foreach ($dir in $directories) {
    if (-not (Test-Path -Path $dir)) {
        New-Item -Path $dir -ItemType Directory -Force | Out-Null
        Write-Host "Created directory: $dir"
    }
}
# Define the path to startset.exe
$startsetExecutable = "C:\ProgramData\Startset\startset.exe"
# Check if startset.exe exists
if (-not (Test-Path -Path $startsetExecutable)) {
    Write-Error "startset.exe not found at $startsetExecutable"
    exit 1
}
# Define service parameters
$serviceName = "StartsetService"
$serviceDisplayName = "Startset Service"
$serviceDescription = "Runs Startset scripts at boot, login, and on-demand."
$serviceArgs = "service"
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
                -BinaryPathName "`"$startsetExecutable`" $serviceArgs" `
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
