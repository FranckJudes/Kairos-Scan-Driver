#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Kairos Scan Engine - Script d'installation du service systemd sous Linux
# ==============================================================================

INSTALL_DIR="/opt/openscan-agent"
DATA_DIR="/var/lib/openscan"
SERVICE_NAME="openscan-agent"
SERVICE_FILE="scripts/openscan-agent.service"
USER_NAME="openscan"

# Vérification des privilèges root
if [[ $EUID -ne 0 ]]; then
    echo "[ERREUR] Ce script doit être exécuté en tant que root (sudo ./install-linux.sh)." >&2
    exit 1
fi

echo "=== Installation du service ${SERVICE_NAME} ==="

# 1. Création de l'utilisateur système dédié (sans shell interactif)
if ! id -u "${USER_NAME}" &>/dev/null; then
    echo "-> Création de l'utilisateur système ${USER_NAME}..."
    useradd -r -s /bin/false -d "${DATA_DIR}" -c "Kairos Scan Engine Agent Service" "${USER_NAME}"
else
    echo "-> L'utilisateur système ${USER_NAME} existe déjà."
fi

# 2. Ajout aux groupes 'scanner' et 'lp' pour l'accès aux périphériques USB / SANE
for group in scanner lp; do
    if getent group "${group}" >/dev/null; then
        echo "-> Ajout de ${USER_NAME} au groupe ${group}..."
        usermod -aG "${group}" "${USER_NAME}" || true
    fi
done

# 3. Préparation des répertoires d'installation et de données
echo "-> Configuration du répertoire de données ${DATA_DIR}..."
mkdir -p "${DATA_DIR}"
chown -R "${USER_NAME}:${USER_NAME}" "${DATA_DIR}"
chmod 750 "${DATA_DIR}"

echo "-> Configuration du répertoire d'installation ${INSTALL_DIR}..."
mkdir -p "${INSTALL_DIR}"

# 4. Copie des fichiers binaires vers INSTALL_DIR
SOURCE_DIR="${1:-}"
if [[ -z "${SOURCE_DIR}" ]]; then
    # Recherche automatique d'un build existant
    SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    REPO_ROOT="$(dirname "${SCRIPT_DIR}")"
    if [[ -d "${REPO_ROOT}/src/OpenScan.Agent/bin/Release/net8.0/publish" ]]; then
        SOURCE_DIR="${REPO_ROOT}/src/OpenScan.Agent/bin/Release/net8.0/publish"
    elif [[ -d "${REPO_ROOT}/src/OpenScan.Agent/bin/Debug/net8.0" ]]; then
        SOURCE_DIR="${REPO_ROOT}/src/OpenScan.Agent/bin/Debug/net8.0"
    fi
fi

if [[ -n "${SOURCE_DIR}" && -d "${SOURCE_DIR}" && -f "${SOURCE_DIR}/OpenScan.Agent" ]]; then
    echo "-> Déploiement des binaires depuis ${SOURCE_DIR} vers ${INSTALL_DIR}..."
    cp -r "${SOURCE_DIR}"/* "${INSTALL_DIR}/"
    chmod 755 "${INSTALL_DIR}/OpenScan.Agent"
else
    echo "[AVERTISSEMENT] Aucun binaire précompilé trouvé dans SOURCE_DIR."
    echo "Assurez-vous de publier l'application vers ${INSTALL_DIR} via :"
    echo "  dotnet publish src/OpenScan.Agent -c Release -o ${INSTALL_DIR}"
fi

# 5. Installation du fichier unit systemd
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVICE_SOURCE="${SCRIPT_DIR}/openscan-agent.service"

if [[ -f "${SERVICE_SOURCE}" ]]; then
    echo "-> Installation de l'unité systemd dans /etc/systemd/system/${SERVICE_NAME}.service..."
    cp "${SERVICE_SOURCE}" "/etc/systemd/system/${SERVICE_NAME}.service"
    chmod 644 "/etc/systemd/system/${SERVICE_NAME}.service"

    echo "-> Rechargement de systemd..."
    systemctl daemon-reload

    echo "-> Activation et démarrage du service ${SERVICE_NAME}..."
    systemctl enable --now "${SERVICE_NAME}.service"

    echo "-> Statut du service :"
    systemctl status "${SERVICE_NAME}.service" --no-pager || true
else
    echo "[ERREUR] Fichier ${SERVICE_SOURCE} introuvable." >&2
    exit 1
fi

echo "=== Installation terminée avec succès ==="
echo "Consultez les logs du service à tout moment avec : journalctl -u ${SERVICE_NAME} -f"
