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
        New-Item -Path $dir -ItemType Directory -Force
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

# Create the service
if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $serviceName `
                -BinaryPathName "`"$startsetExecutable`" $serviceArgs" `
                -DisplayName $serviceDisplayName `
                -Description $serviceDescription `
                -StartupType Automatic
    Write-Host "Created service: $serviceName"
} else {
    Write-Host "Service $serviceName already exists. Skipping creation."
}

# Start the service
Start-Service -Name $serviceName
Write-Host "Started service: $serviceName"
