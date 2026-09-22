# À implémenter — Backend eSCL (section 3.1, priorité PoC)

Contexte complet ci-dessous, pas besoin de redemander quoi que ce soit à l'utilisateur.

## Pourquoi

eSCL (aussi appelé "AirScan", protocole ouvert porté par Apple/Mopria, largement adopté par les
imprimantes/scanners réseau modernes — HP, Canon, Epson, Brother...) est le **seul backend
encore officiellement prioritaire du cahier des charges qui manque entièrement** (section 3.1 :
c'est LE backend du PoC, avant même WIA/SANE/TWAIN qui sont eux déjà livrés et testés sur
matériel réel). `src/OpenScan.Backends.Escl/` est resté un stub vide depuis le début du projet.

**Important, à dire clairement dans le rapport final** : contrairement à SANE/WIA/TWAIN qui ont
pu être validés sur un vrai Fujitsu fi-7030, **aucun scanner/imprimante réseau compatible eSCL
n'est disponible dans cet environnement de développement**. Cette implémentation sera donc écrite
à partir de la spécification du protocole, non testée en conditions réelles — exactement la
situation initiale de WIA/TWAIN avant leur validation ultérieure sur la machine Windows de
l'utilisateur. La règle **R12** du cahier des charges ("toute nouvelle fonctionnalité
d'acquisition doit être testée sur scanner réel avant d'être déclarée compatible") s'applique :
ne jamais prétendre que ce backend est "validé", seulement "implémenté, en attente de test réel".

## Le protocole eSCL en bref

HTTP + XML, pas de SDK propriétaire. Namespaces XML à connaître :
- `scan` = `http://schemas.hp.com/imaging/escl/2011/05/03`
- `pwg` = `http://www.pwg.org/schemas/2010/12/sm`

**1. Capacités** — `GET http://{host}:{port}/eSCL/ScannerCapabilities` → XML :

```xml
<scan:ScannerCapabilities xmlns:scan="..." xmlns:pwg="...">
  <pwg:Version>2.0</pwg:Version>
  <pwg:MakeAndModel>Example MFP</pwg:MakeAndModel>
  <pwg:SerialNumber>ABC123</pwg:SerialNumber>
  <scan:Platen>
    <scan:PlatenInputCaps>
      <scan:MaxWidth>2551</scan:MaxWidth>   <!-- en 1/300 pouce -->
      <scan:MaxHeight>3508</scan:MaxHeight>
      <scan:SettingProfiles>
        <scan:SettingProfile>
          <scan:ColorModes>
            <scan:ColorMode>BlackAndWhite1</scan:ColorMode>
            <scan:ColorMode>Grayscale8</scan:ColorMode>
            <scan:ColorMode>RGB24</scan:ColorMode>
          </scan:ColorModes>
          <scan:DocumentFormats>
            <pwg:DocumentFormat>image/jpeg</pwg:DocumentFormat>
          </scan:DocumentFormats>
          <scan:SupportedResolutions>
            <scan:DiscreteResolutions>
              <scan:DiscreteResolution>
                <scan:XResolution>300</scan:XResolution>
                <scan:YResolution>300</scan:YResolution>
              </scan:DiscreteResolution>
              <!-- une entrée par résolution supportée -->
            </scan:DiscreteResolutions>
          </scan:SupportedResolutions>
        </scan:SettingProfile>
      </scan:SettingProfiles>
    </scan:PlatenInputCaps>
  </scan:Platen>
  <scan:Adf>
    <scan:AdfSimplexInputCaps> <!-- même structure que PlatenInputCaps --> </scan:AdfSimplexInputCaps>
    <scan:AdfDuplexInputCaps> <!-- présent seulement si le duplex est supporté --> </scan:AdfDuplexInputCaps>
    <scan:FeederCapacity>50</scan:FeederCapacity>
  </scan:Adf>
</scan:ScannerCapabilities>
```

Présence de `<scan:Platen>` → flatbed supporté. Présence de `<scan:Adf>` → ADF supporté.
Présence de `<scan:AdfDuplexInputCaps>` → duplex supporté. **Ne pas supposer une structure rigide
: les implémentations varient légèrement selon les fabricants — parser en tolérant les éléments
absents plutôt que de lever une exception, et ignorer les éléments/namespaces inconnus.**

**2. Créer un job** — `POST http://{host}/eSCL/ScanJobs`, `Content-Type: text/xml`, corps :

```xml
<scan:ScanSettings xmlns:scan="..." xmlns:pwg="...">
  <pwg:Version>2.0</pwg:Version>
  <pwg:InputSource>Platen</pwg:InputSource>  <!-- ou "Feeder" pour l'ADF -->
  <scan:ColorMode>RGB24</scan:ColorMode>      <!-- BlackAndWhite1 / Grayscale8 / RGB24 -->
  <scan:XResolution>300</scan:XResolution>
  <scan:YResolution>300</scan:YResolution>
  <pwg:DocumentFormat>image/jpeg</pwg:DocumentFormat>
  <scan:Duplex>false</scan:Duplex>
</scan:ScanSettings>
```

Réponse `201 Created` avec un header `Location` = URL du job (ex.
`http://host/eSCL/ScanJobs/<uuid>`). Cette URL est réutilisée pour les étapes suivantes.

**3. Récupérer les pages** — `GET {jobLocation}/NextDocument`, répété une fois par page. Réponse
`200 OK` avec le contenu binaire de la page (`Content-Type` = celui demandé, ex. `image/jpeg` —
déjà une vraie image encodée, pas besoin de la décoder soi-même côté backend). **Quand il n'y a
plus de page (fin de lot ADF), le serveur répond `404 Not Found`** — exactement le même principe
que "out of documents" (SANE) / `WIA_ERROR_PAPER_EMPTY` (WIA) / `TWCC_NOMEDIA` (TWAIN) déjà géré
dans les trois autres backends : **c'est une fin de lot normale, pas une erreur**, à traiter de
la même façon (voir `SaneScannerBackend.AcquireAsync`/`WiaScannerBackend.AcquireAsync` comme
modèles directs de ce pattern déjà éprouvé dans ce repo).

**4. Annuler** — `DELETE {jobLocation}`.

**5. Statut (optionnel, best-effort)** — `GET http://{host}/eSCL/ScannerStatus` → XML avec l'état
global de l'appareil. Pas bloquant pour une première version ; `GetStatusAsync` peut se contenter
d'un suivi local (busy/idle) comme le font déjà Sane/Wia/Twain, cohérent avec le reste du repo.

## Découverte : version MVP sans mDNS

La découverte "officielle" eSCL se fait via mDNS/Bonjour (annonce `_uscan._tcp.local`). **Ne pas
implémenter ça dans cette première passe** — le multicast est souvent bloqué en environnement
sandboxé/cloud, ça ajoute une dépendance NuGet et une complexité protocolaire (parsing DNS brut)
disproportionnée pour un premier livrable non testable ici de toute façon.

**Version MVP retenue** : liste d'hôtes connus, configurée explicitement (comme un futur
`Security:AllowedOrigins`, même style de config) :

```json
"Escl": {
  "KnownDevices": ["192.168.1.50", "printer.local:8080"]
}
```

`DiscoverAsync` interroge `GET /eSCL/ScannerCapabilities` sur chaque hôte configuré ; un hôte
injoignable est ignoré silencieusement (ne doit jamais faire échouer la découverte des autres —
même principe que `DeviceManager.DiscoverAllAsync` qui isole déjà les échecs par backend). Si tu
identifies une bibliothèque mDNS légère et correctement licenciée (MIT/Apache, pas de
restriction de revenu type Split License) et que l'ajouter reste simple, tu peux l'ajouter en
complément — mais ce n'est pas requis pour ce livrable, ne pas bloquer dessus. Si ajoutée,
signaler le choix de licence dans le rapport final (même vigilance que pour ImageSharp).

## Architecture à respecter (déjà établie partout dans ce repo)

Séparer logique pure (parsing/génération XML, testable sans réseau) et plomberie HTTP
(`HttpClient`), comme `SaneOptionsParser` vs `SaneScannerBackend`, ou `TwainCapabilityMapper`/
`TwainErrorMapper` vs `TwainScannerBackend`.

Fichiers suggérés dans `src/OpenScan.Backends.Escl/` :
- **`EsclCapabilitiesParser.cs`** — parse le XML de `ScannerCapabilities` vers un modèle brut,
  puis mappe vers `ScannerCapabilities` (Sources ADF/flatbed, Duplex, Resolutions, PixelFormats,
  BitDepths). Pur, testable avec une chaîne XML fixture (comme
  `SaneOptionsParserTests`/`WiaCapabilityMapperTests`).
- **`EsclScanSettingsBuilder.cs`** — construit le XML `ScanSettings` à partir de
  `EffectiveConfiguration`. Pur, testable (vérifier le XML généré contient les bonnes valeurs).
- **`EsclErrorMapper.cs`** — mappe les statuts HTTP (`503` = occupé, `404` hors contexte
  `NextDocument` = scanner/job introuvable, `409`/`400` = requête invalide...) vers `ErrorCode`.
  Pur, testable avec des entiers en entrée.
- **`EsclScannerBackend.cs`** — implémente `IScannerBackend` avec `HttpClient` (injecté via
  `IHttpClientFactory`, pas `new HttpClient()` en dur — évite l'épuisement de sockets, pattern
  standard ASP.NET Core). Utilise les trois classes ci-dessus.

`AcquireAsync` : boucle `GET {jobLocation}/NextDocument`, `404` → `yield break` (fin normale),
autre erreur → `ScanEngineException` via `EsclErrorMapper`. Les octets reçus sont déjà au format
demandé (JPEG typiquement) — les mettre directement dans `AcquiredPage.Data`. Ne pas essayer de
lire une largeur/hauteur précise depuis la réponse (ce n'est pas fourni de façon fiable par
eSCL) : renseigner `Width`/`Height` avec la meilleure estimation disponible (taille de page
demandée × résolution) ou `0` si incertain — `JobManager.ConsumeAsync` écrase de toute façon ces
valeurs avec celles réellement décodées par `ImageSharp` (déjà le cas pour tous les backends
existants, voir `page.Width = processed.Width` dans `JobManager.ConsumeAsync`).

## Contraintes à respecter (rappel, déjà appliquées ailleurs dans le repo)

- Ne pas casser les 115 tests existants.
- `EsclScannerBackend.ConfigureAsync` doit être implémenté (comme tous les autres backends) —
  résout et stocke les paramètres eSCL par job avant `AcquireAsync`.
- Ne jamais prétendre supporter une capacité non confirmée par les capacités réellement lues
  (section 12) — si `<scan:Adf>` est absent, ne pas exposer `adf` dans `ScannerCapabilities.Sources`.
- Timeout HTTP raisonnable sur les appels réseau (un scanner injoignable ne doit jamais bloquer
  indéfiniment) — utiliser `HttpClient.Timeout` ou un `CancellationToken` avec délai.
- Tests : parser/builder XML testables sans réseau (fixtures), plus un test simulant le flux
  complet (`POST ScanJobs` → `GET NextDocument` × N → `404`) via un `HttpMessageHandler` factice
  (pattern standard .NET pour tester du code `HttpClient` sans réseau réel — pas besoin d'un vrai
  serveur).

## Livrable attendu

- `EsclScannerBackend` implémentant `IScannerBackend`, enregistré dans
  `src/OpenScan.Agent/Program.cs` (même pattern que Mock/Sane/Wia/Twain).
- Découverte par liste d'hôtes configurés (`Escl:KnownDevices` dans `appsettings.json`).
- Parsing capacités + construction des paramètres de scan + mapping d'erreurs, tous purs et
  testés séparément.
- Gestion correcte de la fin de lot ADF (`404` sur `NextDocument` = normal, pas une erreur).
- Tests couvrant le parsing, la construction XML, le mapping d'erreurs, et le flux HTTP simulé.
- **Rapport final honnête** : ce backend n'a été testé contre aucun matériel réel dans cet
  environnement — à valider par l'utilisateur dès qu'un scanner/imprimante eSCL est disponible
  sur son réseau, avant de le considérer "compatible" au sens de la règle R12.
