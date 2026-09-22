# À implémenter — Sécurité de l'Agent (section 15 du cahier des charges)

Contexte complet ci-dessous, pas besoin de redemander quoi que ce soit à l'utilisateur.

## Pourquoi

Le moteur fonctionne de bout en bout (Mock/SANE/WIA/TWAIN testés sur matériel réel, conversion
JPEG temps réel + export PDF opérationnels) mais **l'Agent n'a aucune sécurité applicative**.
Aujourd'hui, n'importe quel process/page qui atteint `127.0.0.1:5276` peut piloter un scanner
sans restriction. Ce n'est pas déployable face à une vraie application Web tierce en l'état.

Section 15 du cahier des charges, citée intégralement car c'est la spec à respecter :

> L'Agent doit écouter par défaut uniquement sur `127.0.0.1` et non sur toutes les interfaces
> réseau (déjà en place).
>
> La sécurité minimale doit comprendre :
> - token de session ;
> - validation stricte de l'origine ;
> - expiration des sessions ;
> - contrôle des origines autorisées ;
> - protection contre DNS rebinding ;
> - contrôle des méthodes HTTP ;
> - validation des paramètres ;
> - journalisation des accès ;
> - limitation des endpoints sensibles.
>
> Flux d'autorisation d'une application Web :
> Application Web → Demande d'accès → Agent → Validation/consentement → Origine autorisée →
> Session token → Accès au scanner.
>
> Aucune application Web inconnue ne doit pouvoir piloter silencieusement un scanner.

Critères de test associés (section 25.3) : origine non autorisée, token invalide, token expiré,
requêtes malformées, accès depuis une autre machine, tentative d'accès sans session, DNS
rebinding, accès concurrent, injection dans les paramètres.

## État actuel

- `OpenScan.Security` (`src/OpenScan.Security/`) est un **stub vide** (`Class1.cs`) depuis le
  début du projet — jamais implémenté, jamais référencé par l'Agent.
- `src/OpenScan.Agent/Program.cs` n'a **aucun middleware d'authentification/autorisation**. Tous
  les endpoints REST et le WebSocket `/ws` sont ouverts sans contrôle.
- SQLite (`src/OpenScan.Persistence/`) n'a pas de table `sessions` (le cahier des charges,
  section 18, liste `sessions` parmi les tables minimales attendues — actuellement seules
  `jobs`, `pages`, `profiles` existent, voir `SqliteSchema.cs`).

## Architecture à mettre en place

Respecter le principe déjà appliqué partout dans ce repo : **logique pure et testable dans
`OpenScan.Security`**, **plomberie ASP.NET Core (middleware, endpoints) dans `OpenScan.Agent`**.
Regarder `src/OpenScan.Backends.Sane/SaneOptionsParser.cs` vs `SaneScannerBackend.cs`, ou
`src/OpenScan.Pipeline/ImageSharpProcessor.cs` vs son branchement dans `Program.cs`/
`JobManager.cs`, comme modèles de cette séparation.

### 1. `OpenScan.Security` — logique pure

- **`SessionToken`** (record) : `Token` (string opaque, généré via
  `RandomNumberGenerator.GetHexString` ou équivalent cryptographiquement sûr — **pas**
  `Guid.NewGuid()`, qui n'est pas garanti imprévisible), `Origin`, `IssuedAt`, `ExpiresAt`.
- **`SessionManager`** : `IssueToken(origin)`, `ValidateToken(token, origin) ->
  SessionValidationResult` (valide / introuvable / expiré / origine ne correspond pas),
  `RevokeToken(token)`. Expiration configurable (proposer une durée par défaut raisonnable, ex.
  8h — ce n'est pas précisé dans le cahier des charges, documenter le choix).
- **`OriginValidator`** : `IsAllowed(originHeaderValue, allowedOrigins)` — comparaison stricte
  (schéma + host + port), pas de correspondance partielle/wildcard implicite.
- **`DnsRebindingGuard`** (ou nom équivalent) : `IsHostHeaderValid(hostHeaderValue,
  expectedHosts)` — le header `Host` d'une requête doit correspondre à `127.0.0.1:<port>` ou
  `localhost:<port>` (configurable), sinon rejet. C'est la défense standard contre le DNS
  rebinding : un attaquant fait résoudre un domaine vers 127.0.0.1 après coup, mais le header
  `Host` révèle toujours son propre nom de domaine, pas "localhost"/"127.0.0.1".
- **`ISessionStore`** (interface) + une implémentation **`SqliteSessionStore`** dans
  `OpenScan.Persistence` (même pattern que `SqliteJobRepository`/`SqliteProfileRepository` via
  Dapper) — persiste les sessions pour qu'elles survivent à un redémarrage de l'Agent, et ajoute
  la table `sessions` manquante à `SqliteSchema.cs`. Garder aussi une implémentation en mémoire
  (`InMemorySessionStore`, `ConcurrentDictionary`) si utile pour les tests, à l'image
  d'`InMemoryEventBus`.

Toute cette couche doit être testable **sans ASP.NET Core ni HTTP réel** — passer des chaînes en
entrée, vérifier les résultats en sortie.

### 2. `OpenScan.Agent` — endpoints et middleware

**Nouvel endpoint d'obtention de session** :

```
POST /api/v1/auth/request
```

Body : `{}` ou rien (l'origine vient du header `Origin` de la requête HTTP elle-même, pas d'un
champ du body — ne jamais faire confiance à une origine déclarée dans le body). Comportement :
1. Lire le header `Origin` de la requête.
2. Le valider via `OriginValidator` contre la liste configurée (`appsettings.json`, section
   `Security:AllowedOrigins`, ex. `["http://localhost:3000"]` — vide/absente par défaut, donc
   **aucune origine autorisée tant que l'utilisateur ne configure rien explicitement** :
   fail-closed, jamais fail-open).
3. Si autorisée : émettre un `SessionToken` via `SessionManager`, le persister via
   `ISessionStore`, le retourner (`{"token": "...", "expiresAt": "..."}`).
4. Si non autorisée : `403 Forbidden`, journaliser la tentative (voir journalisation ci-dessous).

**Middleware (dans cet ordre, avant le routing des endpoints protégés)** :

1. **Garde anti DNS-rebinding** : vérifie le header `Host` sur **toutes** les requêtes (y
   compris `/api/v1/auth/request` et `/health`) via `DnsRebindingGuard`. Rejet immédiat (400) si
   invalide, avant tout autre traitement.
2. **Authentification de session** : pour tous les endpoints **sauf** `/health` et
   `/api/v1/auth/request`, exiger un token de session valide. Convention : header
   `Authorization: Bearer <token>`. Vérifier via `SessionManager.ValidateToken(token, origin)`
   où `origin` est le header `Origin` de la requête courante (le token doit avoir été émis pour
   cette origine précise — pas transférable entre origines). Sans token ou token invalide/expiré
   → `401 Unauthorized`. Origine ne correspondant pas au token → `403 Forbidden`.

**Cas particulier WebSocket `/ws`** : un `WebSocket` créé côté navigateur (`new
WebSocket(url)`) ne peut pas positionner de header `Authorization` personnalisé. Convention
standard pour ce cas : accepter le token en paramètre de requête (`/ws?token=...`), validé de la
même façon que le header ailleurs. Documenter ce choix dans le code (c'est un compromis connu et
accepté pour l'auth WebSocket, pas une faille — le token reste dans l'URL de la requête HTTP
d'upgrade, jamais dans le corps de la connexion WebSocket établie).

**Contrôle des méthodes HTTP** : s'assurer qu'aucune méthode non mappée n'est acceptée sur les
routes existantes (ASP.NET Core minimal API le fait déjà par défaut — vérifier qu'aucune route
n'utilise un verbe générique/permissif par erreur).

**Validation des paramètres** : ajouter des gardes explicites sur les paramètres de route/requête
là où c'est encore absent (ex. `pageId`/`id` non vides, `format` sur `/scans/{id}/export` déjà
validé — vérifier la cohérence sur les autres endpoints). Ne pas sur-ingénierer un framework de
validation générique, des checks ciblés suffisent.

**Journalisation des accès** : logger chaque requête (méthode, chemin, origine, résultat
d'authentification, code de statut) via `ILogger` — réutiliser le mécanisme déjà en place pour
`ITwainLogger`/`AspNetTwainLogger` comme référence de style d'intégration `ILogger` dans ce repo.
Un middleware simple suffit, pas besoin d'un système de logs structurés complet (hors périmètre
de cette tâche, section 19 plus large).

**Limitation des endpoints sensibles** : au minimum, `POST /scans` (démarre un job = pilote
physiquement un scanner), `POST /scans/{id}/cancel`, et l'accès aux images/export de pages
doivent être derrière l'authentification de session. `GET /health` reste public (nécessaire pour
un simple ping de diagnostic sans négociation préalable).

### 3. Configuration

Dans `appsettings.json` / `appsettings.Development.json` de l'Agent, ajouter une section :

```json
"Security": {
  "AllowedOrigins": [],
  "AllowedHosts": ["127.0.0.1", "localhost"],
  "SessionLifetime": "08:00:00"
}
```

Lue via les options ASP.NET Core standard (`IOptions<SecurityOptions>`), pas de valeurs codées
en dur dans `Program.cs`.

## Contraintes à respecter (rappel, déjà appliquées ailleurs dans le repo)

- Ne pas casser les 99 tests existants.
- Fail-closed partout : en cas de doute/config absente, refuser plutôt qu'autoriser.
- Séparer logique pure (testable sans HTTP) et plomberie ASP.NET Core, comme partout ailleurs
  dans ce repo.
- Le store de sessions doit être thread-safe (accès concurrents) — s'inspirer de
  `ConcurrentDictionary` déjà utilisé dans `DeviceManager`/`InMemoryEventBus`/backends.
- Ne jamais logger le token de session en clair dans les journaux d'accès (logger un hash ou les
  premiers caractères seulement, jamais la valeur complète — un log qui fuit un token annule sa
  protection).
- Ajouter des tests pour : émission de token, validation (token valide/invalide/expiré/origine ne
  correspondant pas), garde anti DNS-rebinding (host valide/invalide), et au moins un test
  d'intégration bout-en-bout vérifiant qu'un appel à `POST /api/v1/scans` sans token est bien
  rejeté (401) alors qu'avec un token valide obtenu via `/auth/request`, il passe.

## Livrable attendu

- `OpenScan.Security` avec `SessionToken`, `SessionManager`, `OriginValidator`,
  `DnsRebindingGuard`, `ISessionStore` — tout testable sans HTTP.
- `SqliteSessionStore` dans `OpenScan.Persistence`, table `sessions` ajoutée à `SqliteSchema.cs`.
- `POST /api/v1/auth/request` fonctionnel, fail-closed par défaut (aucune origine autorisée sans
  configuration explicite).
- Middleware d'authentification appliqué aux endpoints sensibles, `/health` et `/auth/request`
  exemptés.
- `/ws` accepte le token en paramètre de requête, documenté comme choix délibéré.
- Configuration via `appsettings.json` (`Security:AllowedOrigins`, `AllowedHosts`,
  `SessionLifetime`), pas de valeurs codées en dur.
- Journalisation des accès (méthode/chemin/origine/résultat), sans jamais logger un token en
  clair.
- Tests couvrant les scénarios de la section 25.3 listés ci-dessus.
