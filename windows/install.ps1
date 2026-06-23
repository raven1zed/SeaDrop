# SeaDrop Windows install script
# Self-signed certs work when installed to LocalMachine\TrustedPeople first.
# Without this the wiFiControl capability is denied and hotspot won't start.

param(
    [string]$MsixPath = "$PSScriptRoot\SeaDrop-v1.4.0.msix",
    [string]$CertPath = "$PSScriptRoot\SeaDrop_TemporaryKey.pfx",
    [string]$CertPassword = "seadrop"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $MsixPath)) {
    Write-Error "MSIX not found: $MsixPath"
    exit 1
}
if (-not (Test-Path $CertPath)) {
    Write-Error "Cert not found: $CertPath"
    exit 1
}

# Need admin to install to TrustedPeople and to register the package.
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Re-launching as Administrator..."
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    Start-Process -FilePath PowerShell -ArgumentList $args -Verb RunAs
    exit
}

# Export public part of the PFX to .cer
$cerPath = [System.IO.Path]::ChangeExtension($CertPath, ".cer")
Write-Host "Exporting public certificate to $cerPath..."
$pfx = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    (Resolve-Path $CertPath), $CertPassword,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet)
[System.IO.File]::WriteAllBytes($cerPath, $pfx.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

# Install into TrustedPeople so wiFiControl (restricted capability) is honoured
Write-Host "Installing certificate into LocalMachine\TrustedPeople..."
Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

# Remove any previous install
$existing = Get-AppxPackage -Name "SeaDrop" -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Removing previous SeaDrop install..."
    Remove-AppxPackage $existing.PackageFullName | Out-Null
}

# Install the MSIX
Write-Host "Installing SeaDrop MSIX..."
Add-AppxPackage -Path $MsixPath -AllowUnsigned -ErrorAction Stop

# Resolve installed app for shortcut creation
$pkg = Get-AppxPackage -Name "SeaDrop" | Select-Object -First 1
if ($pkg) {
    $appUserModelId = "$($pkg.PackageFamilyName)!App"

    # ── Desktop shortcut ────────────────────────────────────────
    $shell = New-Object -ComObject WScript.Shell
    $desktop = [Environment]::GetFolderPath("Desktop")
    $lnk = Join-Path $desktop "SeaDrop.lnk"
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = "explorer.exe"
    $shortcut.Arguments = "shell:AppsFolder\$appUserModelId"
    $shortcut.WorkingDirectory = $desktop
    $shortcut.IconLocation = "$($pkg.InstallLocation)\SeaDrop.ico"
    $shortcut.Description = "SeaDrop - Seamless Drop"
    $shortcut.Save()
    Write-Host "Desktop shortcut created."

    # ── Start Menu shortcut ────────────────────────────────────
    $startMenu = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\SeaDrop.lnk"
    $smShortcut = $shell.CreateShortcut($startMenu)
    $smShortcut.TargetPath = "explorer.exe"
    $smShortcut.Arguments = "shell:AppsFolder\$appUserModelId"
    $smShortcut.WorkingDirectory = $desktop
    $smShortcut.IconLocation = "$($pkg.InstallLocation)\SeaDrop.ico"
    $smShortcut.Save()
    Write-Host "Start Menu shortcut created."
}

Write-Host ""
Write-Host "SeaDrop installed. Look for it on Start Menu, Desktop, and the system tray." -ForegroundColor Green
