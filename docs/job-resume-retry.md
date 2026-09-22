# À implémenter — Reprise et retry de job (section 9 du cahier des charges)

Contexte complet ci-dessous, pas besoin de redemander quoi que ce soit à l'utilisateur.

## Ce que demande le cahier des charges (section 9, citée intégralement)

> La gestion doit être conçue pour des lots de plusieurs centaines voire milliers de pages.
>
> Exemple :
> ```
> JOB-2026-000123
> 001 READY
> 002 READY
> ...
> 436 READY
> 437 ERROR
> 438 NOT_ACQUIRED
> ...
> 500 NOT_ACQUIRED
> ```
>
> Le système doit permettre au minimum : `retry()`, `cancel()`, `resume()`
> et, lorsque techniquement possible : `resume_from_page()`
>
> Un crash de l'Agent ne doit pas provoquer automatiquement la perte de l'état logique du job.
> SQLite doit conserver les informations nécessaires à la reprise.

`cancel()` est déjà implémenté (`JobManager.CancelJobAsync`). Il manque `retry()`, `resume()`,
et la reconciliation après un redémarrage de l'Agent.

## Contrainte physique à comprendre avant de coder

**Un ADF ne peut pas "rembobiner"** : aucun backend (SANE/WIA/TWAIN) ne peut re-scanner une page
physique précise à la demande. Ce qui est réellement possible : redémarrer l'acquisition à
l'endroit où le chargeur en est physiquement (l'utilisateur recharge le papier restant), et
continuer à numéroter les nouvelles pages à la suite de celles déjà acquises. `resume_from_page`
est donc au mieux **un indice enregistré pour la traçabilité**, pas une garantie que le backend
"saute" jusqu'à cette page — documenter cette limite honnêtement dans le code, ne pas prétendre
un comportement qu'aucun backend ne peut réellement fournir. C'est cohérent avec la formulation
du cahier des charges elle-même ("lorsque techniquement possible").

Étant donné ça, `retry()` et `resume()` sont mécaniquement identiques : les deux relancent une
acquisition sur le même job, à partir du même scanner/config, en ajoutant les nouvelles pages à
la suite (`PageNumber` continue depuis `job.TotalPages + 1`). Ils diffèrent seulement par les
statuts de job depuis lesquels ils sont appelables — voir plus bas.

## Ce qui existe déjà (à réutiliser, pas à dupliquer)

Lire `src/OpenScan.Core/JobManager.cs` en entier avant de commencer. Points clés :

- `StartScanAsync` : crée le job, valide, réserve le scanner, configure le backend, démarre
  `RunAcquisitionAsync` en tâche de fond.
- `RunAcquisitionAsync` : orchestre `ProduceAsync` (lit `backend.AcquireAsync` vers un
  `Channel<AcquiredPage>` borné) et `ConsumeAsync` (traite/convertit/persiste chaque page via
  `IImageProcessor` puis `IPageStorage`, cf. `docs/output-engine-realtime-jpeg.md` pour le détail
  de cette partie déjà livrée). Termine en `Completed`/`Cancelled`/`Error`, libère la réservation
  du scanner dans un `finally`.
- `CancelJobAsync` : signale le `CancellationTokenSource` stocké dans `_runningJobs`
  (dictionnaire **en mémoire uniquement** — perdu si l'Agent redémarre).
- Toute la persistance passe déjà par `IJobRepository`/`IPageRepository` (SQLite via Dapper) —
  `ScanJob.TotalPages`/`ProcessedPages`/`FailedPages` et chaque `ScanPage` individuelle sont déjà
  fidèlement enregistrés à chaque étape.

## Ce qui doit être livré

### 1. Refactor interne : paramétrer l'offset de numérotation des pages

`RunAcquisitionAsync`/`ProduceAsync`/`ConsumeAsync` numérotent actuellement les pages à partir de
1 (`AcquiredPage.PageNumber` tel que renvoyé par le backend, utilisé tel quel comme
`ScanPage.PageNumber`). Pour pouvoir reprendre un job, il faut pouvoir **décaler** cette
numérotation : la première page renvoyée par le backend lors d'une reprise doit devenir la page
logique `job.TotalPages + 1`, pas la page 1. Ajouter un paramètre `pageNumberOffset` (int) à ces
méthodes, calculer `page.PageNumber = acquired.PageNumber + pageNumberOffset` dans
`ConsumeAsync`. Pour un premier démarrage, `pageNumberOffset = 0` (comportement actuel inchangé).

### 2. `JobManager.ResumeJobAsync(string jobId, int? fromPage, CancellationToken ct)`

Appelable quand `job.Status` est `Cancelled` ou `RecoverableError` (pas `Completed`, pas `Error`
— voir point 3 pour `Error`). Logique :

1. Charger le job. Si statut non éligible → `ScanEngineException(ErrorCode.InvalidConfiguration,
   ...)` (ou un code plus adapté si tu en identifies un meilleur dans `ErrorCode` — sinon en
   ajouter un, en le justifiant, comme `JobNotFound` l'a été précédemment).
2. Récupérer `scanner`/`backend` via `DeviceManager`, **réserver à nouveau** le scanner
   (`DeviceManager.Reserve` — peut légitimement lever `ScannerBusy` si un autre job tourne
   dessus entre-temps, laisser remonter tel quel).
3. Rappeler `backend.ConfigureAsync(scannerId, jobId, job.EffectiveConfiguration, ct)` — les
   backends exigent une configuration avant `AcquireAsync` (voir `IScannerBackend`), elle a pu
   être perdue si c'est un nouveau process worker (cas TWAIN) ou juste par prudence pour les
   autres.
4. Déterminer l'offset : `fromPage` explicite si fourni (c'est le `resume_from_page`, enregistré
   tel quel à titre indicatif — voir la limite physique expliquée plus haut, ne pas essayer de le
   faire "sauter" au backend), sinon `job.TotalPages`.
5. Passer `job.Status = Running`, republier `EventNames.ScanStarted` (pas besoin d'un nouvel
   événement — la section 14 du cahier des charges ne liste pas d'événement "resumed" distinct).
6. Relancer `RunAcquisitionAsync` (avec l'offset) en tâche de fond, comme `StartScanAsync` le
   fait déjà — factoriser plutôt que dupliquer le code commun entre les deux méthodes.

### 3. `JobManager.RetryJobAsync(string jobId, CancellationToken ct)`

Mêmes mécaniques que `ResumeJobAsync` (factoriser un helper commun), mais appelable uniquement
quand `job.Status` est `Error` ou `RecoverableError`. Ne modifie **pas** rétroactivement les
`ScanPage` déjà en statut `Error`/`NotAcquired` (elles restent comme trace historique de ce qui a
échoué — ne pas les supprimer ni les réécrire, cohérent avec "chaque transition doit être
journalisée"). Les nouvelles pages acquises après le retry s'ajoutent à la suite, avec l'offset
habituel.

### 4. Endpoints (`src/OpenScan.Agent/Program.cs`)

Remplacer les deux stubs `501` actuels :

```
POST /api/v1/scans/{id}/resume?fromPage={n}   (fromPage optionnel)
POST /api/v1/scans/{id}/retry
```

Même pattern try/catch que les autres endpoints (`ScanEngineException` → `.ToProblem()`).

### 5. Reconciliation au démarrage de l'Agent (crash recovery)

Dans `Program.cs`, juste après `await deviceManager.DiscoverAllAsync();` (ou dans une méthode
dédiée appelée à cet endroit) : requêter `IJobRepository.ListAsync()`, trouver les jobs dont le
statut persistant est encore `Running` — impossible légitimement au démarrage puisque
`_runningJobs` (en mémoire) est forcément vide à ce stade, donc un job `Running` en base ne peut
provenir que d'un arrêt brutal de l'Agent pendant qu'il tournait. Les faire passer explicitement
à `RecoverableError` (avec un `ScanError` expliquant "Agent redémarré pendant l'acquisition"),
persister, publier `EventNames.ScanError`. Ça les rend ensuite éligibles à `resume()`/`retry()`
par l'utilisateur — et ça évite qu'un job reste indéfiniment affiché comme "en cours" alors que
plus rien ne le traite (c'est exactement l'exigence "un crash de l'Agent ne doit pas provoquer
automatiquement la perte de l'état logique du job" : l'état n'est pas perdu, il est correctement
requalifié).

## Contraintes à respecter (rappel, déjà appliquées ailleurs dans le repo)

- Ne pas casser les 99 tests existants.
- Factoriser le code commun entre `StartScanAsync`/`ResumeJobAsync`/`RetryJobAsync` plutôt que
  copier-coller la logique de réservation/configuration/lancement de tâche de fond.
- `DeviceManager.Reserve` peut échouer (`ScannerBusy`) — le laisser remonter proprement, ne pas
  l'avaler.
- Ne jamais prétendre que `resume_from_page` fait physiquement sauter le backend à une page
  précise — documenter cette limite dans un commentaire XML sur la méthode concernée plutôt que
  de la passer sous silence.
- Ajouter des tests dans `tests/OpenScan.Tests/` (réutiliser les patterns déjà en place dans
  `JobManagerTests.cs` : repos en mémoire, backend Mock) couvrant au minimum :
  - `resume()` après un `cancel()` : le job reprend, les nouvelles pages ont bien des
    `PageNumber` qui continuent après celles déjà acquises, `TotalPages` final cohérent.
  - `retry()` après une erreur simulée : même vérification.
  - `resume()`/`retry()` sur un job `Completed` → rejeté proprement (pas d'exception non gérée).
  - La reconciliation au démarrage : un job inséré directement en base avec `Status = Running`
    doit être retrouvé et requalifié en `RecoverableError` par la méthode de reconciliation.
  - Le backend Mock devra probablement être ajusté (ou piloté différemment dans le test) pour
    produire un nombre de pages contrôlable entre le run initial et le run de reprise — à toi de
    voir la façon la plus simple de le permettre sans complexifier inutilement
    `MockScannerBackend` pour l'usage normal (hors tests).

## Livrable attendu

- `ResumeJobAsync`/`RetryJobAsync` dans `JobManager`, code commun factorisé avec
  `StartScanAsync`.
- Numérotation des pages correctement décalée lors d'une reprise (pas de collision de
  `PageNumber` avec les pages déjà persistées).
- Endpoints `/scans/{id}/resume` et `/scans/{id}/retry` fonctionnels (plus de `501`).
- Reconciliation des jobs `Running` orphelins au démarrage de l'Agent.
- Tests couvrant les scénarios ci-dessus.
- Note honnête dans le rapport final sur la portée réelle de `resume_from_page` (indicatif,
  pas un "seek" physique du backend) — comme pour TWAIN/ImageSharp, même logique de transparence
  que le reste du projet plutôt que de survendre une fonctionnalité.
