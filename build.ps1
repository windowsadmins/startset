<#
.SYNOPSIS
    Builds the StartSet project with enterprise code signing and MSI/NuGet packaging.

.DESCRIPTION
    This script automates the build and packaging process for StartSet,
    including building .NET binaries, signing them with enterprise certificates, and creating MSI installers.
    
    DEFAULT BEHAVIOR: Running .\build.ps1 with no parameters builds everything (binaries + MSI + NUPKG) with signing.
    
    Version Format: YYYY.MM.DD.HHMM (e.g., 2025.12.15.1430)
    MSI versions are automatically converted to compatible format (YY.MM.DDHH).

.PARAMETER Sign
    Sign binaries with code signing certificate (default if enterprise cert found)

.PARAMETER NoSign
    Skip code signing (for development only)

.PARAMETER Thumbprint
    Use specific certificate thumbprint for signing

.PARAMETER Binaries
    Build all binaries only (skip packaging)

.PARAMETER Install
    Install MSI package after building (requires elevation)

.PARAMETER IntuneWin
    Create IntuneWin packages for Intune deployment

.PARAMETER Dev
    Development mode - stops services, faster iteration, skips signing

.PARAMETER SignMSI
    Sign existing MSI files in release directory (standalone operation)

.PARAMETER SkipMSI
    Skip MSI packaging, build only .nupkg packages

.PARAMETER PackageOnly
    Package existing binaries only (skip build), create both MSI and NUPKG

.PARAMETER NupkgOnly
    Create .nupkg packages only using existing binaries (skip build and MSI)

.PARAMETER MsiOnly
    Create MSI packages only using existing binaries (skip build and NUPKG)

.PARAMETER PkgOnly
    Create .pkg packages only using existing binaries (direct binary payload)

.PARAMETER Clean
    Clean all build artifacts before building

.PARAMETER Configuration
    Build configuration (Debug or Release). Default: Release

.PARAMETER Architecture
    Target architecture (x64, arm64, or both). Default: both

.PARAMETER BuildVersion
    Version to stamp, as YYYY.MM.DD.HHMM. The release workflow passes the tag's version so
    every file in a release carries exactly that version. Default: the current time.

.PARAMETER Test
    Run tests after building

.EXAMPLE
    .\build.ps1
    # Full build with auto-signing (binaries + MSI + NUPKG)

.EXAMPLE
    .\build.ps1 -Dev -Install
    # Development mode: fast rebuild and install

.EXAMPLE
    .\build.ps1 -Binaries
    # Build only binaries, skip packaging

.EXAMPLE
    .\build.ps1 -Sign -Thumbprint XX
    # Force sign with specific certificate

.EXAMPLE
    .\build.ps1 -SkipMSI
    # Build only .nupkg packages, skip MSI packaging

.EXAMPLE
    .\build.ps1 -PackageOnly
    # Package existing binaries (both MSI and NUPKG)

.EXAMPLE
    .\build.ps1 -NupkgOnly
    # Create only .nupkg packages from existing binaries

.EXAMPLE
    .\build.ps1 -MsiOnly
    # Create only MSI packages from existing binaries

.EXAMPLE
    .\build.ps1 -PkgOnly
    # Create only .pkg packages from existing binaries

.EXAMPLE
    .\build.ps1 -IntuneWin
    # Full build including .intunewin packages

.EXAMPLE
    .\build.ps1 -SignMSI
    # Sign existing MSI files in release directory
#>

[CmdletBinding()]
param(
    [switch]$Sign,
    [switch]$NoSign,
    [string]$Thumbprint,
    [switch]$Binaries,
    [switch]$Install,
    [switch]$IntuneWin,
    [switch]$Dev,
    [switch]$SignMSI,
    [switch]$SkipMSI,
    [switch]$PackageOnly,
    [switch]$NupkgOnly,
    [switch]$MsiOnly,
    [switch]$PkgOnly,
    [switch]$Clean,
    [switch]$Test,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Architecture = 'both',
    [ValidatePattern('^\d{4}\.\d{2}\.\d{2}\.\d{4}$')]
    [string]$BuildVersion
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

#region Logging Functions

function Write-BuildLog {
    param(
        [string]$Message,
        [ValidateSet("INFO", "WARNING", "ERROR", "SUCCESS")]
        [string]$Level = "INFO"
    )
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $color = switch ($Level) {
        "INFO"    { "Cyan" }
        "WARNING" { "Yellow" }
        "ERROR"   { "Red" }
        "SUCCESS" { "Green" }
    }
    Write-Host "[$timestamp] " -NoNewline -ForegroundColor DarkGray
    Write-Host "[$Level] " -NoNewline -ForegroundColor $color
    Write-Host $Message
}

#endregion

# Load environment variables from .env file if it exists
function Import-DotEnv {
    param([string]$Path = ".env")
    if (Test-Path $Path) {
        Write-BuildLog "Loading environment variables from $Path"
        Get-Content $Path | ForEach-Object {
            if ($_ -match '^\s*([^#][^=]*)\s*=\s*(.*)\s*$') {
                $name = $matches[1].Trim()
                $value = $matches[2].Trim()
                if ($value -match '^"(.*)"$' -or $value -match "^'(.*)'$") {
                    $value = $matches[1]
                }
                [Environment]::SetEnvironmentVariable($name, $value, [EnvironmentVariableTarget]::Process)
            }
        }
    }
}

Import-DotEnv

# Enterprise certificate configuration - loaded from environment or .env file
$Global:EnterpriseCertCN = $env:STARTSET_CERT_CN ?? $env:CIMIAN_CERT_CN ?? "$(if ($env:SIGNING_CERT_CN) { $env:SIGNING_CERT_CN } else { 'unset-signing-cert-cn' })"
$Global:EnterpriseCertSubject = $env:STARTSET_CERT_SUBJECT ?? $env:CIMIAN_CERT_SUBJECT ?? 'unset-signing-cert-subject'

# Script constants
$script:RootDir = $PSScriptRoot
$script:OutputDir = Join-Path $RootDir 'release'
$script:BuildDir = Join-Path $RootDir 'build'
$script:SrcDir = Join-Path $RootDir 'src'

# The install scripts have one copy, in scripts/, because that is the copy the
# packaging tool reads out of the repository as checked out. build.ps1 stages the
# same files rather than keeping its own set under build/pkg/: when it did, the two
# drifted and the packaged output silently shipped the older text.
$script:InstallScriptsDir = Join-Path $RootDir 'scripts'

#region Certificate and Signing Functions

function Test-Command {
    param ([string]$Command)
    return $null -ne (Get-Command $Command -ErrorAction SilentlyContinue)
}

function Test-CimiPkg {
    $c = Get-Command cimipkg.exe -ErrorAction SilentlyContinue
    if ($c) { return $true }
    
    # Check in common locations
    $possiblePaths = @(
        "$PSScriptRoot\..\CimianToolsGo\release\x64\cimipkg.exe",
        "$PSScriptRoot\..\..\packages\CimianToolsGo\release\x64\cimipkg.exe",
        "C:\Program Files\Cimian\cimipkg.exe"
    )
    
    foreach ($path in $possiblePaths) {
        if (Test-Path $path) {
            return $true
        }
    }
    
    return $false
}

function Get-CimiPkgPath {
    $c = Get-Command cimipkg.exe -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    
    # Check in common locations
    $possiblePaths = @(
        "$PSScriptRoot\..\CimianToolsGo\release\x64\cimipkg.exe",
        "$PSScriptRoot\..\..\packages\CimianToolsGo\release\x64\cimipkg.exe",
        "C:\Program Files\Cimian\cimipkg.exe"
    )
    
    foreach ($path in $possiblePaths) {
        if (Test-Path $path) {
            return $path
        }
    }
    
    throw "cimipkg.exe not found. Build CimianToolsGo first or add cimipkg to PATH."
}

function Get-SigningCertThumbprint {
    [OutputType([hashtable])]
    param([string]$ProvidedThumbprint)
    
    # Use provided thumbprint first
    if ($ProvidedThumbprint) {
        $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $ProvidedThumbprint }
        if ($cert) {
            return @{ Thumbprint = $cert.Thumbprint; Store = "CurrentUser"; Certificate = $cert }
        }
        $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -eq $ProvidedThumbprint }
        if ($cert) {
            return @{ Thumbprint = $cert.Thumbprint; Store = "LocalMachine"; Certificate = $cert }
        }
    }
    
    # Check CurrentUser store first
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { 
        $_.HasPrivateKey -and $_.Subject -like "*$Global:EnterpriseCertSubject*" 
    } | Sort-Object NotAfter -Descending | Select-Object -First 1
    
    if ($cert) {
        return @{ Thumbprint = $cert.Thumbprint; Store = "CurrentUser"; Certificate = $cert }
    }
    
    # Check LocalMachine store
    $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { 
        $_.HasPrivateKey -and $_.Subject -like "*$Global:EnterpriseCertSubject*" 
    } | Sort-Object NotAfter -Descending | Select-Object -First 1
    
    if ($cert) {
        return @{ Thumbprint = $cert.Thumbprint; Store = "LocalMachine"; Certificate = $cert }
    }
    
    return $null
}

$Global:SignToolPath = $null

function Get-SignToolPath {
    if ($Global:SignToolPath -and (Test-Path $Global:SignToolPath)) {
        return $Global:SignToolPath
    }
    
    # Check PATH (prefer x64)
    $c = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($c -and $c.Source -match '\\x64\\') { 
        $Global:SignToolPath = $c.Source
        return $Global:SignToolPath
    }
    
    # Search Windows SDK
    $programFilesx86 = [Environment]::GetFolderPath('ProgramFilesX86')
    $searchRoot = Join-Path $programFilesx86 "Windows Kits\10\bin"
    
    if (Test-Path $searchRoot) {
        $candidates = Get-ChildItem -Path $searchRoot -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object { $_.Directory.Parent.Name } -Descending
        
        if ($candidates -and $candidates.Count -gt 0) {
            $Global:SignToolPath = $candidates[0].FullName
            return $Global:SignToolPath
        }
    }
    
    # Check registry
    try {
        $kitsRoot = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots" -Name KitsRoot10 -ErrorAction SilentlyContinue
        if ($kitsRoot) { 
            $regRoot = Join-Path $kitsRoot.KitsRoot10 'bin'
            if (Test-Path $regRoot) {
                $candidates = Get-ChildItem -Path $regRoot -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
                    Where-Object { $_.FullName -match '\\x64\\' } |
                    Sort-Object { $_.Directory.Parent.Name } -Descending
                
                if ($candidates -and $candidates.Count -gt 0) {
                    $Global:SignToolPath = $candidates[0].FullName
                    return $Global:SignToolPath
                }
            }
        }
    } catch {}
    
    return $null
}

function Test-SignTool {
    $path = Get-SignToolPath
    if (-not $path) {
        throw "signtool.exe not found. Install Windows 10/11 SDK (Signing Tools)."
    }
}

function Invoke-SignArtifact {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Thumbprint,
        [string]$Store = "CurrentUser",
        [int]$MaxAttempts = 4
    )

    if (-not (Test-Path -LiteralPath $Path)) { 
        throw "File not found: $Path" 
    }
    
    $signToolExe = Get-SignToolPath
    if (-not $signToolExe) {
        throw "signtool.exe not found. Install Windows 10/11 SDK."
    }

    $storeParam = if ($Store -eq "CurrentUser") { "/s", "My" } else { "/s", "My", "/sm" }
    
    $tsas = @(
        'http://timestamp.digicert.com',
        'http://timestamp.sectigo.com',
        'http://timestamp.entrust.net/TSS/RFC3161sha2TS'
    )

    $attempt = 0
    while ($attempt -lt $MaxAttempts) {
        $attempt++
        foreach ($tsa in $tsas) {
            try {
                Write-BuildLog "Signing (attempt $attempt): $Path" "INFO"
                
                $signArgs = @(
                    "sign"
                    "/sha1", $Thumbprint
                    "/tr", $tsa
                    "/td", "sha256"
                    "/fd", "sha256"
                ) + $storeParam + @("`"$Path`"")
                
                $psi = New-Object System.Diagnostics.ProcessStartInfo
                $psi.FileName = $signToolExe
                $psi.Arguments = $signArgs -join ' '
                $psi.UseShellExecute = $false
                $psi.RedirectStandardOutput = $true
                $psi.RedirectStandardError = $true
                $psi.CreateNoWindow = $true
                
                $process = [System.Diagnostics.Process]::Start($psi)
                $null = $process.StandardOutput.ReadToEnd()
                $null = $process.StandardError.ReadToEnd()
                $process.WaitForExit()
                
                if ($process.ExitCode -eq 0) {
                    Write-BuildLog "Successfully signed: $Path" "SUCCESS"
                    return
                }
            }
            catch {
                Write-BuildLog "Signing attempt failed: $_" "WARNING"
            }
            
            Start-Sleep -Seconds (2 * $attempt)
        }
    }

    throw "Signing failed after $MaxAttempts attempts: $Path"
}

#endregion

#region Version Functions

function Get-BuildVersion {
    $currentTime = if ($BuildVersion) {
        [datetime]::ParseExact($BuildVersion, 'yyyy.MM.dd.HHmm', [Globalization.CultureInfo]::InvariantCulture)
    } else {
        Get-Date
    }
    $fullVersion = $currentTime.ToString("yyyy.MM.dd.HHmm")
    $semanticVersion = "{0}.{1}.{2}.{3}" -f ($currentTime.Year - 2000), $currentTime.Month, $currentTime.Day, $currentTime.ToString("HHmm")
    
    return @{
        Full = $fullVersion
        Semantic = $semanticVersion
        MsiCompatible = "{0}.{1}.{2}{3:D2}" -f ($currentTime.Year - 2000), $currentTime.Month, $currentTime.Day, [int]$currentTime.ToString("HH")
    }
}

#endregion

#region Build Functions

function Initialize-BuildEnvironment {
    Write-BuildLog "Initializing build environment..."
    
    # Create output directories
    $archs = if ($Architecture -eq 'both') { @('x64', 'arm64') } elseif ($Architecture -eq 'x64') { @('x64') } else { @('arm64') }
    
    foreach ($arch in $archs) {
        $archDir = Join-Path $OutputDir $arch
        if (-not (Test-Path $archDir)) {
            New-Item -ItemType Directory -Path $archDir -Force | Out-Null
        }
    }
    
    # Verify dotnet is available
    if (-not (Test-Command "dotnet")) {
        throw ".NET SDK not found. Please install .NET SDK."
    }
    
    $dotnetVersion = & dotnet --version
    Write-BuildLog "Using .NET SDK: $dotnetVersion" "SUCCESS"
}

function Invoke-Clean {
    Write-BuildLog "Cleaning build artifacts..."
    
    # Clean release directory
    if (Test-Path $OutputDir) {
        Remove-Item -Path "$OutputDir\*" -Recurse -Force -ErrorAction SilentlyContinue
    }
    
    # Clean bin/obj folders in projects
    Get-ChildItem -Path $SrcDir -Include 'bin', 'obj' -Recurse -Directory | ForEach-Object {
        Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
    
    Write-BuildLog "Clean complete" -Level 'SUCCESS'
}

function Build-Solution {
    Write-BuildLog "Building solution..."
    
    $solutionPath = Join-Path $RootDir 'StartSet.sln'
    $config = if ($Dev) { 'Debug' } else { $Configuration }
    
    $buildArgs = @(
        'build',
        $solutionPath,
        '--configuration', $config,
        '--verbosity', 'minimal'
    )
    
    Write-BuildLog "dotnet $($buildArgs -join ' ')"
    & dotnet @buildArgs
    
    if ($LASTEXITCODE -ne 0) {
        throw "Solution build failed with exit code $LASTEXITCODE"
    }
    
    Write-BuildLog "Solution build complete" -Level 'SUCCESS'
}

function Publish-Binary {
    param(
        [string]$Name,
        [string]$ProjectPath,
        [string]$RuntimeIdentifier,
        [string]$OutputPath,
        [hashtable]$Version
    )
    
    $config = if ($Dev) { 'Debug' } else { $Configuration }
    
    $publishArgs = @(
        'publish',
        $ProjectPath,
        '--configuration', $config,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $OutputPath,
        '-p:PublishSingleFile=true',
        '-p:PublishReadyToRun=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        "-p:Version=$($Version.Full)",
        '--verbosity', 'minimal'
    )
    
    Write-BuildLog "Publishing $Name for $RuntimeIdentifier..."
    & dotnet @publishArgs
    
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish $Name for $RuntimeIdentifier"
    }
    
    Write-BuildLog "$Name ($RuntimeIdentifier) built successfully" -Level 'SUCCESS'
}

function Build-AllBinaries {
    param([hashtable]$Version)
    
    $archs = if ($Architecture -eq 'both') { @('x64', 'arm64') } elseif ($Architecture -eq 'x64') { @('x64') } else { @('arm64') }
    $runtimeMap = @{ 'x64' = 'win-x64'; 'arm64' = 'win-arm64' }
    
    Write-BuildLog "Target architectures: $($archs -join ', ')"
    
    foreach ($arch in $archs) {
        $runtime = $runtimeMap[$arch]
        $outputPath = Join-Path $OutputDir $arch
        
        if (-not (Test-Path $outputPath)) {
            New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
        }
        
        # Build CLI
        $cliProject = Join-Path $SrcDir "CLI\StartSet.CLI.csproj"
        Publish-Binary -Name "StartSet CLI" -ProjectPath $cliProject -RuntimeIdentifier $runtime -OutputPath $outputPath -Version $Version
        
        # Rename CLI executable
        $cliExe = Join-Path $outputPath "StartSet.CLI.exe"
        $targetCliExe = Join-Path $outputPath "managedstatekeeper.exe"
        if (Test-Path $cliExe) {
            Move-Item $cliExe $targetCliExe -Force
        }
        
        # Build Service
        $serviceProject = Join-Path $SrcDir "Service\StartSet.Service.csproj"
        Publish-Binary -Name "StartSet Service" -ProjectPath $serviceProject -RuntimeIdentifier $runtime -OutputPath $outputPath -Version $Version
        
        # Rename Service executable
        $serviceExe = Join-Path $outputPath "StartSet.Service.exe"
        $targetServiceExe = Join-Path $outputPath "StartSetService.exe"
        if (Test-Path $serviceExe) {
            Move-Item $serviceExe $targetServiceExe -Force
        }
        
        # Clean up PDB files
        Get-ChildItem -Path $outputPath -Filter "*.pdb" | Remove-Item -Force

        # Build the GUI, Managed State Keeper.exe, into its own folder; the packages copy
        # it in beside the CLI and the service.
        Build-GuiApp -Arch $arch -RuntimeIdentifier $runtime -OutputPath (Join-Path $outputPath 'gui') -Version $Version
    }

    Write-BuildLog "All binaries built successfully" -Level 'SUCCESS'
}

# The GUI's executable, as installed in C:\Program Files\StartSet beside managedstatekeeper.exe.
$script:GuiExecutableName = 'Managed State Keeper.exe'

function Build-GuiApp {
    param(
        [string]$Arch,
        [string]$RuntimeIdentifier,
        [string]$OutputPath,
        [hashtable]$Version
    )

    $config = if ($Dev) { 'Debug' } else { $Configuration }
    $appProject = Join-Path $SrcDir 'App\StartSet.App.csproj'

    if (Test-Path $OutputPath) {
        Remove-Item $OutputPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

    # A WinUI app is published as a folder, not a single file.
    $publishArgs = @(
        'publish',
        $appProject,
        '--configuration', $config,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $OutputPath,
        "-p:Version=$($Version.Full)",
        '--verbosity', 'minimal'
    )

    Write-BuildLog "Publishing Managed State Keeper for $RuntimeIdentifier..."
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish Managed State Keeper for $RuntimeIdentifier"
    }

    if (-not (Test-Path (Join-Path $OutputPath $GuiExecutableName))) {
        throw "Expected $GuiExecutableName in $OutputPath"
    }

    if (-not (Publish-AppResources -Arch $Arch -OutputDir (Resolve-Path $OutputPath).Path -AppProjectDir (Join-Path $SrcDir 'App') -Config $config)) {
        throw "Could not generate resources.pri for Managed State Keeper ($Arch); the app cannot load its XAML without it"
    }

    Get-ChildItem -Path $OutputPath -Filter "*.pdb" -Recurse | Remove-Item -Force
    Write-BuildLog "Managed State Keeper ($RuntimeIdentifier) built successfully" -Level 'SUCCESS'
}

# Generates resources.pri and copies the compiled XAML (.xbf) into the publish output.
#
# EnableCoreMrtTooling is off in StartSet.App.csproj because the MSBuild PRI step needs
# the Visual Studio UWP workload, which the build machines do not have. This does what
# that step would: stage the .xbf files with the WinUI framework .pri files and merge
# them into one resources.pri with makepri.exe from the Windows SDK. Same procedure as
# the BootstrapMate GUI.
function Publish-AppResources {
    param(
        [Parameter(Mandatory)][string]$Arch,
        [Parameter(Mandatory)][string]$OutputDir,
        [Parameter(Mandatory)][string]$AppProjectDir,
        [Parameter(Mandatory)][string]$Config
    )

    Write-BuildLog "Generating XAML resources (XBF + resources.pri) for Managed State Keeper ($Arch)..."

    # makepri.exe runs on the build host, so prefer the host architecture's copy.
    $hostArch = switch ($env:PROCESSOR_ARCHITECTURE) {
        'AMD64' { 'x64' }
        'ARM64' { 'arm64' }
        default { 'x86' }
    }
    $toolArchOrder = @($hostArch) + (@('x64', 'arm64', 'x86') | Where-Object { $_ -ne $hostArch })
    $sdkBinRoots = @(
        "$env:ProgramFiles\Windows Kits\10\bin",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }

    $makepri = $null
    foreach ($root in $sdkBinRoots) {
        foreach ($toolArch in $toolArchOrder) {
            $candidate = Get-ChildItem "$root\*\$toolArch\makepri.exe" -ErrorAction SilentlyContinue |
                Sort-Object { [version]($_.FullName -replace '.*\\(\d+\.\d+\.\d+\.\d+)\\.*', '$1') } -Descending |
                Select-Object -First 1
            if ($candidate) { $makepri = $candidate.FullName; break }
        }
        if ($makepri) { break }
    }

    if (-not $makepri) {
        Write-BuildLog "makepri.exe not found; install the Windows 10/11 SDK" "ERROR"
        return $false
    }

    $xbfFiles = Get-ChildItem "$AppProjectDir\obj\$Config" -Recurse -Filter "*.xbf" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match [regex]::Escape("\win-$Arch\") }
    if (-not $xbfFiles) {
        Write-BuildLog "No .xbf files under obj\$Config for win-$Arch" "ERROR"
        return $false
    }

    $xbfRootPath = ($xbfFiles[0].FullName -split [regex]::Escape("\win-$Arch\"))[0] + "\win-$Arch"
    $stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "startset-pri-$Arch"
    if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
    New-Item -ItemType Directory $stagingDir | Out-Null

    # resources.pri maps resources by relative path (App.xbf, Views\RunPage.xbf), resolved
    # beside the executable at run time, so each .xbf goes to staging and to the output.
    foreach ($xbf in $xbfFiles) {
        $relativePath = $xbf.FullName.Substring($xbfRootPath.Length).TrimStart('\')
        foreach ($destRoot in @($stagingDir, $OutputDir)) {
            $dest = Join-Path $destRoot $relativePath
            $destDir = Split-Path $dest
            if (-not (Test-Path $destDir)) { New-Item -ItemType Directory $destDir | Out-Null }
            Copy-Item $xbf.FullName $dest -Force
        }
    }

    # The framework .pri files carry the WinUI theme resources; makepri merges them in.
    $frameworkPris = Get-ChildItem $OutputDir -Filter "Microsoft.*.pri"
    foreach ($pri in $frameworkPris) {
        Copy-Item $pri.FullName (Join-Path $stagingDir $pri.Name) -Force
    }

    $priconfigPath = Join-Path $stagingDir "priconfig.xml"
    & $makepri createconfig /cf $priconfigPath /dq "en-US" /pv "10.0.0" /o 2>&1 | Out-Null

    $outPriPath = Join-Path $OutputDir "resources.pri"
    $priOutput = & $makepri new /pr $stagingDir /cf $priconfigPath /in "StartSet" /of $outPriPath /o 2>&1
    $priExit = $LASTEXITCODE
    Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue

    if ($priExit -ne 0) {
        Write-BuildLog "makepri.exe failed (exit $priExit): $priOutput" "ERROR"
        return $false
    }

    Write-BuildLog "Generated resources.pri: $($xbfFiles.Count) XBF + $($frameworkPris.Count) framework PRI(s)" "SUCCESS"
    return $true
}

# Copies the published GUI folder into a package payload, beside the CLI and service.
function Copy-GuiPayload {
    param(
        [string]$BinDir,
        [string]$PayloadDir
    )

    $guiDir = Join-Path $BinDir 'gui'
    if (-not (Test-Path (Join-Path $guiDir $GuiExecutableName))) {
        throw "Managed State Keeper is missing from $guiDir; build the binaries first"
    }

    Copy-Item -Path (Join-Path $guiDir '*') -Destination $PayloadDir -Recurse -Force
    Write-BuildLog "Copied Managed State Keeper to payload" "INFO"
}

#endregion

#region Signing Functions

function Invoke-SignAllBinaries {
    param(
        [string]$Thumbprint,
        [string]$CertStore
    )
    
    Write-BuildLog "Signing all executables..."
    Test-SignTool
    
    # Force garbage collection to release file handles
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    Start-Sleep -Seconds 2
    
    $archs = if ($Architecture -eq 'both') { @('x64', 'arm64') } elseif ($Architecture -eq 'x64') { @('x64') } else { @('arm64') }
    
    foreach ($arch in $archs) {
        $archDir = Join-Path $OutputDir $arch
        $exeFiles = @(Get-ChildItem -Path $archDir -Filter "*.exe" -File -ErrorAction SilentlyContinue)
        $guiExe = Join-Path $archDir "gui\$GuiExecutableName"
        if (Test-Path $guiExe) { $exeFiles += Get-Item $guiExe }

        foreach ($exe in $exeFiles) {
            try {
                Invoke-SignArtifact -Path $exe.FullName -Thumbprint $Thumbprint -Store $CertStore
            }
            catch {
                Write-BuildLog "Failed to sign $($exe.Name): $_" -Level 'WARNING'
            }
        }
    }
    
    Write-BuildLog "Binary signing complete" -Level 'SUCCESS'
}

#endregion

#region Packaging Functions

# Every package -- .msi, .pkg, .nupkg and .intunewin -- is built by cimipkg from one
# staged project: the same payload (CLI, service and Managed State Keeper), the same
# install scripts from scripts/, and the same build-info.yaml. They used to be staged
# separately, and the .nupkg and .intunewin carried only the two exes.
function New-PackageProject {
    param(
        [Parameter(Mandatory)][string]$Arch,
        [Parameter(Mandatory)][hashtable]$Version,
        [Parameter(Mandatory)][string]$Format
    )

    $binDir = Join-Path $OutputDir $Arch
    if (-not (Test-Path $binDir)) {
        throw "Binary directory not found: $binDir"
    }

    $projectDir = Join-Path $OutputDir "${Format}_$Arch"
    if (Test-Path $projectDir) {
        Remove-Item $projectDir -Recurse -Force
    }
    $payloadDir = Join-Path $projectDir "payload"
    $scriptsDir = Join-Path $projectDir "scripts"
    New-Item -ItemType Directory -Path $payloadDir, $scriptsDir -Force | Out-Null

    foreach ($binary in @("managedstatekeeper.exe", "StartSetService.exe")) {
        $sourcePath = Join-Path $binDir $binary
        if (-not (Test-Path $sourcePath)) {
            throw "Binary not found: $sourcePath"
        }
        Copy-Item $sourcePath $payloadDir -Force
    }
    Copy-GuiPayload -BinDir $binDir -PayloadDir $payloadDir

    foreach ($script in @("preinstall.ps1", "postinstall.ps1")) {
        $source = Join-Path $InstallScriptsDir $script
        if (Test-Path $source) {
            Copy-Item $source (Join-Path $scriptsDir $script) -Force
        } else {
            Write-BuildLog "Install script not found: $source" "WARNING"
        }
    }

    $buildInfoTemplatePath = Join-Path $BuildDir "pkg\build-info.yaml"
    if (-not (Test-Path $buildInfoTemplatePath)) {
        throw "build-info.yaml template not found: $buildInfoTemplatePath"
    }
    $buildInfo = Get-Content $buildInfoTemplatePath -Raw
    $buildInfo = $buildInfo -replace '\{\{VERSION\}\}', $Version.Full
    $buildInfo = $buildInfo -replace '\{\{ARCHITECTURE\}\}', $Arch
    $buildInfo | Set-Content (Join-Path $projectDir "build-info.yaml") -Encoding UTF8

    Write-BuildLog "Staged $Format project for $Arch" "INFO"
    return $projectDir
}

# Builds one package with cimipkg and moves each output named in $Extensions to the
# release folder as StartSet-<version>-<arch>.<ext>. Returns the moved paths.
function Invoke-CimiPkgBuild {
    param(
        [Parameter(Mandatory)][string]$Arch,
        [Parameter(Mandatory)][hashtable]$Version,
        [Parameter(Mandatory)][string]$Format,
        [string[]]$FormatArgs = @(),
        [Parameter(Mandatory)][string[]]$Extensions,
        [switch]$Sign,
        [string]$Thumbprint
    )

    if (-not (Test-CimiPkg)) {
        Write-BuildLog "cimipkg.exe not found. Build CimianTools first or add cimipkg to PATH." "ERROR"
        return @()
    }
    $cimipkgPath = Get-CimiPkgPath

    $projectDir = $null
    try {
        $projectDir = New-PackageProject -Arch $Arch -Version $Version -Format $Format

        $cimipkgArgs = @("--verbose", "--skip-import") + $FormatArgs
        if ($Sign -and $Thumbprint) {
            $cimipkgArgs += @("--sign-thumbprint", $Thumbprint)
        }
        $cimipkgArgs += "`"$projectDir`""

        Write-BuildLog "Building $Format for $Arch with cimipkg..." "INFO"
        $process = Start-Process -FilePath $cimipkgPath -ArgumentList $cimipkgArgs -Wait -NoNewWindow -PassThru
        if ($process.ExitCode -ne 0) {
            Write-BuildLog "cimipkg failed for $Format ($Arch) with exit code $($process.ExitCode)" "ERROR"
            return @()
        }

        $built = @()
        foreach ($extension in $Extensions) {
            $created = Get-ChildItem -Path (Join-Path $projectDir "build") -Filter "*.$extension" -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if (-not $created) {
                Write-BuildLog "No .$extension in cimipkg output for $Arch" "WARNING"
                continue
            }
            $finalPath = Join-Path $OutputDir "StartSet-$($Version.Full)-$Arch.$extension"
            Move-Item $created.FullName $finalPath -Force
            $size = (Get-Item $finalPath).Length / 1MB
            Write-BuildLog "Created $(Split-Path $finalPath -Leaf) ($($size.ToString('F2')) MB)" "SUCCESS"
            $built += $finalPath
        }
        return $built
    }
    catch {
        Write-BuildLog "Failed to create $Format package for ${Arch}: $_" "ERROR"
        return @()
    }
    finally {
        if ($projectDir) {
            Remove-Item $projectDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Build-MsiPackage {
    param(
        [string]$Arch,
        [hashtable]$Version,
        [switch]$Sign,
        [string]$Thumbprint,
        [string]$CertStore
    )

    $built = Invoke-CimiPkgBuild -Arch $Arch -Version $Version -Format 'msi' -Extensions @('msi') -Sign:$Sign -Thumbprint $Thumbprint
    return $built | Select-Object -First 1
}

function Build-PkgPackage {
    param(
        [Parameter(Mandatory)][string]$Arch,
        [Parameter(Mandatory)][hashtable]$Version,
        [switch]$Sign,
        [string]$Thumbprint,
        [string]$Store
    )

    $built = Invoke-CimiPkgBuild -Arch $Arch -Version $Version -Format 'pkg' -FormatArgs @('--pkg') -Extensions @('pkg') -Sign:$Sign -Thumbprint $Thumbprint
    return $built | Select-Object -First 1
}

function Build-NuGetPackage {
    param(
        [string]$Arch,
        [hashtable]$Version,
        [switch]$Sign,
        [string]$Thumbprint
    )

    $built = Invoke-CimiPkgBuild -Arch $Arch -Version $Version -Format 'nupkg' -FormatArgs @('--nupkg') -Extensions @('nupkg') -Sign:$Sign -Thumbprint $Thumbprint
    return $built | Select-Object -First 1
}

# The .intunewin wraps the MSI, built from the same staged project.
function Build-IntuneWinPackage {
    param(
        [string]$Arch,
        [hashtable]$Version,
        [switch]$Sign,
        [string]$Thumbprint
    )

    $built = Invoke-CimiPkgBuild -Arch $Arch -Version $Version -Format 'intunewin' -FormatArgs @('--intunewin') -Extensions @('intunewin') -Sign:$Sign -Thumbprint $Thumbprint
    return $built | Select-Object -First 1
}

#endregion

#region Installation Functions

function Install-MsiPackage {
    param([string]$MsiPath)
    
    if (-not (Test-Path $MsiPath)) {
        Write-BuildLog "MSI package not found: $MsiPath" "ERROR"
        return $false
    }
    
    Write-BuildLog "Installing MSI package: $MsiPath" "INFO"
    
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]"Administrator")
    
    $absoluteMsiPath = (Resolve-Path $MsiPath).Path
    
    if ($isAdmin) {
        $installProcess = Start-Process -FilePath "msiexec.exe" `
            -ArgumentList "/i", "`"$absoluteMsiPath`"", "/qn", "/l*v", "`"$env:TEMP\startset_install.log`"" `
            -Wait -PassThru
            
        if ($installProcess.ExitCode -eq 0) {
            Write-BuildLog "MSI installation completed successfully" "SUCCESS"
            return $true
        }
        else {
            Write-BuildLog "MSI installation failed with exit code $($installProcess.ExitCode)" "ERROR"
            return $false
        }
    }
    else {
        # Try sudo if available
        if (Get-Command "sudo" -ErrorAction SilentlyContinue) {
            Write-BuildLog "Using sudo for elevated installation..." "INFO"
            $sudoProcess = Start-Process -FilePath "sudo" `
                -ArgumentList "msiexec.exe", "/i", "`"$absoluteMsiPath`"", "/qn" `
                -Wait -PassThru
                
            if ($sudoProcess.ExitCode -eq 0) {
                Write-BuildLog "MSI installation completed via sudo" "SUCCESS"
                return $true
            }
        }
        
        Write-BuildLog "Administrator privileges required for installation" "ERROR"
        return $false
    }
}

#endregion

#region Development Mode Functions

function Enter-DevelopmentMode {
    Write-BuildLog "Development mode enabled - preparing for rapid iteration..." "INFO"
    
    # Stop StartSet service
    $services = @("StartSet")
    foreach ($serviceName in $services) {
        try {
            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($service -and $service.Status -eq "Running") {
                Write-BuildLog "Stopping service: $serviceName" "INFO"
                Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            }
        }
        catch {
            Write-BuildLog "Could not stop service $serviceName" "WARNING"
        }
    }
    
    # Kill running processes
    $processes = @("startset", "StartSetService")
    foreach ($processName in $processes) {
        try {
            Get-Process -Name $processName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Write-BuildLog "Stopped $processName process" "INFO"
        }
        catch {
            # Normal if process not running
        }
    }
    
    Write-BuildLog "Development mode preparation complete" "SUCCESS"
}

#endregion

#region Summary Functions

function Show-BuildSummary {
    param([hashtable]$Version)
    
    Write-Host ""
    Write-Host "===============================================================" -ForegroundColor Cyan
    Write-Host "                      BUILD SUMMARY                            " -ForegroundColor Cyan
    Write-Host "===============================================================" -ForegroundColor Cyan
    Write-Host ""
    
    Write-Host "Version:       " -NoNewline; Write-Host $Version.Full -ForegroundColor Yellow
    Write-Host "Configuration: " -NoNewline; Write-Host $(if ($Dev) { 'Debug' } else { $Configuration }) -ForegroundColor Yellow
    Write-Host "Architecture:  " -NoNewline; Write-Host $Architecture -ForegroundColor Yellow
    Write-Host "Output:        " -NoNewline; Write-Host $OutputDir -ForegroundColor Yellow
    Write-Host ""
    
    # List built files
    if (Test-Path $OutputDir) {
        Write-Host "Built Artifacts:" -ForegroundColor Green
        Get-ChildItem -Path $OutputDir -Recurse -Include "*.exe","*.msi","*.nupkg","*.intunewin","*.pkg" | ForEach-Object {
            $relativePath = $_.FullName.Replace($OutputDir, '').TrimStart('\')
            $size = [math]::Round($_.Length / 1MB, 1)
            Write-Host "  - $relativePath ($size MB)" -ForegroundColor White
        }
    }
    
    Write-Host ""
    Write-Host "===============================================================" -ForegroundColor Cyan
}

#endregion

#region Main Execution

try {
    $startTime = Get-Date
    $version = Get-BuildVersion
    
    Write-Host ""
    Write-Host "===============================================================" -ForegroundColor Cyan
    Write-Host "          STARTSET BUILD SYSTEM                                " -ForegroundColor Cyan
    Write-Host "          Version: $($version.Full)                            " -ForegroundColor Cyan
    Write-Host "===============================================================" -ForegroundColor Cyan
    Write-Host ""
    
    # Handle development mode
    if ($Dev) {
        Enter-DevelopmentMode
        $NoSign = $true  # Development mode skips signing
    }
    
    # Handle signing configuration
    $shouldSign = -not $NoSign
    $actualThumbprint = ""
    $certStore = "CurrentUser"
    
    if ($Thumbprint) {
        $actualThumbprint = $Thumbprint
        Write-BuildLog "Using provided certificate thumbprint" "INFO"
    }
    elseif ($shouldSign) {
        $certInfo = Get-SigningCertThumbprint -ProvidedThumbprint $Thumbprint
        if ($certInfo) {
            $actualThumbprint = $certInfo.Thumbprint
            $certStore = $certInfo.Store
            Write-BuildLog "Enterprise certificate auto-detected: $actualThumbprint" "SUCCESS"
        }
        else {
            Write-BuildLog "No enterprise certificate found - binaries will be unsigned" "WARNING"
            $shouldSign = $false
        }
    }
    else {
        Write-BuildLog "Signing disabled - binaries will be unsigned" "WARNING"
    }
    
    # Validate conflicting flags
    if ($SignMSI -and ($Binaries -or $Install -or $IntuneWin -or $Dev -or $PackageOnly -or $NupkgOnly -or $MsiOnly -or $PkgOnly)) {
        Write-BuildLog "SignMSI cannot be used with other build flags" "ERROR"
        exit 1
    }
    
    if ($PkgOnly -and ($MsiOnly -or $NupkgOnly)) {
        Write-BuildLog "PkgOnly cannot be combined with MsiOnly or NupkgOnly" "ERROR"
        exit 1
    }
    
    # Handle SignMSI mode
    if ($SignMSI) {
        Write-BuildLog "SignMSI mode - signing existing MSI files..." "INFO"
        
        if (-not $shouldSign) {
            throw "Cannot sign MSI files without a valid certificate."
        }
        
        Test-SignTool
        
        $msiFiles = Get-ChildItem -Path $OutputDir -Filter "*.msi" -File -ErrorAction SilentlyContinue
        
        if ($msiFiles.Count -eq 0) {
            Write-BuildLog "No MSI files found in release directory." "WARNING"
            exit 0
        }
        
        foreach ($msi in $msiFiles) {
            Invoke-SignArtifact -Path $msi.FullName -Thumbprint $actualThumbprint -Store $certStore
        }
        
        Write-BuildLog "SignMSI completed." "SUCCESS"
        exit 0
    }
    
    # Initialize build environment
    Initialize-BuildEnvironment
    
    # Clean if requested
    if ($Clean) {
        Invoke-Clean
    }
    
    # Build phase
    if (-not ($PackageOnly -or $NupkgOnly -or $MsiOnly -or $PkgOnly)) {
        # Build solution first
        Build-Solution
        
        # Build binaries
        Build-AllBinaries -Version $version
    }
    
    # Signing phase
    if ($shouldSign -and -not ($PackageOnly -or $NupkgOnly -or $MsiOnly -or $PkgOnly)) {
        Invoke-SignAllBinaries -Thumbprint $actualThumbprint -CertStore $certStore
    }
    
    # Early exit for binaries-only mode
    if ($Binaries) {
        Show-BuildSummary -Version $version
        $elapsed = (Get-Date) - $startTime
        Write-BuildLog "Build completed in $($elapsed.TotalSeconds.ToString('F1')) seconds" -Level 'SUCCESS'
        exit 0
    }
    
    # Run tests if requested
    if ($Test) {
        Write-BuildLog "Running tests..." "INFO"
        $solutionPath = Join-Path $RootDir 'StartSet.sln'
        & dotnet test $solutionPath --configuration $Configuration --no-build --verbosity minimal
        
        if ($LASTEXITCODE -ne 0) {
            throw "Tests failed"
        }
        Write-BuildLog "All tests passed" "SUCCESS"
    }
    
    # Packaging phase
    $archs = if ($Architecture -eq 'both') { @('x64', 'arm64') } elseif ($Architecture -eq 'x64') { @('x64') } else { @('arm64') }
    
    # MSI packages (unless skipped)
    if (-not $SkipMSI -and -not $NupkgOnly -and -not $PkgOnly) {
        foreach ($arch in $archs) {
            $msiPath = Build-MsiPackage -Arch $arch -Version $version -Sign:$shouldSign -Thumbprint $actualThumbprint -CertStore $certStore
        }
    }
    
    # NuGet packages (unless skipped)
    if (-not $MsiOnly -and -not $PkgOnly) {
        foreach ($arch in $archs) {
            $nupkgPath = Build-NuGetPackage -Arch $arch -Version $version -Sign:$shouldSign -Thumbprint $actualThumbprint
        }
    }
    
    # .pkg packages (unless skipped)
    if (-not $MsiOnly -and -not $NupkgOnly) {
        foreach ($arch in $archs) {
            $pkgParams = @{
                Arch = $arch
                Version = $version
                Sign = $shouldSign
            }
            if ($actualThumbprint) {
                $pkgParams['Thumbprint'] = $actualThumbprint
                $pkgParams['Store'] = $certStore
            }
            Build-PkgPackage @pkgParams
        }
    }
    
    # IntuneWin packages (if requested)
    if ($IntuneWin) {
        foreach ($arch in $archs) {
            $null = Build-IntuneWinPackage -Arch $arch -Version $version -Sign:$shouldSign -Thumbprint $actualThumbprint
        }
    }
    
    # Installation (if requested)
    if ($Install) {
        Write-BuildLog "Install flag detected - installing MSI package..." "INFO"
        
        # Stop services before installation
        Enter-DevelopmentMode
        
        $currentArch = if ($env:PROCESSOR_ARCHITECTURE -eq "AMD64") { "x64" } else { "arm64" }
        $msiToInstall = Join-Path $OutputDir "StartSet-$($version.Full)-$currentArch.msi"
        
        if (-not (Test-Path $msiToInstall)) {
            $msiToInstall = Get-ChildItem -Path $OutputDir -Filter "StartSet-*-$currentArch.msi" | Select-Object -First 1
            if ($msiToInstall) { $msiToInstall = $msiToInstall.FullName }
        }
        
        if ($msiToInstall -and (Test-Path $msiToInstall)) {
            $installSuccess = Install-MsiPackage -MsiPath $msiToInstall
            if ($installSuccess) {
                Write-BuildLog "StartSet has been successfully installed!" "SUCCESS"
            }
        }
        else {
            Write-BuildLog "No MSI package found for installation" "ERROR"
        }
    }
    
    # Summary
    Show-BuildSummary -Version $version
    
    $elapsed = (Get-Date) - $startTime
    Write-BuildLog "Build completed in $($elapsed.TotalSeconds.ToString('F1')) seconds" -Level 'SUCCESS'
}
catch {
    Write-BuildLog "Build failed: $_" -Level 'ERROR'
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    exit 1
}

#endregion
