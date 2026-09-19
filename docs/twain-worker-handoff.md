# Handoff — Backend TWAIN (worker isolé)

Document de transfert pour poursuivre l'implémentation d'OpenScan Engine. À donner tel quel à
l'agent/IA qui prend la suite. Écrit après l'implémentation et la validation des backends Mock,
SANE (testé sur un vrai Fujitsu fi-7030) et WIA (compilé, non testé — pas de machine Windows
disponible pendant son développement).

Référence normative : `KAIROS-SCAN - CAHIER DES CHARGES TECHNIQUE.pdf` à la racine du repo.
Sections citées ci-dessous = sections de ce document.

## 1. État actuel du projet

Solution .NET 8, 12 projets. Ce qui existe et fonctionne déjà :

- **OpenScan.Contracts** — modèles (`Scanner`, `ScanJob`, `ScanPage`, `ScanProfile`,
  `EffectiveConfiguration`, `ScanError`), enums (`ErrorCode`, `JobStatus`, `PageStatus`...),
  `IScannerBackend` (contrat que tout backend doit implémenter — lire ce fichier en premier :
  `src/OpenScan.Contracts/IScannerBackend.cs`).
- **OpenScan.Core** — `DeviceManager` (découverte multi-backend, réservation exclusive),
  `ConfigurationEngine` (validation profil vs capacités), `JobManager` (orchestration complète
  du cycle de vie d'un job : Created→Validating→Ready→Running→Completed/Cancelled/Error, via
  une `Channel<AcquiredPage>` bornée pour découpler acquisition et persistance).
- **OpenScan.Persistence** — SQLite (Dapper) pour jobs/pages/profiles, stockage fichier séparé
  pour les images (`FileSystemPageStorage`, checksum SHA-256, jamais en base).
- **OpenScan.Agent** — API REST + WebSocket (`/ws`, événements en camelCase), écoute sur
  127.0.0.1 uniquement.
- **Backends déjà livrés** (à utiliser comme modèles de style/structure) :
  - `src/OpenScan.Backends.Mock/` — backend factice, référence pour comprendre le contrat.
  - `src/OpenScan.Backends.Sane/` — pilote n'importe quel scanner SANE (Linux) en shellant
    `scanimage`. **Testé avec un vrai scanner physique.** Regarder
    `SaneOptionsParser.cs` (logique pure de parsing, testée sans matériel) vs
    `SaneScannerBackend.cs` (plomberie process/IO). C'est le modèle de séparation à reproduire.
  - `src/OpenScan.Backends.Wia/` — pilote WIA (Windows) via COM tardif (`dynamic` +
    `Type.GetTypeFromProgID`), ciblé `net8.0` (pas `net8.0-windows`) pour rester compilable sur
    Linux/macOS ; toute méthode se neutralise via `OperatingSystem.IsWindows()`. **Compilé mais
    jamais exécuté sur une vraie machine Windows** — à valider avant de le considérer fiable.
    Regarder `WiaCapabilityMapper.cs` + `WiaErrorMapper.cs` (logique pure, testée) vs
    `WiaScannerBackend.cs` (plomberie COM).
- **Tests** : `tests/OpenScan.Tests/` — 38 tests xUnit, tous passent. Inclut des tests
  d'intégration bout-en-bout avec le backend Mock (`JobManagerTests.cs`) et des tests de
  persistance SQLite réels (`SqlitePersistenceTests.cs`).

Commande de référence pour vérifier l'état de la solution :

```bash
dotnet build OpenScan.sln
dotnet test tests/OpenScan.Tests/OpenScan.Tests.csproj
```

## 2. Objectif de cette tâche : backend TWAIN

Le cahier des charges classe TWAIN en priorité **V1** (section 3.1, après le MVP eSCL/WIA/SANE
déjà couvert). Deux exigences non négociables :

- **Section 6.5 — TWAIN doit être isolable** : *« Le backend TWAIN devra fonctionner dans un
  worker séparé afin qu'un crash du driver ou du DSM ne provoque pas nécessairement l'arrêt de
  l'Agent. »* C'est LA contrainte structurante de cette tâche — contrairement à SANE/WIA (qui
  tournent in-process), TWAIN DOIT être un processus enfant séparé.
- **Section 23 — organisation indicative** prévoit explicitement `OpenScan.Twain.Worker` comme
  projet distinct des autres backends.

### Architecture à mettre en place

Deux projets, pas un seul :

1. **`src/OpenScan.Twain.Worker/`** — exécutable console **séparé**, seul endroit qui touche
   réellement le DSM TWAIN (P/Invoke natif, `[DllImport]`). Reçoit des commandes et répond en
   continu (voir protocole ci-dessous). C'est lui qui crashe si le driver TWAIN plante — et ça
   n'affecte alors que ce process, pas l'Agent.
2. **`src/OpenScan.Backends.Twain/`** — implémente `IScannerBackend` (comme Mock/Sane/Wia),
   mais c'est un **client mince** : il lance/gère le process worker (`System.Diagnostics.Process`)
   et lui parle via son protocole. Il ne doit contenir *aucun* P/Invoke TWAIN lui-même.

Ce découpage est ce qui permet de détecter et gérer un crash worker proprement dans
`OpenScan.Backends.Twain` (le process meurt → le backend catch ça → `ScanEngineException` avec
un `ErrorCode` approprié → `JobManager` passe le job en `Error` → l'Agent continue de tourner).

### Protocole Agent ↔ Worker (proposition, adaptable)

Le plus simple à implémenter et déboguer : JSON ligne-par-ligne sur stdin/stdout du process
enfant (pas besoin de dépendance supplémentaire, `System.Text.Json` suffit).

- `OpenScan.Backends.Twain` lance `OpenScan.Twain.Worker.exe` via `Process`, redirige
  stdin/stdout/stderr.
- Chaque commande = une ligne JSON envoyée sur stdin (`{"cmd":"discover"}`,
  `{"cmd":"open","scannerId":"..."}`, `{"cmd":"configure","jobId":"...","resolution":300,...}`,
  `{"cmd":"acquire","jobId":"..."}`, `{"cmd":"cancel","jobId":"..."}`).
- Le worker répond en continu par des lignes JSON (`{"type":"scanners","data":[...]}`,
  `{"type":"page","jobId":"...","pageNumber":1,"filePath":"...","width":...,"height":...}`,
  `{"type":"error","code":"PAPER_JAM","message":"...","recoverable":true}`,
  `{"type":"batchComplete","jobId":"..."}`). Les images ne transitent jamais en JSON/base64
  (contrainte section 13) : le worker écrit chaque page sur disque (répertoire temp) et ne
  renvoie que le chemin ; `OpenScan.Backends.Twain` ouvre ensuite ce fichier en `FileStream` avec
  `FileOptions.DeleteOnClose` pour le `AcquiredPage.Data` — exactement le pattern déjà utilisé
  dans `SaneScannerBackend.AcquireAsync` et `WiaScannerBackend.AcquireAsync`, à copier.
- Extraire ces types de message dans un fichier séparé **pur, sans IO** (ex.
  `WorkerProtocol.cs` avec des `record` sérialisables) pour pouvoir les tester sans lancer de
  vrai process — même logique que `SaneOptionsParser`/`WiaCapabilityMapper`.
- Si le worker meurt de manière inattendue (process exit code non-zéro / stream fermé sans
  message de fin) pendant `AcquireAsync`, le backend doit lever une `ScanEngineException`
  (`ErrorCode.DriverError` ou `BackendError`, `recoverable: false`) plutôt que de planter.

### Ce qu'il y a à faire dans le worker (P/Invoke TWAIN)

Point technique le plus lourd de la tâche : le DSM TWAIN classique (`twaindsm.dll` sur Windows,
via `DSM_Entry`) suit une machine à états à 7 niveaux (Pre-Session → Source Manager Loaded →
Source Manager Open → Source Open → Source Enabled → Transfer Ready → Transferring) et **exige
une boucle de messages Win32** pour recevoir `MSG_XFERREADY` (TWAIN a été conçu autour de la
boucle de messages Windows classique). Prévoir une fenêtre cachée (WinForms `Form` suffit,
référencer `Microsoft.WindowsDesktop.App`/`UseWindowsForms` dans le csproj du worker — celui-ci
*peut* être `net8.0-windows` puisqu'il n'a pas besoin de compiler ailleurs que sur Windows,
contrairement à `OpenScan.Backends.Twain`) pour pomper les messages et déclencher le transfert.

Structs/constantes à déclarer (P/Invoke, standard TWAIN 2.x, jamais spécifique à un fabricant) :
`TW_IDENTITY`, `TW_EVENT`, `TW_CAPABILITY`, `TW_ONEVALUE`, `TW_RANGE`, `TW_ENUMERATION`,
`TW_FIX32`, `TW_USERINTERFACE`, `TW_IMAGEINFO`, `TW_IMAGEMEMXFER`. Groupes/messages :
`DG_CONTROL`/`DG_IMAGE`, `DAT_IDENTITY`/`DAT_CAPABILITY`/`DAT_USERINTERFACE`/`DAT_IMAGEXFER`,
`MSG_OPENDSM`/`MSG_OPENDS`/`MSG_ENABLEDS`/`MSG_XFERREADY`/`MSG_GET`/`MSG_SET`.

**Fin de lot ADF** : contrairement à SANE (texte stderr) et WIA (HRESULT
`WIA_ERROR_PAPER_EMPTY`), TWAIN signale l'ADF vide via `TWRC_FAILURE` + condition code
`TWCC_NOMEDIA` sur l'appel suivant. Le worker doit traiter ce cas comme une fin de lot normale
(comme les deux autres backends), pas comme une erreur remontée à l'Agent.

## 3. Contraintes de style à respecter (déjà appliquées partout ailleurs dans le repo)

- **Toujours séparer logique pure et plomberie I/O.** Toute fonction qui transforme des données
  (mapping capacités ↔ modèle OpenScan, mapping codes d'erreur, (dé)sérialisation du protocole
  worker) doit être une méthode statique pure, dans un fichier séparé, testable sans process ni
  matériel. C'est ce qui a permis d'avoir des tests réels pour SANE et WIA malgré l'absence de
  matériel pendant le développement de WIA.
- **Ne jamais coder en dur un modèle/fabricant.** Uniquement des constantes du standard TWAIN
  lui-même (comme les constantes SANE "Lineart/Gray/Color" ou WIA "Document Handling
  Capabilities" sont génériques au protocole, pas à un scanner précis).
- **Ne jamais prétendre supporter une capacité non confirmée par le driver** (section 12) —
  toujours interroger les capabilities réelles avant de les exposer dans `ScannerCapabilities`.
- **Mapping d'erreurs** : traduire vers `OpenScan.Contracts.ErrorCode` existant (`PaperJam`,
  `NoPaper`, `MultiFeed`, `ScannerBusy`, `ScannerDisconnected`, `DriverError`, etc.) — ne pas
  inventer de nouveau code sans le justifier par un vrai besoin (comme `JobNotFound` a été
  ajouté parce qu'aucun code existant ne convenait).
- **`ConfigureAsync` doit être implémenté** (ajouté à `IScannerBackend` durant le développement
  SANE/WIA — absent du contrat d'origine, mais indispensable : résout et applique
  resolution/mode/source *avant* `AcquireAsync`). Suivre exactement la signature et l'ordre
  d'appel déjà utilisés par `JobManager.StartScanAsync` (`src/OpenScan.Core/JobManager.cs`).
- **Suppression CA1416** : si le worker cible `net8.0-windows` avec de vrais appels Windows-only,
  c'est légitime de ne pas neutraliser via `OperatingSystem.IsWindows()` partout (le projet
  entier est déjà windows-only). Pour `OpenScan.Backends.Twain` en revanche (client mince,
  `net8.0` cross-platform comme WIA), suivre le même pattern que
  `OpenScan.Backends.Wia.csproj` : `<NoWarn>$(NoWarn);CA1416</NoWarn>` avec commentaire, guards
  `OperatingSystem.IsWindows()` explicites.

## 4. Fichiers à créer (proposition concrète)

```
src/OpenScan.Twain.Worker/
  OpenScan.Twain.Worker.csproj      (net8.0-windows, UseWindowsForms, réf. OpenScan.Contracts
                                      uniquement — pas de dépendance à Core/Agent)
  Program.cs                         boucle stdin/stdout, dispatch des commandes
  TwainDsm.cs                        P/Invoke : structs + DllImport DSM_Entry
  TwainSession.cs                    machine à états, fenêtre cachée, boucle de messages
  TwainCapabilityMapper.cs           pur : TW_CAPABILITY -> ScannerCapabilities

src/OpenScan.Backends.Twain/
  OpenScan.Backends.Twain.csproj     (net8.0, réf. OpenScan.Core, NoWarn CA1416)
  TwainScannerBackend.cs             implémente IScannerBackend, lance/pilote le Process worker
  WorkerProtocol.cs                  pur : records de requêtes/réponses + (dé)sérialisation JSON

tests/OpenScan.Tests/
  TwainCapabilityMapperTests.cs      teste TwainCapabilityMapper (pur, pas de matériel requis)
  WorkerProtocolTests.cs             teste la sérialisation du protocole (pur)
```

Ajouter les deux nouveaux projets à `OpenScan.sln` (`dotnet sln add ...`) et référencer
`OpenScan.Backends.Twain` depuis `src/OpenScan.Agent/OpenScan.Agent.csproj`, puis enregistrer
`TwainScannerBackend` comme `IScannerBackend` de plus dans `Program.cs` (même pattern que Mock/
Sane/Wia — chercher `AddSingleton<IScannerBackend,`).

**Ne pas faire dépendre `OpenScan.Twain.Worker` de `OpenScan.Backends.Twain`** ni de `Core` : le
worker ne doit connaître que les types minimaux du protocole (idéalement dupliqués/partagés via
un petit projet `OpenScan.Twain.Protocol` commun aux deux, ou simplement copiés — à trancher
selon ce qui reste le plus simple).

## 5. Ce que je (Claude) ferai ensuite

Le travail d'implémentation TWAIN doit être fait par un autre agent (Gemini, d'après
l'utilisateur) pour préserver le budget de tokens de cette session. Une fois le code livré :

- Je compilerai (`dotnet build OpenScan.sln`) et lancerai les tests (`dotnet test`) sur cette
  machine Linux — ce qui couvre `OpenScan.Backends.Twain` (cross-platform) et toute la logique
  pure extraite, mais **pas** `OpenScan.Twain.Worker` si celui-ci est `net8.0-windows` pur (ne
  compilera que sur Windows — c'est attendu et correct, pas une erreur à corriger).
- Je corrigerai les erreurs de compilation, incohérences avec `IScannerBackend`, régressions sur
  les 38 tests existants, et problèmes évidents (ex. le piège rencontré sur WIA : un appel avec
  un argument `dynamic` fait que **tout le retour** de l'appel devient `dynamic`, cassant la
  déconstruction de tuple plus loin — donc typer explicitement les variables plutôt que `var`
  dès qu'une valeur dynamic entre en jeu).
- La validation réelle sur driver TWAIN (comme celle faite en conditions réelles avec le SANE
  sur le Fujitsu fi-7030) ne pourra se faire que sur une vraie machine Windows avec un scanner
  TWAIN branché — à faire par l'utilisateur, avec mon aide pour corriger ce qui remonte.

## 6. Critères d'acceptation (section 25.1, adaptés à TWAIN)

Le backend est considéré fonctionnel quand :

- [ ] `dotnet build OpenScan.sln` réussit sans erreur sur Linux (le worker `net8.0-windows` peut
      être exclu du build multi-plateforme si nécessaire, mais `OpenScan.Backends.Twain` doit
      compiler).
- [ ] Les 38 tests existants passent toujours, plus les nouveaux tests purs (mapper, protocole).
- [ ] Sur Windows avec un scanner TWAIN réel : découverte, lecture des capacités, job créé et
      validé, plusieurs pages ADF acquises et persistées avec checksum.
- [ ] Un ADF vide se termine proprement en `Completed` avec 0 page (pas une erreur) — comme
      déjà vérifié pour SANE et prévu pour WIA.
- [ ] **Un crash simulé du worker (ex. `Process.Kill()` en cours d'acquisition) fait passer le
      job en `Error` proprement, sans jamais faire planter l'Agent.** C'est le test le plus
      important de toute cette tâche — il valide l'exigence de la section 6.5.
- [ ] `job.Cancel` interrompt réellement l'acquisition (tue le worker si besoin) sans laisser de
      process TWAIN zombie.

Suivi de l'implémentation livrée et de sa validation réelle sur Windows : voir
`docs/twain-pinvoke-validation.md` (document séparé).
