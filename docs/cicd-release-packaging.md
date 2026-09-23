# À implémenter — Pipeline CI/CD GitHub Actions : installeur Windows + paquet .deb Linux

Contexte complet ci-dessous, pas besoin de redemander quoi que ce soit à l'utilisateur.

## Pourquoi

Aujourd'hui, obtenir un package installable pour Windows ou Linux demande de builder à la main
(`dotnet publish ...`) puis de lancer manuellement `scripts/install-windows.ps1` ou
`scripts/install-linux.sh`. L'objectif est d'automatiser ça entièrement en ligne : à chaque tag de
version poussé sur GitHub (ex. `v1.0.0`), une pipeline construit **un vrai installeur Windows
(.exe)** et **un vrai paquet Debian (.deb)**, et les publie en pièces jointes d'une GitHub Release.
Pas de version "zip + lance le script toi-même" — l'utilisateur final double-clique
l'installeur (Windows) ou fait `apt install ./openscan-agent_....deb` (Linux) et tout se fait :
copie des fichiers, création de l'utilisateur système/service, démarrage automatique.

## Déclencheur

- `on: push: tags: ["v*.*.*"]` uniquement — pas à chaque commit sur `main`. La version du paquet
  est dérivée du tag (`v1.2.3` → `1.2.3`), à injecter dans `dotnet publish` via
  `-p:Version=$VERSION`, dans le champ `Version:` du `DEBIAN/control`, et dans `AppVersion` du
  script Inno Setup.
- Un seul workflow, `.github/workflows/release.yml`, avec trois jobs : `build-windows`,
  `build-linux`, puis `publish-release` (dépend des deux premiers via `needs:`), qui télécharge
  les deux artefacts et crée la GitHub Release avec `gh release create` (ou l'action
  `softprops/action-gh-release`), en y attachant les deux fichiers.

## Job `build-windows` (runner `windows-latest`)

1. `dotnet publish src/OpenScan.Agent -c Release -r win-x64 --self-contained -p:Version=$VERSION -o publish/win-x64`
   — ceci déclenche déjà, via les cibles MSBuild existantes dans
   `src/OpenScan.Agent/OpenScan.Agent.csproj` (`BuildAndCopyTwainWorkerOnPublish`), la construction
   et copie automatique du worker TWAIN self-contained win-x86 dans `publish/win-x64/twain-worker/`
   — ne pas dupliquer cette étape, elle est déjà gérée.
2. Installer Inno Setup sur le runner (`choco install innosetup` fonctionne directement sur
   `windows-latest`, ou utiliser l'action toute faite `Minionguyjpro/Inno-Setup-Action`).
3. Écrire `scripts/windows-installer.iss` (nouveau fichier), qui :
   - Embarque tout le contenu de `publish/win-x64/` (`[Files] Source: "publish\win-x64\*"; DestDir:
     "{app}"; Flags: recursesubdirs`).
   - `DefaultDirName={autopf}\OpenScan\Agent` (équivalent de l'`$InstallDir` par défaut du script
     PowerShell existant).
   - Dans `[Run]`, après la copie des fichiers, exécute les mêmes commandes `sc.exe` que
     `scripts/install-windows.ps1` (création du service `OpenScanAgent`, `start= auto`,
     configuration `sc.exe failure` pour le redémarrage auto sur crash — reprendre exactement la
     logique déjà présente dans ce script plutôt que la réinventer) puis `Start-Service`/
     `net start OpenScanAgent`.
   - Dans `[UninstallRun]`, arrêt + suppression du service (`sc.exe stop` puis `sc.exe delete`),
     symétrique à `scripts/uninstall-windows.ps1`.
   - `PrivilegesRequired=admin` (le service Windows exige des droits admin pour s'enregistrer).
   - Créer aussi `%ProgramData%\OpenScan` avec les bons droits si besoin (le service le fera
     lui-même au premier démarrage via `DataDirectoryResolver`, mais vérifier qu'il n'y a pas de
     conflit de droits avec l'utilisateur qui lance l'installeur vs le compte du service —
     `LocalSystem` par défaut pour un service `sc.exe create` sans `obj=`, donc pas de souci
     normalement).
   - Nom de sortie : `OpenScanEngineSetup-$VERSION-x64.exe`.
4. Uploader l'installeur généré comme artefact du job (`actions/upload-artifact`).

**Ne pas** essayer de signer l'installeur ici (`signtool`) — la signature de code est un sujet
séparé, en pause, traité ultérieurement si l'utilisateur fournit un certificat. L'installeur sera
non signé pour l'instant (SmartScreen affichera un avertissement "Éditeur inconnu", c'est un
comportement attendu et déjà documenté ailleurs dans le projet).

## Job `build-linux` (runner `ubuntu-latest`)

1. `dotnet publish src/OpenScan.Agent -c Release -r linux-x64 --self-contained -p:Version=$VERSION -o publish/linux-x64`
2. Construire l'arborescence du paquet Debian dans un dossier temporaire (ex. `debian-pkg/`) :
   ```
   debian-pkg/DEBIAN/control
   debian-pkg/DEBIAN/postinst
   debian-pkg/DEBIAN/postrm
   debian-pkg/opt/openscan-agent/          <- contenu de publish/linux-x64/
   debian-pkg/etc/systemd/system/openscan-agent.service   <- copie de scripts/openscan-agent.service
   ```
3. `DEBIAN/control` (nouveau fichier) — champs standards : `Package: openscan-agent`,
   `Version: $VERSION`, `Architecture: amd64`, `Maintainer: FranckJudes`,
   `Description: Agent local de numérisation OpenScan Engine`, `Depends:` (vide, c'est
   self-contained, pas besoin du runtime .NET installé séparément).
4. `DEBIAN/postinst` (nouveau fichier, exécuté après installation des fichiers par `dpkg`) —
   reprendre **exactement** la logique déjà présente dans `scripts/install-linux.sh` à partir de
   l'étape "création de l'utilisateur système" (le paquet gère déjà la copie des fichiers via
   `dpkg`, donc seulement la partie post-copie : `useradd` système dédié, ajout aux groupes
   `scanner`/`lp`, `chown` du répertoire de données `/var/lib/openscan`, `systemctl daemon-reload`,
   `systemctl enable --now openscan-agent`). `chmod 755` sur le binaire `OpenScan.Agent`.
5. `DEBIAN/postrm` (nouveau fichier) — symétrique à `scripts/uninstall-linux.sh` : sur `purge`
   uniquement, arrêt/désactivation du service, suppression de l'utilisateur système ; sur `remove`
   simple, ne pas toucher aux données utilisateur (`/var/lib/openscan`) — convention Debian
   standard à respecter ici (`$1` reçu par le script vaut `remove`, `purge`, `upgrade`, etc., voir
   `man deb-postrm`).
6. `dpkg-deb --build --root-owner-group debian-pkg openscan-agent_${VERSION}_amd64.deb`
7. Uploader le `.deb` comme artefact du job.

## Job `publish-release` (dépend des deux précédents)

- Télécharge les deux artefacts (`actions/download-artifact`).
- Crée la GitHub Release correspondant au tag poussé, avec les deux fichiers attachés
  (`OpenScanEngineSetup-$VERSION-x64.exe` et `openscan-agent_${VERSION}_amd64.deb`).
- Le corps de la release peut rester minimal (généré automatiquement par GitHub, ou notes de
  version manuelles si un fichier `CHANGELOG.md` existe déjà — sinon ne pas en créer un, hors
  scope de cette tâche).

## Contraintes à respecter

- Ne casse ni `dotnet build`/`dotnet run` en développement local, ni les scripts
  `install-windows.ps1`/`install-linux.sh` existants — ceux-ci restent utilisables tels quels pour
  une installation manuelle sans passer par un package (les garder documentés dans le README comme
  méthode alternative).
- Ne pas dupliquer la logique métier des scripts existants dans les maintainer scripts `.deb` /
  le script Inno Setup — **reprendre les mêmes commandes**, pas les réinventer différemment (risque
  de divergence entre les deux méthodes d'installation).
- Pas de secrets/tokens à créer manuellement pour ce workflow — `GITHUB_TOKEN` fourni
  automatiquement par GitHub Actions suffit pour créer la release et uploader les artefacts.

## Validation

- **Linux/.deb** : je peux valider moi-même en local (vraie machine Linux) — construire le paquet,
  vérifier sa structure (`dpkg-deb --info`, `dpkg-deb --contents`), l'installer réellement dans un
  conteneur ou chroot jetable, vérifier que le service démarre, désinstaller, vérifier le
  comportement `remove` vs `purge`.
- **Windows/.exe** : je ne peux pas exécuter Inno Setup ni tester l'installeur réel depuis cet
  environnement Linux — relecture attentive du script `.iss` et de la syntaxe du workflow
  uniquement de mon côté ; validation réelle de l'exécution à faire par l'utilisateur sur une
  machine Windows, comme pour WIA/TWAIN précédemment (règle R12 du cahier des charges : pas de
  mention "compatible" sans test sur matériel/OS réel).
- Le workflow complet (déclenchement sur tag, deux jobs en parallèle, job de release) peut être
  vérifié syntaxiquement (`actionlint` si disponible, ou au minimum une relecture manuelle
  attentive du YAML) même sans pouvoir le déclencher réellement depuis ici.

## Livrable attendu

- `.github/workflows/release.yml`
- `scripts/windows-installer.iss`
- `debian/control`, `debian/postinst`, `debian/postrm` (ou équivalent, nommage libre tant que le
  workflow les référence correctement)
- README mis à jour avec une section "Installation via package" pointant vers la page GitHub
  Releases, en gardant la méthode manuelle existante comme alternative documentée.
