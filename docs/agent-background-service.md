# À implémenter — Agent en service système (background, mono-instance, permanent)

Contexte complet ci-dessous, pas besoin de redemander quoi que ce soit à l'utilisateur.

## Pourquoi

L'Agent doit s'installer une fois sur chaque poste utilisateur et **tourner en permanence en
arrière-plan** (démarré au boot de la machine, sans qu'un utilisateur ait besoin de le lancer
manuellement, sans fenêtre de console visible). Aujourd'hui c'est une appli console ASP.NET Core
classique : elle ne survit pas à un redémarrage machine, rien n'empêche de la lancer deux fois
(collision de port/SQLite), et les logs sortent uniquement sur une console qui n'existera plus en
usage réel.

## Contraintes à couvrir (résumé, détail ci-dessous)

1. Démarrage automatique au boot, sans session utilisateur (service Windows / systemd Linux).
2. Verrou mono-instance : une deuxième tentative de lancement doit échouer proprement, pas créer
   de collision de port ni de corruption SQLite.
3. Redémarrage automatique en cas de crash (configuré, pas juste "possible en théorie").
4. Déplacement du répertoire de données hors du dossier d'installation (droits d'écriture d'un
   service généralement restreints sur le dossier d'install, surtout Windows).
5. Logs redirigés vers un sink adapté à un service sans console (journal système / fichier).
6. Scripts d'installation/désinstallation du service (pas encore le MSI complet de la section 22
   du cahier des charges — juste ce qu'il faut pour "installer une fois, ça tourne toujours").

## 1. Hébergement en tant que service (Windows + Linux)

Ajouter les packages NuGet officiels .NET pour ça (mécanisme standard, pas une réinvention) :
- `Microsoft.Extensions.Hosting.WindowsServices` → `builder.Host.UseWindowsService(...)`
- `Microsoft.Extensions.Hosting.Systemd` → `builder.Host.UseSystemd()`

Les deux peuvent être appelés **inconditionnellement** dans `src/OpenScan.Agent/Program.cs` (ils
ne font rien si l'app n'est pas effectivement lancée par le SCM Windows / systemd — donc ça ne
casse pas `dotnet run`/le développement local). Donner un nom de service explicite via
`UseWindowsService(options => options.ServiceName = "OpenScanAgent")`.

## 2. Verrou mono-instance

Avant même de construire l'hôte Kestrel (tout début de `Program.cs`), vérifier qu'aucune autre
instance ne tourne déjà :
- **Windows** : `System.Threading.Mutex` nommé (ex. `Global\\OpenScanAgent`), `WaitOne(0)`. Si
  `false`, une autre instance tient déjà le verrou → logger clairement et quitter avec un code de
  sortie non-zéro (ne pas planter avec une stack trace brute comme aujourd'hui).
- **Linux** : fichier de verrou avec `flock` exclusif non bloquant (ex.
  `/var/lock/openscan-agent.lock` ou équivalent dans le répertoire de données choisi au point 4),
  relâché automatiquement à la fermeture du fichier/process. `System.Threading.Mutex` nommé ne
  fonctionne pas de façon fiable entre process sur Linux — ne pas s'appuyer dessus pour cette
  plateforme, utiliser un verrou fichier natif (`FileStream` avec `FileShare.None` suffit déjà en
  pratique comme verrou exclusif basique, ou `System.IO.FileStream.Lock` explicite).

Extraire cette logique dans une petite classe pure/testable (ex.
`SingleInstanceGuard` dans `OpenScan.Agent` ou `OpenScan.Core`) avec une méthode du genre
`TryAcquire(out IDisposable? handle)` — testable en simulant deux acquisitions successives dans
le même process de test (la deuxième doit échouer) sans avoir besoin de lancer un vrai deuxième
process.

## 3. Redémarrage automatique en cas de crash

- **systemd** : dans le fichier `.service` (point 6), `Restart=on-failure` et `RestartSec=5`.
- **Windows** : documenter dans le script d'installation PowerShell l'appel `sc.exe failure` (ou
  l'équivalent `New-Service`/`Set-Service` + configuration des actions de récupération) pour
  configurer un redémarrage automatique après échec.

## 4. Répertoire de données hors du dossier d'installation

Dans `Program.cs`, le calcul actuel est :
```csharp
var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
```
Remplacer par une résolution qui, par défaut, pointe vers un emplacement système approprié :
- Windows : `%ProgramData%\OpenScan\` (`Environment.GetFolderPath(Environment
  .SpecialFolder.CommonApplicationData)` + `OpenScan`).
- Linux : `/var/lib/openscan/` si le process a les droits d'écriture dessus (cas normal pour un
  service systemd tournant sous un utilisateur dédié avec ce répertoire pré-créé par le script
  d'installation, point 6), sinon repli sur l'ancien comportement (`AppContext.BaseDirectory/data`)
  pour ne pas casser l'usage en développement local (`dotnet run`).
- Rendre ce chemin **configurable** (variable d'environnement `OPENSCAN_DATA_DIR` ou clé
  `appsettings.json`) plutôt que codé en dur, pour laisser le script d'installation décider
  explicitement sans avoir à modifier le code.

## 5. Logs adaptés à un service sans console

`UseWindowsService()`/`UseSystemd()` redirigent déjà une bonne partie de la journalisation
standard ASP.NET Core vers les sinks appropriés (Observateur d'événements / `journald`) — vérifier
que c'est suffisant une fois branché. En complément, envisager d'ajouter un provider de log
fichier simple (ex. la configuration `Logging` standard d'ASP.NET Core avec un provider fichier,
ou au minimum documenter où trouver les logs dans chaque cas — `journalctl -u openscan-agent` sous
Linux, Observateur d'événements sous Windows). Ne pas sur-ingénierer un système de logs complet
ici (c'est le périmètre de la section 19 du cahier des charges, hors scope de cette tâche).

## 6. Scripts d'installation / désinstallation

**Windows** (`scripts/install-windows.ps1` ou équivalent) :
- Publie ou pointe vers le build self-contained (`dotnet publish ... -r win-x64 --self-contained`,
  déjà fonctionnel — voir `src/OpenScan.Agent/OpenScan.Agent.csproj`).
- Crée le service (`sc.exe create OpenScanAgent binPath= "..." start= auto`), configure les
  actions de récupération (redémarrage auto), démarre le service.
- Script `uninstall-windows.ps1` symétrique (`sc.exe stop` puis `sc.exe delete`).

**Linux** (`scripts/install-linux.sh` + `scripts/openscan-agent.service`) :
- Fichier unit systemd avec `WantedBy=multi-user.target`, `Restart=on-failure`, `RestartSec=5`,
  `ExecStart=` pointant vers l'exécutable self-contained publié, tourne sous un utilisateur dédié
  non-root (créer l'utilisateur système si besoin dans le script d'install), `/var/lib/openscan/`
  pré-créé avec les bons droits pour cet utilisateur.
- Script d'installation : copie l'exécutable, installe le fichier `.service` dans
  `/etc/systemd/system/`, `systemctl daemon-reload`, `systemctl enable --now openscan-agent`.
- Script de désinstallation symétrique.

## Contraintes à respecter (rappel, déjà appliquées ailleurs dans le repo)

- Ne pas casser les 148 tests existants, ni le flux `dotnet run`/`dotnet build` habituel en
  développement local (tout ce qui touche au service doit être un no-op transparent hors contexte
  service).
- `SingleInstanceGuard` doit être pur/testable comme le reste des composants du repo (voir
  `SaneOptionsParser`, `TwainDsmLocator`, etc. comme modèles de séparation logique/plomberie).
- Ne pas construire un vrai installeur MSI ici (hors scope, section 22 complète du cahier des
  charges) — seulement des scripts fonctionnels pour enregistrer/désenregistrer le service.

## Validation

- **Linux** : je peux tester moi-même directement ici (vraie machine Linux) — build, installation
  du service systemd, vérification qu'il démarre au `daemon-reload`/`enable --now`, qu'une
  deuxième tentative de lancement manuel de l'exécutable échoue proprement à cause du verrou
  mono-instance, que `journalctl` montre bien les logs, que `systemctl stop` puis un crash
  simulé (`kill -9` du process) déclenche bien le redémarrage automatique.
- **Windows** : reste à valider par l'utilisateur sur sa machine, comme pour WIA/TWAIN — je ne
  peux pas vérifier l'enregistrement réel d'un Service Windows depuis cet environnement Linux.

## Livrable attendu

- `UseWindowsService()`/`UseSystemd()` branchés dans `Program.cs`.
- `SingleInstanceGuard` (ou nom équivalent), testable, empêchant toute collision de port/SQLite
  lors d'un second lancement.
- Répertoire de données déplaçable vers un emplacement système, configurable, avec repli sur le
  comportement actuel en développement local.
- Configuration de redémarrage automatique en cas de crash (systemd nativement ; documentée pour
  Windows dans le script d'installation).
- Scripts d'installation/désinstallation pour les deux plateformes.
- Tests couvrant le verrou mono-instance (au minimum).
