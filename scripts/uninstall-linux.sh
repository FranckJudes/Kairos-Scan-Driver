#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# OpenScan Engine - Script de désinstallation du service systemd sous Linux
# ==============================================================================

INSTALL_DIR="/opt/openscan-agent"
DATA_DIR="/var/lib/openscan"
SERVICE_NAME="openscan-agent"

# Vérification des privilèges root
if [[ $EUID -ne 0 ]]; then
    echo "[ERREUR] Ce script doit être exécuté en tant que root (sudo ./uninstall-linux.sh)." >&2
    exit 1
fi

echo "=== Désinstallation du service ${SERVICE_NAME} ==="

# 1. Arrêt et désactivation du service
if systemctl is-active --quiet "${SERVICE_NAME}.service" 2>/dev/null; then
    echo "-> Arrêt du service ${SERVICE_NAME}..."
    systemctl stop "${SERVICE_NAME}.service" || true
fi

if systemctl is-enabled --quiet "${SERVICE_NAME}.service" 2>/dev/null; then
    echo "-> Désactivation du service ${SERVICE_NAME}..."
    systemctl disable "${SERVICE_NAME}.service" || true
fi

# 2. Suppression de l'unité systemd
if [[ -f "/etc/systemd/system/${SERVICE_NAME}.service" ]]; then
    echo "-> Suppression du fichier /etc/systemd/system/${SERVICE_NAME}.service..."
    rm -f "/etc/systemd/system/${SERVICE_NAME}.service"
    systemctl daemon-reload
fi

# 3. Suppression des fichiers d'installation
if [[ -d "${INSTALL_DIR}" ]]; then
    echo "-> Suppression du répertoire ${INSTALL_DIR}..."
    rm -rf "${INSTALL_DIR}"
fi

# 4. Traitement du répertoire de données
if [[ "${1:-}" == "--purge" ]]; then
    echo "-> Suppression complète des données utilisateur (--purge) dans ${DATA_DIR}..."
    rm -rf "${DATA_DIR}"
else
    echo "-> Les données persistantes (SQLite, scans) dans ${DATA_DIR} sont conservées."
    echo "   Pour les supprimer manuellement : rm -rf ${DATA_DIR} ou ré-exécuter avec --purge."
fi

echo "=== Désinstallation terminée avec succès ==="
