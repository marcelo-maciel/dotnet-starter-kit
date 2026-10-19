#!/usr/bin/env bash
# Read-only check of the host Apache before the PAF vhost goes in (O03). Changes nothing: a missing module is
# printed with the a2enmod line to run, because enabling a module on the Apache shared with other clients' sites is
# a host step done one at a time, with Marcelo's confirmation, configtest and graceful (plan of slice 2, risks).
# Checks: version 2.4.47 or newer (upgrade=websocket on the balancer member), MPM event (prefork pins a whole
# process per open WebSocket), and every module the template uses. Prints Timeout, ProxyTimeout and
# MaxRequestWorkers for the record. Exit 0 only when every check passed.
#
# Usage: deploy/paf/apache/conferir-apache.sh
set -uo pipefail

MODULOS=(ssl_module proxy_module proxy_http_module proxy_balancer_module slotmem_shm_module lbmethod_byrequests_module headers_module)

falhas=0
registrar() {
  echo "$1 $2"
  if [ "$1" != "PASS" ]; then falhas=$((falhas + 1)); fi
}

if ! versao_completa="$(apachectl -V 2>&1)"; then
  echo "FAIL apachectl -V não rodou:"
  echo "$versao_completa"
  exit 1
fi
versao="$(printf '%s\n' "$versao_completa" | sed -n 's#^Server version: Apache/\([0-9.]*\).*#\1#p')"
mpm="$(printf '%s\n' "$versao_completa" | sed -n 's#^Server MPM: *\([a-z]*\).*#\1#p')"
IFS=. read -r maior menor revisao <<<"$versao"
if [ -n "$versao" ] && { [ "$maior" -gt 2 ] || { [ "$maior" -eq 2 ] && { [ "$menor" -gt 4 ] || { [ "$menor" -eq 4 ] && [ "$revisao" -ge 47 ]; }; }; }; }; then
  registrar PASS "versão $versao (upgrade=websocket exige 2.4.47 ou mais nova)"
else
  registrar FAIL "versão '$versao' (upgrade=websocket exige 2.4.47 ou mais nova)"
fi
if [ "$mpm" = "event" ]; then registrar PASS "MPM $mpm"
else registrar FAIL "MPM '$mpm': com prefork cada WebSocket prende um processo (ARQ seção 11)"; fi

if ! carregados="$(apachectl -M 2>&1)"; then
  echo "FAIL apachectl -M não rodou:"
  echo "$carregados"
  exit 1
fi
for modulo in "${MODULOS[@]}"; do
  if printf '%s\n' "$carregados" | grep -Eq "^ *$modulo "; then registrar PASS "módulo $modulo"
  else registrar FAIL "módulo $modulo ausente: sudo a2enmod ${modulo%_module} (um por vez, com confirmação, configtest e graceful)"; fi
done

if ! despejo="$(apachectl -t -D DUMP_RUN_CFG 2>&1)"; then
  echo "AVISO apachectl -t -D DUMP_RUN_CFG falhou; configuração atual não lida:"
  echo "$despejo"
fi
for diretiva in Timeout ProxyTimeout MaxRequestWorkers ThreadsPerChild ServerLimit GracefulShutdownTimeout; do
  linhas="$(grep -rhiE "^[[:space:]]*$diretiva[[:space:]]" /etc/apache2/apache2.conf /etc/apache2/mods-enabled /etc/apache2/conf-enabled 2>&1 | tr -s ' \t' ' ' | tr '\n' ';')"
  echo "INFO $diretiva: ${linhas:-não definido (padrão compilado)}"
done

if [ "$falhas" -eq 0 ]; then echo "CONFERIR-APACHE OK"; exit 0; fi
echo "CONFERIR-APACHE FALHOU ($falhas)"
exit 1
