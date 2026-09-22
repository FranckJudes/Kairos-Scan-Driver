# ==============================================================================
# OpenScan Engine - Script de désinstallation du Service Windows
# ==============================================================================
# Exécuter dans une console PowerShell en tant qu'Administrateur :
#   PowerShell -ExecutionPolicy Bypass -File .\scripts\uninstall-windows.ps1
# ==============================================================================

[CmdletBinding()]
param(
    [string]$ServiceName = "OpenScanAgent",
    [string]$InstallDir = "C:\Program Files\OpenScan\Agent",
    [string]$DataDir = "$env:ProgramData\OpenScan",
    [switch]$PurgeData = $false
)

# 1. Vérification des droits Administrateur
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "[ERREUR] Ce script doit être exécuté avec des privilèges Administrateur."
    exit 1
}

Write-Host "=== Désinstallation du Service Windows $ServiceName ===" -ForegroundColor Cyan

# 2. Arrêt du service
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq 'Running') {
        Write-Host "-> Arrêt du service $ServiceName..." -ForegroundColor Yellow
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }

    Write-Host "-> Suppression du service $ServiceName..." -ForegroundColor Yellow
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
} else {
    Write-Host "-> Le service $ServiceName n'est pas enregistré." -ForegroundColor Yellow
}

# 3. Suppression des fichiers d'installation
if (Test-Path $InstallDir) {
    Write-Host "-> Suppression du dossier d'installation $InstallDir..." -ForegroundColor Yellow
    Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
}

# 4. Traitement du dossier de données
if ($PurgeData) {
    if (Test-Path $DataDir) {
        Write-Host "-> Suppression du dossier de données $DataDir (-PurgeData)..." -ForegroundColor Yellow
        Remove-Item -Path $DataDir -Recurse -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Host "-> Le dossier de données $DataDir (base SQLite, pages) est conservé." -ForegroundColor Green
    Write-Host "   Pour le supprimer, relancez avec -PurgeData ou supprimez-le manuellement." -ForegroundColor Green
}

Write-Host "=== Désinstallation de $ServiceName terminée avec succès ===" -ForegroundColor Green
