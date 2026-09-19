# À investiguer — TWAIN ne remonte aucun scanner sur machine réelle

Prompt de travail pour poursuivre l'implémentation d'OpenScan Engine. Contexte complet ci-dessous
pour ne pas avoir à redemander quoi que ce soit à l'utilisateur.

## Constat (test réel sur Windows, 19/09/2026)

`GET /api/v1/scanners` sur une machine Windows avec un scanner Fujitsu fi-7030 branché retourne :

```json
[
  {"id":"mock-scanner-1","backend":"mock", ...},
  {"id":"{6BDD1FC6-810F-11D0-BEC7-08002BE2092F}\\0000","name":"fi-7030","manufacturer":"FUJITSU","backend":"wia", ...}
]
```

Le backend **WIA a trouvé et scanné avec succès** ce fi-7030 (job `Completed`, 2 pages ADF,
dimensions et checksums corrects — entièrement validé en conditions réelles). Mais **aucune
entrée avec `"backend":"twain"` n'apparaît**, alors que `TwainScannerBackend` est bien enregistré
dans `Program.cs` aux côtés de Mock/Sane/Wia.

## Hypothèse la plus probable (à vérifier en premier)

`TwainScannerBackend.DiscoverAsync` (`src/OpenScan.Backends.Twain/TwainScannerBackend.cs`)
retourne silencieusement une liste vide dans plusieurs cas sans aucune trace :

```csharp
var workerPath = ResolveWorkerExecutablePath();
if (!File.Exists(workerPath))
{
    return [];   // <-- aucun log, aucune indication
}
```

Et `ResolveWorkerExecutablePath()` cherche `OpenScan.Twain.Worker.exe` dans cet ordre :
1. `_customWorkerExecutablePath` (non utilisé en prod)
2. variable d'env `OPENSCAN_TWAIN_WORKER_PATH` (non définie chez l'utilisateur)
3. **le même répertoire que l'Agent** (`AppContext.BaseDirectory`)
4. **un chemin relatif de dev** codé en dur :
   `<baseDir>/../../../../OpenScan.Twain.Worker/bin/Debug/net8.0/OpenScan.Twain.Worker.exe`

**Point clé** : `src/OpenScan.Agent/OpenScan.Agent.csproj` référence `OpenScan.Backends.Twain`
(une bibliothèque), **pas** `OpenScan.Twain.Worker` (un exécutable séparé). MSBuild ne copie
**jamais automatiquement** l'exécutable d'un projet non référencé à côté de la sortie de
l'Agent. Donc le candidat n°3 échoue quasi certainement en toutes circonstances.

Le candidat n°4 (chemin relatif de dev) suppose une disposition exacte du repo sur disque. Or le
chemin observé chez l'utilisateur est :

```
C:\Users\HOME\Music\OpenScan-Engine\OpenScan-Engine\src\Openscan.Agent\bin\Debug\net8.0\...
```

Remarquer le dossier dupliqué `OpenScan-Engine\OpenScan-Engine` — une disposition de checkout
légèrement différente de celle prévue suffit à casser un `../../../../` codé en dur. Il est très
probable que **le worker n'a jamais été trouvé du tout**, et que la liste vide observée ne dit
rien sur l'état réel du DSM TWAIN/du driver du fabricant sur cette machine.

## Ce qui doit être corrigé

1. **Rendre l'échec diagnosticable.** Actuellement `DiscoverAsync` avale toute exception
   (`catch (Exception ex) when (ex is not OperationCanceledException) { return []; }`) et le cas
   "fichier introuvable" ne logue rien du tout. Il faut au minimum écrire un message clair
   (`Console.Error` ou, mieux, injecter un `ILogger<TwainScannerBackend>` via DI comme le permet
   `AddSingleton<IScannerBackend, TwainScannerBackend>` dans `Program.cs`) indiquant :
   - le chemin exact essayé pour trouver le worker,
   - s'il n'a pas été trouvé, lister les candidats testés,
   - si le worker a démarré mais n'a rien répondu / a crashé, inclure son stderr.

   Objectif : la prochaine fois que quelqu'un voit une liste vide, il doit pouvoir comprendre
   pourquoi depuis les logs de l'Agent, sans avoir à lire le code source.

2. **Fiabiliser la localisation du worker en environnement de développement.** Ne pas se fier à
   un unique chemin relatif fragile. Options possibles (choisir la plus simple et robuste, ou
   combiner) :
   - Ajouter une cible MSBuild dans `OpenScan.Agent.csproj` qui copie la sortie de build de
     `OpenScan.Twain.Worker` dans le répertoire de sortie de l'Agent après compilation (ex.
     `<Target Name="CopyTwainWorker" AfterTargets="Build">` avec un `<Copy>` des fichiers du
     worker). C'est la solution la plus propre : le candidat n°3 (même répertoire que l'Agent)
     fonctionnerait alors systématiquement, en dev comme en publication.
   - En complément, remonter la recherche du chemin relatif de dev en cherchant dynamiquement
     un dossier `OpenScan.Twain.Worker` en remontant l'arborescence depuis `AppContext
     .BaseDirectory` (au lieu d'un nombre fixe de `..`), pour tolérer une profondeur de checkout
     différente.

3. **Ne pas mélanger les deux causes possibles.** Une fois le point 1 en place, il sera possible
   de distinguer clairement "le worker n'a pas été trouvé" (bug de packaging/chemin, à corriger
   par le code) de "le worker a démarré mais le DSM TWAIN n'est pas installé sur cette machine"
   (état normal si l'utilisateur n'a pas installé de composant TWAIN séparé — le pilote TWAIN du
   fi-7030 n'est pas forcément installé même si le pilote WIA l'est, ce sont deux composants
   distincts chez la plupart des fabricants). Dans ce second cas, le comportement actuel (liste
   vide, pas d'erreur) est correct — il suffit que ce soit *visible* dans les logs plutôt que
   totalement silencieux.

## Contraintes à respecter (rappel, déjà appliquées ailleurs dans le repo)

- Ne pas casser les tests existants (76 tests passent actuellement,
  `tests/OpenScan.Tests/TwainScannerBackendTests.cs` inclut déjà un test utilisant
  `customWorkerExecutablePath` pour simuler un worker via un script — le mécanisme de logging
  ajouté ne doit pas dépendre uniquement de `AppContext.BaseDirectory` réel en test).
- Garder `DiscoverAsync` non bloquant et jamais fatal pour les autres backends (le principe déjà
  en place dans `DeviceManager.DiscoverAllAsync` : un backend qui échoue ne doit jamais empêcher
  la découverte des autres).
- Toujours regarder `src/OpenScan.Backends.Sane/SaneScannerBackend.cs` et
  `src/OpenScan.Backends.Wia/WiaScannerBackend.cs` comme référence de style avant d'écrire du
  nouveau code.

## Livrable attendu

- Le correctif de packaging/localisation du worker (option MSBuild recommandée ci-dessus).
- Le logging clair de la cause d'échec de `DiscoverAsync`/`GetCapabilitiesAsync` quand le worker
  n'est pas trouvé ou ne répond pas.
- Si possible, un test qui vérifie que le message de log/l'exception distingue bien "worker
  introuvable" de "worker introuvable mais chemin custom fourni" (déjà couvert) — pas besoin de
  tester le vrai DSM TWAIN, uniquement la logique de résolution de chemin et le logging.
