# À implémenter — Conversion JPEG temps réel + export PDF à la demande

Contexte complet ci-dessous, pas besoin de redemander quoi que ce soit à l'utilisateur.

## Objectif exprimé par l'utilisateur

*"Je veux un truc de conversion très très rapide qui va afficher les images en temps réel sur le
web sans temps de latence après le scan."*

Priorité absolue : **vitesse de conversion + affichage immédiat page par page**, pas l'exhaustivité
des formats de sortie. Le PDF est secondaire (à la demande / fin de job), pas le chemin critique.

## État actuel du projet

Lire `README.md` et `docs/twain-worker-handoff.md` pour le contexte général. Résumé pertinent ici :

- `JobManager.ConsumeAsync` (`src/OpenScan.Core/JobManager.cs`) traite déjà les pages **en flux**,
  une par une, dès qu'elles arrivent d'un backend (`AcquiredPage`), via une `Channel<AcquiredPage>`
  bornée. C'est le bon endroit pour brancher la conversion — aucun changement d'architecture de
  pipeline nécessaire.
- Chaque page acquise est actuellement persistée **telle quelle, en brut**, via
  `IPageStorage.SaveAsync` (`FileSystemPageStorage` dans `src/OpenScan.Persistence/`) : le format
  brut dépend du backend (PNM pour SANE, BMP pour WIA et TWAIN, octets synthétiques arbitraires
  pour Mock — voir `MockScannerBackend.GeneratePageData`, qui ne produit PAS une image valide).
- `ScanProfile.OutputFormat` (`src/OpenScan.Contracts/Models/ScanProfile.cs`) existe dans le
  modèle mais n'est **jamais utilisé** — aucune conversion n'a lieu nulle part.
- **Aucun endpoint HTTP ne sert les octets d'une image.** `GET /api/v1/scans/{id}/pages/{pageId}`
  (`src/OpenScan.Agent/Program.cs`) renvoie uniquement du JSON avec `fileReference` = chemin de
  fichier local sur le disque de l'Agent. Un navigateur distant ne peut rien afficher avec ça.
  **C'est le vrai bloquant** pour l'objectif "affichage temps réel sur le web", à corriger même
  avant la question de la conversion elle-même.

## Ce qui doit être livré

### 1. Nouvel endpoint qui sert réellement les octets d'image (priorité n°1)

Dans `src/OpenScan.Agent/Program.cs`, ajouter (à côté des routes `/scans/{id}/pages/*`
existantes) :

```
GET /api/v1/scans/{id}/pages/{pageId}/image
```

Doit streamer le contenu du fichier référencé par `ScanPage.FileReference` avec le bon
`Content-Type` (`image/jpeg` une fois la conversion en place — voir point 2), via
`Results.File(...)` ou `Results.Stream(...)` d'ASP.NET Core minimal API. Vérifier que la page
appartient bien au job `{id}` (même contrôle que l'endpoint JSON existant). Cet endpoint doit
fonctionner en streaming (ne pas charger tout le fichier en mémoire avant de répondre — cohérent
avec la contrainte "jamais un lot entier en mémoire" déjà appliquée ailleurs dans le moteur).

### 2. Conversion JPEG immédiate à l'arrivée de chaque page

Ajouter la dépendance **SixLabors.ImageSharp** (licence Split — gratuite sous 1M$ de revenu
annuel de l'entité utilisatrice, payante au-delà ; déjà validé avec l'utilisateur, ne pas
redemander confirmation).

Où : soit directement dans `JobManager.ConsumeAsync` (le plus simple, cohérent avec l'existant),
soit en extrayant un petit composant dédié (ex. `OpenScan.Pipeline` — actuellement un projet
stub vide, c'est exactement sa vocation prévue par l'architecture, voir section 5.1 et 10 du
cahier des charges : `Acquisition → Bounded Queue → Processing → Compression → Persistence →
Output`). Choisir l'option la plus simple à livrer vite ; ne pas sur-architecturer.

Étapes par page acquise, avant persistance :
1. Décoder le flux brut reçu du backend (`AcquiredPage.Data`) via ImageSharp
   (`Image.Load(stream)` — ImageSharp détecte le format automatiquement, PNM/BMP sont tous deux
   supportés nativement).
2. Encoder immédiatement en JPEG (`image.SaveAsJpeg(outputStream, new JpegEncoder { Quality =
   85 })` ou équivalent) — qualité ~85 est un bon compromis vitesse/poids/lisibilité pour du
   document scanné, à ajuster si besoin mais ne pas sur-optimiser sans mesure (règle R14 : les
   performances doivent être mesurées avant optimisation).
3. Persister le JPEG résultant via `IPageStorage.SaveAsync` comme c'est déjà fait aujourd'hui
   pour le brut — remplace le fichier brut par le JPEG (pas besoin de garder les deux sauf si
   explicitement demandé plus tard).
4. Mettre à jour `ScanPage` en conséquence (`PixelFormat`, dimensions si elles changent après
   normalisation, `FileReference` pointant vers le JPEG).

**Mesurer** le temps de conversion par page (log simple suffit, pas besoin d'un système de
métriques complet à ce stade — section 20 du cahier des charges le demande globalement mais
n'est pas dans le périmètre de cette tâche) pour valider que "très très rapide" est bien atteint
en pratique (objectif indicatif : quelques dizaines de millisecondes par page A4 300dpi, pas
plusieurs secondes).

### 3. Backend Mock : produire une image décodable

`MockScannerBackend.GeneratePageData` (`src/OpenScan.Backends.Mock/MockScannerBackend.cs`)
génère actuellement des octets arbitraires (motif déterministe, pas une image valide). Pour que
Mock exerce le même chemin de conversion que les vrais backends (et reste utile pour les tests
bout-en-bout), le faire générer une image BMP ou PNM minimale mais valide (dimensions
cohérentes avec `Width`/`Height`/`PixelFormat` déjà déclarés dans `AcquiredPage`) plutôt que du
bruit. Pas besoin d'un contenu visuel élaboré, juste un fichier structurellement valide que
ImageSharp peut décoder sans erreur.

### 4. Export PDF à la demande (secondaire, pas dans le chemin temps réel)

Nouvel endpoint :

```
GET /api/v1/scans/{id}/export?format=pdf
```

Assemble les JPEG déjà produits (étape 2) en un PDF multi-pages, **sans re-décoder les images**
(les octets JPEG peuvent être embarqués directement dans un flux PDF via le filtre `DCTDecode`
du format PDF — c'est standard et rapide, pas besoin de convertir vers un format intermédiaire).

Ne pas ajouter de nouvelle dépendance NuGet lourde uniquement pour ça si évitable : un
assembleur PDF minimal (un seul objet Catalog, un objet Pages, un objet Page + un XObject Image
par page scannée, table xref) est un morceau de code raisonnablement petit et autonome pour un
usage "image-only, pas de texte". Si une bibliothèque légère et correctement licenciée (MIT/
Apache, pas de restriction de revenu) est plus rapide à livrer correctement, c'est acceptable —
mais vérifier la licence avant de l'ajouter et le signaler dans le rapport final, même logique de
vigilance que pour ImageSharp/NAPS2 (règle R13 du cahier des charges).

Cet endpoint peut être plus lent que l'affichage temps réel par page — ce n'est pas son objectif,
ne pas sacrifier la simplicité du code pour l'optimiser prématurément.

## Contraintes à respecter (rappel, déjà appliquées ailleurs dans le repo)

- Ne pas casser les 87 tests existants.
- Ajouter des tests pour la nouvelle logique : au minimum, un test qui vérifie qu'une page Mock
  (une fois corrigée point 3) passe par la conversion et produit un JPEG valide et décodable, et
  un test pour l'endpoint `/image` (statut 200, `Content-Type: image/jpeg`, contenu non vide).
- Respecter le style déjà en place : logique de conversion pure/testable séparée de la plomberie
  I/O quand c'est raisonnable, pas de sur-ingénierie.
- Le pipeline reste en flux (pas de chargement de tout un lot en mémoire) — la conversion d'UNE
  page à la fois en mémoire est acceptable (c'est déjà le cas ailleurs, ex. lecture d'en-tête
  BMP/PNM), le problème à éviter est de charger TOUT le lot d'un job simultanément.

## Livrable attendu

- Endpoint `GET /api/v1/scans/{id}/pages/{pageId}/image` fonctionnel, streaming, bon
  Content-Type.
- Conversion JPEG automatique et rapide de chaque page acquise, pour tous les backends y compris
  Mock (corrigé pour produire une image valide).
- Endpoint `GET /api/v1/scans/{id}/export?format=pdf` produisant un PDF multi-pages valide à
  partir des JPEG déjà produits.
- Tests couvrant les deux endpoints et la conversion.
- Note sur le temps de conversion mesuré par page (log ou commentaire dans le rapport final),
  pour confirmer que l'objectif de rapidité est atteint en pratique et pas seulement en théorie.
