# Validation à faire — P/Invoke TWAIN (Windows requis)

Suivi séparé du handoff d'implémentation (`docs/twain-worker-handoff.md`). Ce document ne
concerne que la validation du code déjà livré, à faire dès qu'une machine Windows avec un
scanner TWAIN est disponible.

**Statut au 19/09/2026** : le backend TWAIN a été implémenté (`OpenScan.Backends.Twain` +
`OpenScan.Twain.Worker`, ~2060 lignes), compile sans erreur, et 76/76 tests passent sur la
machine Linux de développement. Un bug réel a été trouvé et corrigé pendant la revue :
`DiscoverAsync` et `GetCapabilitiesAsync` ne terminaient jamais le process worker qu'ils
lançaient (fuite de process à chaque appel `GET /api/v1/scanners`) — corrigé en réutilisant la
logique de nettoyage d'`AcquireAsync` dans une méthode partagée `StopWorkerGracefullyAsync`,
revérifié empiriquement (process bien tué après coup).

## Ce qui n'a pas pu être vérifié en développement

Faute de machine Windows et de driver TWAIN réel — à tester en priorité :

- `src/OpenScan.Twain.Worker/TwainDsm.cs` (316 lignes) — déclarations P/Invoke (`DllImport
  DSM_Entry`, structs `TW_IDENTITY`/`TW_EVENT`/`TW_CAPABILITY`/`TW_ONEVALUE`/`TW_RANGE`/
  `TW_FIX32`/`TW_IMAGEINFO`/`TW_IMAGEMEMXFER`...). Point de vigilance : le marshaling de ces
  structs doit correspondre **exactement** à la disposition mémoire définie par la spec TWAIN
  (tailles de champs, alignement, `Pack`/`CharSet` sur `[StructLayout]`) — une erreur ici ne
  plante pas à la compilation, elle plante (ou pire, corrompt silencieusement des données) à
  l'exécution contre un vrai DSM.
- `src/OpenScan.Twain.Worker/TwainSession.cs` (772 lignes) — machine à états TWAIN (Pre-Session
  → Source Manager Loaded → ... → Transferring), fenêtre cachée + boucle de messages Win32 pour
  `MSG_XFERREADY`. À vérifier concrètement :
  - Ouverture réelle du Source Manager puis d'une source (`MSG_OPENDSM`/`MSG_OPENDS`) avec au
    moins deux pilotes différents si possible (pas un seul, pour confirmer qu'il n'y a pas de
    supposition implicite propre à un fabricant).
  - Un `MSG_XFERREADY` est bien reçu et déclenche le transfert (le point le plus fragile du
    protocole TWAIN classique, historiquement source de bugs subtils selon l'implémentation de
    la boucle de messages).
  - Le comportement en fin de lot ADF (`TWCC_NOMEDIA`) correspond bien à ce que le driver réel
    renvoie — certains drivers ont des comportements légèrement non conformes à la spec ici.
  - Fermeture propre de la session (`MSG_CLOSEDS`/`MSG_CLOSEDSM`) : un scanner qui reste
    "occupé" après un job WIA/TWAIN raté est un symptôme classique de mauvaise fermeture.
- Tester aussi le cas **duplex** et le cas **résolution demandée non supportée** (doit tomber
  dans `ConfigurationEngine`/`TwainCapabilityMapper`, pas planter le worker).
- Confirmer qu'aucun process `OpenScan.Twain.Worker.exe` ne reste zombie dans le Gestionnaire
  des tâches après une session complète (scan normal, scan annulé, scan en erreur).

## Comment procéder

Lancer l'Agent sur Windows (`dotnet run --project src/OpenScan.Agent`), brancher un scanner
TWAIN, puis suivre le même protocole de validation manuelle déjà utilisé avec succès pour SANE :
`GET /api/v1/scanners`, `GET /api/v1/scanners/{id}/capabilities`, `POST /api/v1/scans` avec un
profil réel, suivre `GET /api/v1/scans/{id}` jusqu'à `Completed`, vérifier les pages persistées.
Remonter tout ce qui coince — correction à partir des messages d'erreur/logs plutôt qu'à l'aveugle.
