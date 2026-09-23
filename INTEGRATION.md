# Guide d'intégration — Kairos Scan Engine

Ce guide s'adresse aux développeurs qui intègrent une application Web (ou tout client HTTP) avec
l'**Agent Kairos Scan Engine** déjà installé et tournant sur le poste de l'utilisateur. Pour
construire ou contribuer au moteur lui-même, voir [`README.md`](./README.md).

## Prérequis côté utilisateur final

L'Agent doit être **installé et démarré en arrière-plan** sur la machine de l'utilisateur (voir
`scripts/install-windows.ps1` / `scripts/install-linux.sh`) — il tourne en permanence comme un
service système, pas besoin de le lancer. Votre application Web ne fait qu'appeler son API HTTP
locale.

## Ce qu'il faut savoir avant de coder

- **URL de base** : `http://127.0.0.1:5276/api/v1` — l'Agent n'écoute **que** sur `127.0.0.1`,
  jamais accessible depuis un autre poste du réseau. Votre application doit tourner dans le
  navigateur de l'utilisateur (JS côté client), pas depuis votre serveur applicatif distant.
- **Pas encore de sécurité de session** (token, origine autorisée) à ce stade — n'importe quelle
  page ouverte sur le poste peut actuellement appeler l'Agent. Cette contrainte sera resserrée
  plus tard (section 15 du cahier des charges) ; ne codez pas en dur d'hypothèse contraire.
- **JSON en camelCase** partout (`scannerId`, `pageNumber`, `createdAt`...).
- **Les enums** (`status` de job/page) sont sérialisés en **entier**, pas en chaîne — voir la
  table de correspondance plus bas.
- **Les erreurs** suivent le format [RFC 9110 Problem Details](https://www.rfc-editor.org/rfc/rfc9110)
  (`application/problem+json`) : `{"title": "ScannerBusy", "status": 409, "detail": "...",
  "recoverable": false}`. **Faites correspondre vos erreurs sur `title`** (le nom du code
  d'erreur), pas sur une valeur numérique — voir la table des codes plus bas.

## Flux d'intégration typique

```
1. Vérifier que l'Agent répond          GET  /health
2. Découvrir les scanners disponibles   GET  /scanners
3. Lire les capacités réelles           GET  /scanners/{id}/capabilities
4. (Optionnel) Créer un profil réutilisable   POST /profiles
5. Lancer un scan                       POST /scans
6. Suivre la progression en temps réel  WebSocket /ws  (ou polling GET /scans/{id})
7. Afficher chaque page dès qu'elle arrive   GET /scans/{id}/pages/{pageId}/image
8. Exporter le document complet en PDF  GET /scans/{id}/export?format=pdf
```

### 1–2. Vérifier la présence de l'Agent et lister les scanners

```javascript
const BASE_URL = "http://127.0.0.1:5276/api/v1";

async function isAgentAvailable() {
  try {
    const res = await fetch(`${BASE_URL}/health`);
    return res.ok;
  } catch {
    return false; // Agent non installé/démarré sur ce poste
  }
}

async function listScanners() {
  const res = await fetch(`${BASE_URL}/scanners`);
  return res.json(); // Scanner[]
}
```

Toujours prévoir le cas `isAgentAvailable() === false` dans votre UI (message "veuillez installer
Kairos Scan Engine" avec un lien vers l'installateur) — un utilisateur sans l'Agent installé, ou
avec un antivirus qui bloque le port local, est un cas normal à gérer, pas une exception.

### 3. Lire les capacités réelles avant de proposer des options à l'utilisateur

```javascript
async function getCapabilities(scannerId) {
  const res = await fetch(`${BASE_URL}/scanners/${encodeURIComponent(scannerId)}/capabilities`);
  if (!res.ok) throw await toApiError(res);
  return res.json();
  // { sources: ["adf","flatbed"], duplex: true, resolutions: [150,300,600],
  //   pixelFormats: ["color","grayscale","bw"], adf: true, flatbed: false, ... }
}
```

**Ne proposez à l'utilisateur que ce que `capabilities` confirme réellement supporté** — ne
codez jamais en dur une liste d'options "standard", chaque scanner est différent (c'est tout le
principe du moteur, section 12 du cahier des charges).

### 4–5. Créer un profil (optionnel) et lancer un scan

```javascript
async function startScan(scannerId, { source = "adf", resolution = 300, pixelFormat = "color", duplex = false } = {}) {
  const res = await fetch(`${BASE_URL}/scans`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      scannerId,
      profile: { id: crypto.randomUUID(), source, resolution, pixelFormat, duplex, outputFormat: "pdf" }
    })
  });
  if (!res.ok) throw await toApiError(res);
  return res.json(); // ScanJob, status initial = Running (3)
}
```

Le profil peut aussi être créé une fois via `POST /profiles` puis référencé par `profileId` au
lieu de `profile` dans le corps de `/scans` — pratique pour un profil "Facture" réutilisé souvent.

**La résolution demandée peut être ajustée** par le moteur si le scanner ne la supporte pas
exactement (ex. 350 dpi demandé → 300 dpi réellement appliqué). Vérifiez toujours
`job.effectiveConfiguration.adjustments` pour informer l'utilisateur d'un écart silencieux :

```javascript
if (job.effectiveConfiguration.adjustments.length > 0) {
  console.warn("Ajustements appliqués :", job.effectiveConfiguration.adjustments);
  // [{ parameter: "resolution", requested: 350, effective: 300, reason: "nearest_supported" }]
}
```

### 6. Suivre la progression en temps réel (WebSocket, recommandé)

```javascript
function watchJob(jobId, { onPage, onComplete, onError }) {
  const ws = new WebSocket("ws://127.0.0.1:5276/ws");

  ws.onmessage = (event) => {
    const evt = JSON.parse(event.data);
    if (evt.jobId !== jobId) return; // le flux est partagé entre tous les jobs de l'Agent

    switch (evt.event) {
      case "page.persisted":
        onPage?.(evt.pageId, evt.pageNumber);
        break;
      case "scan.completed":
        onComplete?.();
        ws.close();
        break;
      case "scan.error":
      case "scan.cancelled":
        onError?.(evt.error ?? { title: evt.event });
        ws.close();
        break;
    }
  };

  return ws; // conserver la référence pour pouvoir ws.close() si l'utilisateur navigue ailleurs
}
```

**Événements disponibles** : `scan.created`, `scan.validating`, `scan.started`, `scan.cancelled`,
`scan.completed`, `scan.error`, `page.acquired`, `page.persisted`, `page.error`. **Les données
binaires (images) ne transitent jamais par le WebSocket** — seulement les métadonnées ; récupérez
l'image via l'endpoint HTTP dédié (étape 7).

Alternative sans WebSocket : `setInterval` sur `GET /scans/{id}` et comparer `processedPages`/
`status` — fonctionne mais moins réactif, à éviter si l'objectif est un affichage temps réel.

### 7. Afficher chaque page dès qu'elle est prête

C'est le point conçu pour être **rapide et sans latence perceptible** (conversion JPEG immédiate
côté Agent dès l'acquisition de chaque page) :

```javascript
function displayPage(jobId, pageId) {
  const img = document.createElement("img");
  img.src = `${BASE_URL}/scans/${jobId}/pages/${pageId}/image`;
  document.getElementById("pages-container").appendChild(img);
}

// Combiné avec le WebSocket :
watchJob(job.id, {
  onPage: (pageId) => displayPage(job.id, pageId),
  onComplete: () => console.log("Scan terminé"),
  onError: (err) => showError(err)
});
```

L'URL de `/image` peut être utilisée **directement comme `src` d'une balise `<img>`** — pas besoin
de `fetch` + `URL.createObjectURL` sauf si vous avez besoin des octets bruts (upload vers votre
serveur, par exemple).

### 8. Exporter le document complet en PDF

```javascript
async function downloadPdf(jobId) {
  const res = await fetch(`${BASE_URL}/scans/${jobId}/export?format=pdf`);
  if (!res.ok) throw await toApiError(res);
  const blob = await res.blob();
  const url = URL.createObjectURL(blob);
  // soit déclencher un téléchargement, soit l'uploader vers votre serveur applicatif :
  await fetch("https://votre-serveur.example.com/api/documents", { method: "POST", body: blob });
  URL.revokeObjectURL(url);
}
```

Appelez cet endpoint **une fois le job `Completed`** (via l'événement `scan.completed` ou en
vérifiant `job.status === 8`) — il assemble les pages déjà converties, pas besoin d'attendre
davantage après.

## Annuler, reprendre, relancer un job

```javascript
await fetch(`${BASE_URL}/scans/${jobId}/cancel`, { method: "POST" });

// Un job Cancelled ou RecoverableError peut reprendre là où il s'est arrêté :
await fetch(`${BASE_URL}/scans/${jobId}/resume`, { method: "POST" });

// Un job Error ou RecoverableError peut être relancé (les pages déjà acquises sont conservées) :
await fetch(`${BASE_URL}/scans/${jobId}/retry`, { method: "POST" });
```

`resume`/`retry` renvoient `409 Conflict` (`title: "InvalidJobState"`) si le job n'est pas dans un
état éligible — un job `Completed` ne peut pas être repris, par exemple. Toujours vérifier
`job.status` avant de proposer ces actions dans votre UI.

## Table des statuts (`ScanJob.status`, entier)

| Valeur | Nom | Signification |
|---|---|---|
| 0 | Created | Job créé, validation pas encore lancée |
| 1 | Validating | Validation du profil contre les capacités en cours |
| 2 | Ready | Validé, configuration en cours d'application au scanner |
| 3 | Running | Acquisition en cours |
| 4 | Paused | Réservé, non utilisé actuellement |
| 5 | Cancelled | Annulé par l'utilisateur — éligible à `resume` |
| 6 | Error | Échec non récupérable — éligible à `retry` |
| 7 | RecoverableError | Échec récupérable (bourrage, etc., ou Agent redémarré en plein job) — éligible à `resume` et `retry` |
| 8 | Completed | Terminé avec succès |

`ScanPage.status` suit la même logique (0=Acquired, 1=Processing, 2=Ready, 3=Persisted,
4=Error, 5=NotAcquired) — en pratique, une page affichable a toujours `status === 3`.

## Table des codes d'erreur (`title` dans le corps Problem Details)

| `title` | HTTP | Sens | Action recommandée côté client |
|---|---|---|---|
| `ScannerNotFound` | 404 | ID de scanner inconnu | Relancer `GET /scanners` (peut avoir été débranché) |
| `JobNotFound` | 404 | ID de job inconnu | Vérifier l'ID, ne pas réessayer |
| `ScannerBusy` | 409 | Un autre job utilise déjà ce scanner | Réessayer plus tard, ou informer l'utilisateur |
| `InvalidJobState` | 409 | `resume`/`retry` appelé sur un job dans un état non éligible | Vérifier `job.status` avant l'appel |
| `CapabilityUnsupported` | 422 | Le profil demande une option que le scanner ne supporte pas | Relire `GET .../capabilities`, ajuster le profil |
| `InvalidConfiguration` | 422 | Profil malformé | Corriger les paramètres envoyés |
| autres (`DriverError`, `PaperJam`, `NoPaper`, `MultiFeed`, `AcquisitionError`...) | 500 | Erreur matérielle/pilote | Afficher `detail` à l'utilisateur ; vérifier `recoverable` pour savoir si `retry` a un sens |

```javascript
async function toApiError(res) {
  const body = await res.json().catch(() => null);
  return new Error(body?.detail ?? `Erreur HTTP ${res.status}`);
}
```

## Résumé des endpoints

| Méthode | Route | Usage |
|---|---|---|
| GET | `/health` | Vérifier que l'Agent tourne |
| GET | `/scanners` | Lister les scanners détectés |
| GET | `/scanners/{id}/capabilities` | Capacités réelles avant de construire un profil |
| POST | `/profiles` | Créer un profil réutilisable |
| POST | `/scans` | Démarrer un job (`{scannerId, profile}` ou `{scannerId, profileId}`) |
| GET | `/scans/{id}` | Statut courant du job |
| GET | `/scans/{id}/pages/{pageId}/image` | Octets JPEG d'une page — utilisable en `<img src>` |
| GET | `/scans/{id}/export?format=pdf` | Export PDF complet du job |
| POST | `/scans/{id}/cancel` \| `/resume` \| `/retry` | Contrôle du cycle de vie |
| WS | `/ws` | Événements temps réel (voir section 6) |

Pour le détail complet des modèles (`ScanProfile`, `ScannerCapabilities`...), voir
`src/OpenScan.Contracts/Models/` dans le dépôt.
