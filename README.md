# Kairos-SCan Engine

Moteur local de capture documentaire : permet à une application Web d'accéder à des scanners
physiques (TWAIN, WIA, SANE, eSCL) via une **API locale normalisée**, sans jamais connaître la
technologie d'acquisition réelle du matériel.

Implémentation du cahier des charges technique
[`KAIROS-SCAN - CAHIER DES CHARGES TECHNIQUE.pdf`](./KAIROS-SCAN%20-%20CAHIER%20DES%20CHARGES%20TECHNIQUE.pdf)
(à la racine du dépôt).

## Principe

```
Application Web
      │  HTTPS / WebSocket
      ▼
OpenScan Agent (.NET 8, local, 127.0.0.1 uniquement)
      │
      ▼
IScannerBackend (abstraction commune)
      │
  ┌───┼──────┬──────────┬──────────────────┐
 Mock      SANE        WIA          TWAIN (worker process isolé)
(virtuel)  (Linux,    (Windows,     (Windows, DSM natif via P/Invoke,
           scanimage)  COM tardif)   crash-isolé de l'Agent)
```

Le navigateur ne pilote jamais un scanner directement (règle R1 du cahier des charges) ; le cœur
du moteur ne contient aucune logique spécifique à un constructeur (règle R2).

## État des backends

| Backend | Implémenté | Testé sur matériel réel |
|---|---|---|
| Mock | ✅ | N/A (scanner virtuel) |
| SANE (Linux) | ✅ | ✅ Fujitsu fi-7030 |
| WIA (Windows) | ✅ | ✅ Fujitsu fi-7030 |
| TWAIN (Windows, worker x86 self-contained isolé) | ✅ | ✅ Fujitsu fi-7030 (pilote PaperStream IP) |
| eSCL (réseau, HTTP/XML) | ✅ | ⚠️ Validé contre un faux serveur HTTP imitant le protocole, jamais contre un vrai périphérique eSCL |
| ICA (macOS) | ❌ non implémenté | — |

Conversion JPEG temps réel par page, export PDF à la demande, et reprise/retry de job (avec
réconciliation automatique après un crash de l'Agent) sont implémentés (voir
[API REST](#api-rest-apiv1)). Traitement d'image avancé (rotation, deskew, crop, OCR…) et
sécurité de session (tokens, origine, DNS rebinding) **ne sont pas encore implémentés** — voir
[Limitations connues](#limitations-connues--feuille-de-route).

## Prérequis

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Linux** : `sane-utils` installé (fournit `scanimage`) pour le backend SANE.
- **Windows** :
  - WIA : rien de plus, la couche Automation WIA est native à Windows.
  - TWAIN : le worker (`OpenScan.Twain.Worker`) est publié self-contained x86, aucun runtime .NET
    supplémentaire à installer — mais il faut qu'un **DSM/pilote TWAIN** du fabricant soit
    installé sur la machine (ex. PaperStream IP pour les scanners Fujitsu).
- **eSCL** : aucune découverte automatique (mDNS) pour l'instant — déclarer les adresses des
  scanners/imprimantes réseau connus dans `Escl:KnownDevices` (`appsettings.json`).
## Installation via package

Des installeurs prêts à l'emploi sont publiés automatiquement à chaque release sur la page
[**GitHub Releases**](https://github.com/FranckJudes/Kairos-Scan-Driver/releases) :

### Windows (.exe)

1. Télécharger `OpenScanEngineSetup-X.Y.Z-x64.exe` depuis la page Releases.
2. Double-cliquer l'installeur (droits administrateur requis).
3. L'installeur copie les fichiers, crée le service Windows `OpenScanAgent` avec démarrage
   automatique au boot et redémarrage automatique en cas de crash, puis le démarre.
4. Désinstallation : via « Ajout/Suppression de programmes » Windows.

> **Note** : L'installeur n'est pas signé numériquement (SmartScreen affichera un avertissement
> « Éditeur inconnu »). Cliquer « Plus d'infos » → « Exécuter quand même ».

### Linux / Debian (.deb)

```bash
# Télécharger le paquet depuis la page Releases, puis :
sudo apt install ./openscan-agent_X.Y.Z_amd64.deb
```

Le paquet crée automatiquement l'utilisateur système `openscan` (avec accès aux groupes `scanner`
et `lp`), le répertoire de données `/var/lib/openscan`, installe et démarre le service systemd
`openscan-agent` avec redémarrage automatique en cas de crash.

```bash
# Vérification
systemctl status openscan-agent
journalctl -u openscan-agent -f

# Désinstallation (conserve les données) :
sudo apt remove openscan-agent

# Désinstallation complète (supprime les données et l'utilisateur) :
sudo apt purge openscan-agent
```

### Alternative : installation manuelle (sans package)

Les scripts `scripts/install-linux.sh` et `scripts/install-windows.ps1` restent utilisables
pour une installation depuis les sources compilées manuellement :

```bash
# Linux
dotnet publish src/OpenScan.Agent -c Release -r linux-x64 --self-contained -o /opt/openscan-agent
sudo scripts/install-linux.sh

# Windows (PowerShell administrateur)
dotnet publish src/OpenScan.Agent -c Release -r win-x64 --self-contained
PowerShell -ExecutionPolicy Bypass -File scripts/install-windows.ps1
```

## Démarrage rapide (développement)

```bash
dotnet build OpenScan.sln
dotnet test tests/OpenScan.Tests/OpenScan.Tests.csproj
dotnet run --project src/OpenScan.Agent
```

L'Agent écoute par défaut sur `http://127.0.0.1:5276` (profil `http` de
`src/OpenScan.Agent/Properties/launchSettings.json`) et n'est **jamais** accessible depuis le
réseau (règle R10). En environnement de développement, Swagger est disponible sur `/swagger`.

## API REST (`/api/v1`)

| Méthode | Route | Description |
|---|---|---|
| GET | `/health` | Vérification de vie de l'Agent |
| GET | `/scanners` | Découverte des scanners (tous backends confondus) |
| GET | `/scanners/{id}` | Détail d'un scanner connu |
| GET | `/scanners/{id}/capabilities` | Capacités réelles du scanner (résolutions, sources, duplex…) |
| GET | `/profiles` | Liste des profils de numérisation enregistrés |
| POST | `/profiles` | Crée un profil |
| GET / PUT / DELETE | `/profiles/{id}` | Lecture / mise à jour / suppression d'un profil |
| POST | `/scans` | Crée et démarre un job (`{"scannerId", "profile"}` ou `{"scannerId", "profileId"}`) |
| GET | `/scans/{id}` | Statut du job (voir `JobStatus` dans `OpenScan.Contracts`) |
| GET | `/scans/{id}/pages` | Pages acquises pour ce job |
| GET | `/scans/{id}/pages/{pageId}` | Détail d'une page |
| GET | `/scans/{id}/pages/{pageId}/image` | Octets JPEG de la page (streaming, `image/jpeg`) |
| GET | `/scans/{id}/export?format=pdf` | Export PDF multi-pages du job (streaming, `application/pdf`) |
| POST | `/scans/{id}/cancel` | Annule un job en cours |
| POST | `/scans/{id}/resume?fromPage={n}` | Reprend un job `Cancelled`/`RecoverableError` (`fromPage` optionnel) |
| POST | `/scans/{id}/retry` | Relance un job `Error`/`RecoverableError`, pages déjà acquises conservées |

## WebSocket

`ws://127.0.0.1:5276/ws` diffuse les événements temps réel du cycle de vie d'un job
(`scan.created`, `scan.started`, `page.acquired`, `page.persisted`, `scan.completed`,
`scan.error`…) en JSON camelCase. Les données binaires (images) ne transitent jamais par ce
canal — uniquement par les routes `/scans/{id}/pages/*` via disque.

## Structure du dépôt

```
src/
  OpenScan.Contracts/       Modèles, IScannerBackend, erreurs normalisées
  OpenScan.Core/             DeviceManager, ConfigurationEngine, JobManager
  OpenScan.Persistence/      SQLite (Dapper) + stockage fichier des pages
  OpenScan.Agent/            API REST + WebSocket (ASP.NET Core minimal API)
  OpenScan.Backends.Mock/    Backend virtuel (dev/tests)
  OpenScan.Backends.Sane/    Backend SANE (Linux, pilote scanimage)
  OpenScan.Backends.Wia/     Backend WIA (Windows, COM tardif)
  OpenScan.Backends.Twain/   Client TWAIN (lance/pilote le worker isolé)
  OpenScan.Twain.Worker/     Process isolé : seul point de contact avec le DSM TWAIN natif
  OpenScan.Backends.Escl/    Backend eSCL/AirScan (réseau, HTTP/XML)
  OpenScan.Pipeline/         Conversion JPEG (ImageSharp) + export PDF (assembleur maison)
  OpenScan.Security/         Stub — sécurité de session, non implémenté
tests/
  OpenScan.Tests/            158 tests xUnit (unitaires + intégration)
docs/                        Notes de handoff et de validation (TWAIN)
```

## Tests

```bash
dotnet test tests/OpenScan.Tests/OpenScan.Tests.csproj
```

158 tests couvrent : validation de configuration, réservation de scanners, orchestration de job
de bout en bout (avec le backend Mock), persistance SQLite réelle, parsing des capacités
SANE/WIA/TWAIN (logique pure, sans matériel), et un test d'intégration simulant un crash du
worker TWAIN pour vérifier que l'Agent ne plante jamais (exigence de la section 6.5 du cahier
des charges).

## Architecture technique notable

- **Pipeline d'acquisition en flux** : `JobManager` découple acquisition et persistance via une
  `Channel<AcquiredPage>` bornée (jamais un lot entier chargé en mémoire, règle R9).
- **TWAIN isolé dans un process séparé** (règle R5) : un crash du driver TWAIN ne fait jamais
  planter l'Agent — vérifié par test (`TwainScannerBackendTests
  .AcquireAsync_WhenWorkerCrashes_ThrowsScanEngineExceptionWithDriverError`).
- **Un backend remplaçable sans changer l'API publique** (règle R11) : tous implémentent
  `IScannerBackend`, enregistrés côté Agent via injection de dépendances.
- **Toute configuration effectivement appliquée est explicable** (règle R4) : chaque job expose
  `EffectiveConfiguration.Adjustments`, détaillant les écarts entre ce qui a été demandé et ce
  qui a réellement été appliqué (ex. résolution ajustée à la valeur supportée la plus proche).

## Limitations connues / feuille de route

Non couverts par cette implémentation à ce stade (voir le cahier des charges pour le détail) :

- Backend **ICA** (macOS).
- **Pipeline de traitement d'image avancé** (rotation, deskew, crop automatique, détection de
  page blanche, OCR, barcode). La conversion JPEG temps réel et l'export PDF sont en place
  (`OpenScan.Pipeline` : `ImageSharpProcessor`, `MinimalPdfExporter`), mais pas les traitements
  de correction/qualité d'image, ni les formats TIFF/PDF-A.
- **Sécurité de l'Agent** (section 15) : token de session, validation d'origine, protection DNS
  rebinding — l'Agent est protégé uniquement par sa liaison loopback par défaut.
- Tables de persistance `events`, `errors`, `sessions`, `settings` (seules `jobs`, `pages`,
  `profiles` existent).
- Journalisation structurée avec corrélation (`correlationId`), métriques de performance,
  matrice de compatibilité matérielle, signature numérique de l'installeur Windows.

## Documentation complémentaire

Le dossier [`docs/`](./docs) contient les notes de handoff et de validation produites pendant le
développement du backend TWAIN (diagnostic de la découverte vide, du `DllNotFoundException`
32/64 bits, checklist de validation P/Invoke sur matériel réel).
