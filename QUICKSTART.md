# Kairos Scan Engine — Intégration rapide

L'Agent est un service local installé sur le poste de l'utilisateur. Il pilote les scanners
(TWAIN, WIA, SANE, eSCL) et expose une API HTTP + WebSocket **sur la machine elle-même**.
Votre application Web l'appelle **depuis le navigateur**, jamais depuis votre serveur.

> Guide complet (statuts, codes d'erreur, reprise de job) : [`INTEGRATION.md`](./INTEGRATION.md)

## 1. Installer l'Agent sur le poste

| OS | Installation | Service |
|---|---|---|
| Windows | `OpenScanEngineSetup-X.Y.Z-x64.exe` (admin) | `OpenScanAgent`, démarrage auto |
| Debian/Ubuntu | `sudo apt install ./openscan-agent_X.Y.Z_amd64.deb` | `systemctl status openscan-agent` |

Paquets disponibles sur la page Releases du dépôt. Windows TWAIN : le pilote du fabricant doit
être installé (ex. PaperStream IP pour Fujitsu).

## 2. Comment le contacter

| | Valeur |
|---|---|
| API REST | `http://127.0.0.1:5276/api/v1` |
| WebSocket | `ws://127.0.0.1:5276/ws` |
| Écoute | `127.0.0.1` uniquement (inaccessible depuis le réseau) |
| CORS | toutes origines autorisées, Private Network Access géré |
| Authentification | aucune pour l'instant |
| Format | JSON camelCase ; **les enums (`status`) sont des entiers** |
| Swagger | `/swagger`, en mode Development seulement |

Une page servie en HTTPS peut appeler `http://127.0.0.1` : les navigateurs traitent le loopback
comme une origine de confiance.

Rendez l'URL configurable. Dans Kairos KM V3, c'est `VITE_URL_KAIROS_SCAN_ENGINE`.

```bash
curl http://127.0.0.1:5276/api/v1/health   # {"status":"ok","time":"..."}
```

## 3. Flux minimal

```
GET  /health                          → l'Agent est-il installé ?
GET  /scanners                        → liste des scanners
GET  /scanners/{id}/capabilities      → options réellement supportées
POST /scans                           → démarre un job
WS   /ws                              → page.persisted … scan.completed
GET  /scans/{id}/pages/{pageId}/image → JPEG d'une page (utilisable en <img src>)
GET  /scans/{id}/export?format=pdf    → PDF final
```

## 4. Exemple complet (JavaScript navigateur)

```javascript
const AGENT = "http://127.0.0.1:5276";
const API = `${AGENT}/api/v1`;

async function scanToPdf(onPage) {
  // 1. Présence de l'Agent (absent = cas normal à gérer dans l'UI)
  const ok = await fetch(`${API}/health`).then(r => r.ok).catch(() => false);
  if (!ok) throw new Error("Kairos Scan Engine n'est pas installé ou démarré sur ce poste");

  // 2. Scanner + capacités
  const [scanner] = await fetch(`${API}/scanners`).then(r => r.json());
  if (!scanner) throw new Error("Aucun scanner détecté");
  const caps = await fetch(`${API}/scanners/${encodeURIComponent(scanner.id)}/capabilities`).then(r => r.json());

  // 3. Ouvrir le WebSocket AVANT de lancer le job pour ne rater aucun événement
  const ws = new WebSocket(AGENT.replace("http", "ws") + "/ws");
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });

  // 4. Lancer le scan
  const res = await fetch(`${API}/scans`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      scannerId: scanner.id,
      profile: {
        id: crypto.randomUUID(),
        source: caps.sources.includes("adf") ? "adf" : caps.sources[0],
        resolution: caps.resolutions.includes(300) ? 300 : caps.resolutions[0],
        pixelFormat: "color",
        duplex: caps.duplex,
        outputFormat: "pdf",
      },
    }),
  });
  if (!res.ok) throw new Error((await res.json()).detail);
  const job = await res.json();

  // 5. Suivre le job ; le flux WS est partagé entre tous les jobs, donc filtrer sur jobId
  return new Promise((resolve, reject) => {
    ws.onmessage = async ({ data }) => {
      const evt = JSON.parse(data);
      if (evt.jobId !== job.id) return;
      if (evt.event === "page.persisted") {
        onPage?.(`${API}/scans/${job.id}/pages/${evt.pageId}/image`, evt.pageNumber);
      } else if (evt.event === "scan.completed") {
        ws.close();
        resolve(await fetch(`${API}/scans/${job.id}/export?format=pdf`).then(r => r.blob()));
      } else if (evt.event === "scan.error" || evt.event === "scan.cancelled") {
        ws.close();
        reject(new Error(evt.error?.message ?? evt.event));
      }
    };
  });
}

// Usage : afficher les pages au fil de l'eau, puis envoyer le PDF à votre backend
const pdf = await scanToPdf((url) => {
  const img = new Image(); img.src = url; document.body.append(img);
});
await fetch("/api/mes-documents", { method: "POST", body: pdf });
```

## 5. À savoir

- **Les images ne passent jamais par le WebSocket.** Il ne transporte que des métadonnées
  (`event`, `jobId`, `pageId`, `pageNumber`, `error`). Les octets se récupèrent en HTTP.
- **Proposez seulement ce que `capabilities` renvoie.** Chaque scanner est différent ; ne codez
  pas en dur de liste d'options.
- **Vérifiez les ajustements.** Le moteur peut ajuster un paramètre, par exemple 350 dpi demandé
  devient 300 dpi. Les écarts sont listés dans `job.effectiveConfiguration.adjustments`.
- **Erreurs au format Problem Details** :
  `{ "title": "ScannerBusy", "status": 409, "detail": "...", "recoverable": false }`.
  Testez `title`, pas le code HTTP.
- **Contrôle du job** : `POST /scans/{id}/cancel`, `/resume`, `/retry`.
- **Statuts de job** (entiers) : `3` = Running, `5` = Cancelled, `6` = Error,
  `7` = RecoverableError, `8` = Completed.
- **Doublons possibles sous Linux.** Un même scanner réseau peut remonter deux fois via SANE
  (`airscan` et `hpaio`). Dédoublonnez par nom si besoin ; V3 le fait dans `dedupeScanners`.
- **Pas de sécurité de session pour l'instant.** Toute page ouverte sur le poste peut appeler
  l'Agent. Un token ou un contrôle d'origine viendra plus tard ; ne présumez pas de son absence.

