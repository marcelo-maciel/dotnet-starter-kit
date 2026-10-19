#!/usr/bin/env bash
# Installs the PAF vhost of one environment on the host Apache (Debian layout) and reloads it gracefully (O03).
# Steps, each one's exit checked:
#   1. conferir-apache.sh (version, MPM, modules): any failure aborts before touching a file;
#   2. gerar-vhost.sh renders the vhost from the environment file;
#   3. the previous /etc/apache2/sites-available/<projeto>.conf, if any, is kept aside; the new one goes in and is
#      enabled with a2ensite;
#   4. apachectl configtest: on failure the previous file comes back (or the new site is disabled and removed), the
#      restored state is configtested again, and the script exits non-zero WITHOUT a graceful, so the running
#      Apache, shared with other clients' sites, never sees the broken config;
#   5. apachectl graceful.
# Exit 0 only when the graceful ran on a config that passed configtest. Run as root, one environment at a time,
# with the rollback named first: the file kept aside in step 3 (README).
#
# Usage: deploy/paf/apache/aplicar-vhost.sh <ambiente.env>
set -uo pipefail

if [ "$#" -ne 1 ]; then
  echo "uso: $0 <ambiente.env>" >&2
  exit 2
fi
AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARQUIVO="$1"
SITES=/etc/apache2/sites-available

echo "--- 1. conferir o Apache"
if ! bash "$AQUI/conferir-apache.sh"; then
  echo "APLICAR-VHOST ABORTADO: Apache do host não atende o template (nenhum arquivo foi tocado)"
  exit 1
fi

echo "--- 2. gerar o vhost"
NOVO="$(mktemp)"
trap 'rm -f "$NOVO"' EXIT
if ! bash "$AQUI/gerar-vhost.sh" "$ARQUIVO" >"$NOVO"; then
  echo "APLICAR-VHOST ABORTADO: arquivo de ambiente inválido (nenhum arquivo foi tocado)"
  exit 1
fi
PROJETO="$(grep '^COMPOSE_PROJECT_NAME=' "$ARQUIVO" | tail -n 1 | tr -d '\r' | cut -d= -f2-)"
DESTINO="$SITES/$PROJETO.conf"
ANTERIOR="$SITES/$PROJETO.conf.anterior"

echo "--- 3. instalar $DESTINO"
havia_anterior=0
if [ -f "$DESTINO" ]; then
  havia_anterior=1
  if ! cp -p "$DESTINO" "$ANTERIOR"; then
    echo "APLICAR-VHOST ABORTADO: não guardou $DESTINO em $ANTERIOR"
    exit 1
  fi
  echo "anterior guardado em $ANTERIOR"
fi
if ! install -m 0644 "$NOVO" "$DESTINO" || ! a2ensite -q "$PROJETO"; then
  echo "APLICAR-VHOST FALHOU ao instalar ou habilitar $PROJETO; restaurando"
  configtest_ok=1
else
  echo "--- 4. configtest"
  if apachectl configtest; then configtest_ok=0; else configtest_ok=1; fi
fi

if [ "$configtest_ok" -ne 0 ]; then
  restaurado=0
  if [ "$havia_anterior" -eq 1 ]; then
    cp -p "$ANTERIOR" "$DESTINO" || restaurado=1
  else
    if [ -e "/etc/apache2/sites-enabled/$PROJETO.conf" ]; then a2dissite -q "$PROJETO" || restaurado=1; fi
    rm -f "$DESTINO" || restaurado=1
  fi
  if [ "$restaurado" -eq 0 ] && apachectl configtest; then
    echo "APLICAR-VHOST ABORTADO: configtest falhou com o vhost novo; estado anterior restaurado e conferido, sem graceful"
  else
    echo "APLICAR-VHOST ABORTADO: configtest falhou e o estado restaurado TAMBÉM falha; sem graceful, conferir à mão"
  fi
  exit 1
fi

echo "--- 5. graceful"
apachectl graceful
codigo=$?
if [ "$codigo" -ne 0 ]; then
  echo "APLICAR-VHOST FALHOU no graceful (código $codigo)"
  exit 1
fi
echo "APLICAR-VHOST OK: $DESTINO ativo; rollback = restaurar $ANTERIOR (ou a2dissite $PROJETO), configtest e graceful"
exit 0
