# À corriger — DllNotFoundException twaindsm.dll/twain_32.dll (probable mismatch 32/64 bits)

Suite directe de `docs/twain-discovery-empty-investigation.md` (déjà résolue : le worker est
maintenant trouvé et démarré correctement). Nouveau problème, plus profond, révélé une fois ce
premier bug corrigé.

## Constat (test réel sur Windows, 19/09/2026)

Le worker démarre bien, reçoit bien la commande `discover`, mais échoue à l'initialisation TWAIN :

```
System.DllNotFoundException: Impossible de localiser 'twaindsm.dll' ou 'twain_32.dll' dans le système.
   at OpenScan.Twain.Worker.TwainSession.Initialize() ... TwainSession.cs:line 50
```

L'utilisateur confirme avoir **installé le DSM TWAIN officiel**, sans effet. Il confirme aussi
que **NAPS2, lui, détecte correctement le fi-7030 via TWAIN avec le pilote PaperStream IP**
installé sur la même machine. Donc le DSM/pilote existe bel et bien sur le système — le worker
ne le trouve/charge simplement pas.

## Diagnostic probable

Deux causes classiques et bien documentées dans l'écosystème TWAIN, qui se cumulent souvent :

1. **Mismatch de bitness (32 vs 64 bits).** Beaucoup de pilotes TWAIN de scanners de bureau
   (dont historiquement les gammes Fujitsu fi-70xx / PaperStream IP) ne sont fournis qu'en
   **32 bits**. `src/OpenScan.Twain.Worker/OpenScan.Twain.Worker.csproj` ne fixe actuellement
   aucun `<PlatformTarget>` ni `<RuntimeIdentifier>` → le worker compile/s'exécute en 64 bits
   par défaut sur une machine 64 bits. Un process 64 bits ne peut pas charger une DLL 32 bits ;
   Windows/le loader .NET remonte fréquemment ça comme "DLL introuvable" plutôt qu'une erreur de
   format, ce qui correspond exactement au symptôme observé.

   **C'est précisément pour cette raison que NAPS2 fait tourner son propre worker TWAIN en
   x86** (fait bien documenté et connu de l'écosystème TWAIN, pas une bizarrerie de leur
   implémentation). Le cahier des charges autorise explicitement l'étude de NAPS2 comme
   référence technique (**règle R13**) — *architecture uniquement*, aucun code ne doit en être
   recopié sans validation juridique préalable.

2. **Emplacement non standard du DSM legacy 32 bits.** Le TWAIN 32 bits historique
   (`TWAIN_32.DLL`) vit conventionnellement à un chemin fixe :
   `%windir%\twain_32\twain_32.dll` (ex. `C:\Windows\twain_32\twain_32.dll`) — ce dossier n'est
   **pas** dans le PATH système par défaut. Un `[DllImport("twain_32.dll")]` nu, qui ne s'appuie
   que sur l'ordre de recherche standard (dossier de l'exe, System32/SysWOW64, PATH), ne trouvera
   donc jamais ce fichier même si le worker tourne dans la bonne architecture. Le DSM TWAIN 2.x
   plus récent (`twaindsm.dll`) s'installe en général directement dans `%windir%\` (qui, lui,
   fait partie de l'ordre de recherche standard), donc ce point concerne surtout le fallback
   32 bits legacy.

## Corrections à apporter

### 1. Forcer le worker en x86 (32 bits)

Dans `src/OpenScan.Twain.Worker/OpenScan.Twain.Worker.csproj`, ajouter :

```xml
<PropertyGroup>
  ...
  <PlatformTarget>x86</PlatformTarget>
</PropertyGroup>
```

Vérifier que ça ne casse pas la cible MSBuild `CopyTwainWorker` dans
`src/OpenScan.Agent/OpenScan.Agent.csproj` (qui copie depuis
`bin\$(Configuration)\$(TargetFramework)\*.*` — `PlatformTarget` seul, sans
`RuntimeIdentifier`, ne change pas ce chemin de sortie, donc ça devrait rester compatible sans
modification côté Agent, mais à vérifier après coup).

Attention : si `OpenScan.Twain.Worker` référence des DLLs P/Invoke qui doivent elles-mêmes
matcher l'architecture (rien de spécial ici a priori, seulement des DllImport vers le DSM
système), pas d'impact attendu ailleurs dans le projet.

### 2. Chercher explicitement le DSM à ses emplacements connus avant le DllImport nu

Dans `src/OpenScan.Twain.Worker/TwainDsm.cs` (ou `TwainSession.cs` selon où vit
`Initialize()`), avant d'appeler `DSM_Entry` via le `[DllImport]` par nom simple, résoudre le
chemin complet et charger explicitement via `NativeLibrary.Load(cheminAbsolu)` (ou
`SetDllDirectory`/`AddDllDirectory`), en essayant dans l'ordre :

1. `%windir%\twaindsm.dll` (TWAIN 2.x moderne)
2. `%windir%\twain_32\twain_32.dll` (legacy 32 bits — emplacement fixe historique)
3. Fallback sur le nom nu `twaindsm.dll` / `twain_32.dll` (ordre de recherche standard, au cas où
   un pilote l'aurait placé ailleurs sur le PATH)

Utiliser `Environment.GetFolderPath(Environment.SpecialFolder.Windows)` pour obtenir
`%windir%` de façon portable plutôt que de coder `C:\Windows` en dur.

Si aucun des chemins ne donne de fichier existant, lever une erreur claire listant les chemins
essayés (même logique de diagnostic que celle déjà mise en place pour la résolution du chemin du
worker lui-même côté `TwainScannerBackend.ResolveWorkerExecutablePath` — rester cohérent avec ce
pattern).

## Contraintes à respecter

- Ne pas casser les 81 tests existants.
- Ne recopier aucun code de NAPS2 — uniquement s'en inspirer pour l'architecture (worker 32
  bits, résolution de chemin DSM), conformément à la règle R13.
- Garder `OpenScan.Twain.Worker` compilable/testable dans son ensemble (le
  `OpenScan.Backends.Twain` côté Agent doit continuer à compiler sur Linux comme avant — seul le
  worker change de cible de plateforme, ce qui n'affecte pas la compilation Linux puisque de
  toute façon le worker ne s'exécute réellement que sur Windows).

## Validation attendue

Une fois corrigé, sur la machine Windows de test :
- Le worker doit charger le DSM sans `DllNotFoundException`.
- `GET /api/v1/scanners` doit faire apparaître une entrée `"backend":"twain"` pour le fi-7030 en
  plus de l'entrée `"backend":"wia"` déjà validée.
- Un job de scan via ce backend TWAIN doit produire des pages avec les mêmes garanties déjà
  vérifiées côté WIA (dimensions correctes, checksum, fichier persistant).
