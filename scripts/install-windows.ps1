# ==============================================================================
# OpenScan Engine - Script d'installation du Service Windows
# ==============================================================================
# Exécuter dans une console PowerShell en tant qu'Administrateur :
#   PowerShell -ExecutionPolicy Bypass -File .\scripts\install-windows.ps1
# ==============================================================================

[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\OpenScan\Agent",
    [string]$DataDir = "$env:ProgramData\OpenScan",
    [string]$SourceDir = "",
    [string]$ServiceName = "OpenScanAgent",
    [string]$DisplayName = "OpenScan Engine Agent"
)

# 1. Vérification des droits Administrateur
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "[ERREUR] Ce script doit être exécuté avec des privilèges Administrateur."
    exit 1
}

Write-Host "=== Installation du Service Windows $ServiceName ===" -ForegroundColor Cyan

# 2. Création des répertoires
Write-Host "-> Création des dossiers..." -ForegroundColor Yellow
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
if (-not (Test-Path $DataDir)) {
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
}

# 3. Déploiement des binaires
if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoRoot = Split-Path -Parent $scriptDir
    $candidates = @(
        "$repoRoot\src\OpenScan.Agent\bin\Release\net8.0\win-x64\publish",
        "$repoRoot\src\OpenScan.Agent\bin\Release\net8.0\publish",
        "$repoRoot\src\OpenScan.Agent\bin\Release\net8.0",
        "$repoRoot\src\OpenScan.Agent\bin\Debug\net8.0"
    )
    foreach ($c in $candidates) {
        if (Test-Path "$c\OpenScan.Agent.exe") {
            $SourceDir = $c
            break
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($SourceDir) -and (Test-Path "$SourceDir\OpenScan.Agent.exe")) {
    Write-Host "-> Copie des fichiers depuis $SourceDir vers $InstallDir..." -ForegroundColor Yellow
    Copy-Item -Path "$SourceDir\*" -Destination $InstallDir -Recurse -Force
} else {
    Write-Warning "Aucun binaire précompilé trouvé. Veuillez publier le projet via :"
    Write-Warning "  dotnet publish src\OpenScan.Agent -c Release -r win-x64 --self-contained -o `"$InstallDir`""
}

$exePath = Join-Path $InstallDir "OpenScan.Agent.exe"
if (-not (Test-Path $exePath)) {
    Write-Error "[ERREUR] $exePath est introuvable. Installation interrompue."
    exit 1
}

# 4. Enregistrement du service Windows avec sc.exe
Write-Host "-> Enregistrement du service $ServiceName..." -ForegroundColor Yellow
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "-> Arrêt du service existant..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Création du service avec démarrage automatique
sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto DisplayName= "`"$DisplayName`"" | Out-Null

# Description
sc.exe description $ServiceName "Moteur d'acquisition et agent de numérisation OpenScan Engine (arrière-plan permanent)." | Out-Null

# Configuration du redémarrage automatique en cas de crash :
# Redémarrage après 5 secondes sur 1ère, 2ème et suivantes pannes (reset compteur après 86400s / 1 jour)
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

# 5. Démarrage du service
Write-Host "-> Démarrage du service $ServiceName..." -ForegroundColor Yellow
Start-Service -Name $ServiceName

$status = Get-Service -Name $ServiceName
Write-Host "=== Service $ServiceName installé et démarré avec succès (Statut: $($status.Status)) ===" -ForegroundColor Green
Write-Host "Consultez l'Observateur d'événements Windows (Journaux des applications) pour les logs." -ForegroundColor Green
