#!/usr/bin/env bash
# Paths of deploy.sh (O06) that need no Docker: usage errors (2), missing environment file (4), the lock held by
# another deploy (3), the rollback tag of the pre-flight, the other instance without health (4, nothing stopped) and
# the order of the instances, all on stubbed docker/curl. deploy.sh runs from a copy in a temporary directory, so
# nothing under deploy/paf/ambientes is written. The full path (Caddy, health, rollback) is ensaio-deploy.sh.
# Usage: deploy/paf/deploy.test.sh   -> exit 0 only when every case got the expected exit code.
set -uo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMP="$(mktemp -d)"
trap 'rm -rf "$TEMP"' EXIT
mkdir -p "$TEMP/paf/ambientes"
cp "$DIR/deploy.sh" "$TEMP/paf/deploy.sh"
cp "$DIR/permissoes-root.txt" "$TEMP/paf/permissoes-root.txt"
DEPLOY="$TEMP/paf/deploy.sh"

falhas=0
caso() {
  local esperado="$1" nome="$2"; shift 2
  "$@" >"$TEMP/saida" 2>&1
  local obtido=$?
  if [ "$obtido" = "$esperado" ]; then echo "PASS $nome"; else echo "FAIL $nome (esperado $esperado, obtido $obtido)"; sed 's/^/    /' "$TEMP/saida"; falhas=$((falhas + 1)); fi
}

caso 2 "sem argumentos" env PAF_REGISTRO=ghcr.io/dono bash "$DEPLOY"
caso 2 "um argumento" env PAF_REGISTRO=ghcr.io/dono bash "$DEPLOY" producao
caso 2 "ambiente inválido" env PAF_REGISTRO=ghcr.io/dono bash "$DEPLOY" staging v1
caso 2 "tag com espaço" env PAF_REGISTRO=ghcr.io/dono bash "$DEPLOY" producao 'v1 v2'
caso 2 "tag com aspas" env PAF_REGISTRO=ghcr.io/dono bash "$DEPLOY" producao "v1';rm"
caso 2 "PAF_REGISTRO ausente" env -u PAF_REGISTRO bash "$DEPLOY" producao v1
caso 2 "PAF_REGISTRO com maiúscula" env PAF_REGISTRO=ghcr.io/Dono bash "$DEPLOY" producao v1
caso 4 "arquivo de ambiente inexistente" env PAF_REGISTRO=ghcr.io/dono bash "$DEPLOY" producao v1

if command -v flock >/dev/null; then
  : >"$TEMP/paf/ambientes/producao.env"
  flock "$TEMP/paf/ambientes/producao.deploy.lock" sleep 5 &
  segurando=$!
  sleep 1
  caso 3 "trava já tomada por outro deploy" env PAF_REGISTRO=ghcr.io/dono bash "$DEPLOY" producao v1
  wait "$segurando"
else
  echo "SKIPPED trava já tomada por outro deploy: flock ausente nesta máquina (coberto por ensaio-deploy.sh)"
fi

# Rollback tag recorded in pre-flight, with docker, curl, flock and verificar-ambiente.sh stubbed: the stub docker
# reports the running API images from STUB_IMAGEM_A / STUB_IMAGEM_B and fails `compose up` unless STUB_UP=0, so
# those runs stop at step 3 (exit 6) with nothing deployed, right after the tag-anterior decision under test. Every
# docker call is appended to STUB_LOG; curl answers 200 only for the ports listed in STUB_SAUDAVEIS. The psql of step 7
# (`compose exec`) fails when STUB_PSQL_FALHA is set; otherwise it answers the first line of the file STUB_CONTAGENS
# (consumed while more than one is left, so "1 then 0" is a role sync still running), or 0 without the file.
STUBS="$TEMP/stubs"
mkdir -p "$STUBS"
cat >"$STUBS/docker" <<'STUB'
#!/usr/bin/env bash
ultimo="${!#}"
echo "$*" >>"$STUB_LOG"
case " $* " in
  *" exec -T postgres psql "*)
    if [ -n "${STUB_PSQL_FALHA:-}" ]; then echo "psql: error: connection refused" >&2; exit 2; fi
    if [ -z "${STUB_CONTAGENS:-}" ]; then echo 0; exit 0; fi
    head -n 1 "$STUB_CONTAGENS"
    if [ "$(wc -l <"$STUB_CONTAGENS")" -gt 1 ]; then
      tail -n +2 "$STUB_CONTAGENS" >"$STUB_CONTAGENS.resto" && mv "$STUB_CONTAGENS.resto" "$STUB_CONTAGENS"
    fi
    exit 0 ;;
  *" image inspect "*) exit 0 ;;
  *" ps -q "*)
    case "$ultimo" in
      api-a) if [ -n "${STUB_IMAGEM_A:-}" ]; then echo id-api-a; fi ;;
      api-b) if [ -n "${STUB_IMAGEM_B:-}" ]; then echo id-api-b; fi ;;
    esac
    exit 0 ;;
  *" inspect "*)
    case "$ultimo" in id-api-a) echo "$STUB_IMAGEM_A" ;; id-api-b) echo "$STUB_IMAGEM_B" ;; *) exit 1 ;; esac
    exit 0 ;;
  *" up -d --no-deps api-"*) exit "${STUB_UP_INSTANCIA:-${STUB_UP:-1}}" ;;
  *" up "*) exit "${STUB_UP:-1}" ;;
  *" run "* | *" stop "* | *" logs "*) exit 0 ;;
esac
echo "docker stub: chamada inesperada: $*" >&2
exit 1
STUB
cat >"$STUBS/curl" <<'STUB'
#!/usr/bin/env bash
porta="${!#}"
porta="${porta#http://127.0.0.1:}"
porta="${porta%%/*}"
case " ${STUB_SAUDAVEIS:-} " in *" $porta "*) printf 200 ;; *) printf 000 ;; esac
STUB
printf '#!/usr/bin/env bash\nexit 0\n' >"$STUBS/flock"
chmod +x "$STUBS/docker" "$STUBS/curl" "$STUBS/flock"
printf '#!/usr/bin/env bash\nexit 0\n' >"$TEMP/paf/verificar-ambiente.sh"
printf 'COMPOSE_PROJECT_NAME=paf-prod\nPAF_PORTA_API_A=18081\nPAF_PORTA_API_B=18082\nPAF_PORTA_DASHBOARD=18083\nPAF_PORTA_ADMIN=18084\n' \
  >"$TEMP/paf/ambientes/producao.env"
: >"$TEMP/paf/ambientes/homologacao.env"
REGISTRADA="$TEMP/paf/ambientes/producao.tag-anterior"

STUB_LOG="$TEMP/docker.log"
STUBADO=(env PATH="$STUBS:$PATH" PAF_REGISTRO=ghcr.io/dono STUB_LOG="$STUB_LOG")

tag_registrada() {
  local esperada="$1" nome="$2" obtida=""
  if [ -f "$REGISTRADA" ]; then obtida="$(cat "$REGISTRADA")"; fi
  if [ "$obtida" = "$esperada" ]; then echo "PASS $nome"; else echo "FAIL $nome (esperada '$esperada', obtida '$obtida')"; falhas=$((falhas + 1)); fi
}

rm -f "$REGISTRADA"
caso 6 "instâncias em t1, deploy de t2" "${STUBADO[@]}" STUB_IMAGEM_A=ghcr.io/dono/paf-api:t1 STUB_IMAGEM_B=ghcr.io/dono/paf-api:t1 bash "$DEPLOY" producao t2
tag_registrada t1 "instâncias em t1, deploy de t2: tag-anterior t1"
# Partial deploy (exit 7): api-a on t2, api-b back on t1. Retrying t2 must keep t1 as the rollback.
caso 6 "deploy parcial, nova tentativa de t2" "${STUBADO[@]}" STUB_IMAGEM_A=ghcr.io/dono/paf-api:t2 STUB_IMAGEM_B=ghcr.io/dono/paf-api:t1 bash "$DEPLOY" producao t2
tag_registrada t1 "deploy parcial, nova tentativa de t2: tag-anterior continua t1"
# Rolling the partial deploy back to t1: the way forward again is t2.
caso 6 "deploy parcial, rollback para t1" "${STUBADO[@]}" STUB_IMAGEM_A=ghcr.io/dono/paf-api:t2 STUB_IMAGEM_B=ghcr.io/dono/paf-api:t1 bash "$DEPLOY" producao t1
tag_registrada t2 "deploy parcial, rollback para t1: tag-anterior t2"
# Mixed tags and a third one requested: no single rollback, refused before any mutation.
printf 't0\n' >"$REGISTRADA"
caso 4 "deploy parcial, terceira tag t3" "${STUBADO[@]}" STUB_IMAGEM_A=ghcr.io/dono/paf-api:t2 STUB_IMAGEM_B=ghcr.io/dono/paf-api:t1 bash "$DEPLOY" producao t3
tag_registrada t0 "deploy parcial, terceira tag t3: tag-anterior intocada"
# Redeploy of the tag already running: recording it would lose the real rollback.
caso 6 "redeploy da tag em execução" "${STUBADO[@]}" STUB_IMAGEM_A=ghcr.io/dono/paf-api:t2 STUB_IMAGEM_B=ghcr.io/dono/paf-api:t2 bash "$DEPLOY" producao t2
tag_registrada t0 "redeploy da tag em execução: tag-anterior intocada"
rm -f "$REGISTRADA"
caso 6 "primeira implantação" "${STUBADO[@]}" STUB_IMAGEM_A= STUB_IMAGEM_B= bash "$DEPLOY" producao t1
tag_registrada "" "primeira implantação: sem tag-anterior"

# The other instance does not answer health: abort with 11 (the migrator already ran) before stopping or recreating
# anything (Caddy would be left with no instance).
: >"$STUB_LOG"
caso 11 "outra instância sem saúde" "${STUBADO[@]}" STUB_UP=0 STUB_SAUDAVEIS="18081" \
  STUB_IMAGEM_A=ghcr.io/dono/paf-api:t1 STUB_IMAGEM_B=ghcr.io/dono/paf-api:t1 bash "$DEPLOY" producao t2
if grep -q -e " stop " -e "--no-deps api-" "$STUB_LOG"; then
  echo "FAIL outra instância sem saúde: uma instância foi parada ou recriada"; sed 's/^/    /' "$STUB_LOG"; falhas=$((falhas + 1))
else
  echo "PASS outra instância sem saúde: nenhuma instância parada nem recriada"
fi

# api-b without a container (a host that ran one instance): it starts first, and api-a stops only after it.
: >"$STUB_LOG"
caso 0 "instância nova primeiro" "${STUBADO[@]}" STUB_UP=0 STUB_SAUDAVEIS="18081 18082 18083 18084" \
  STUB_IMAGEM_A=ghcr.io/dono/paf-api:t1 STUB_IMAGEM_B= bash "$DEPLOY" producao t2
linha_b="$(grep -n -- "--no-deps api-b" "$STUB_LOG" | head -n 1 | cut -d: -f1)"
linha_parada="$(grep -n "stop api-a" "$STUB_LOG" | head -n 1 | cut -d: -f1)"
if [ -n "$linha_b" ] && [ -n "$linha_parada" ] && [ "$linha_b" -lt "$linha_parada" ]; then
  echo "PASS instância nova primeiro: api-b sobe (linha $linha_b) antes de api-a parar (linha $linha_parada)"
else
  echo "FAIL instância nova primeiro: api-b na linha '$linha_b', parada de api-a na linha '$linha_parada'"; sed 's/^/    /' "$STUB_LOG"
  falhas=$((falhas + 1))
fi

# Stopped, then neither the new image nor the previous one starts: exit 8, and the EXIT trap names the instance left
# stopped (A7).
caso 8 "instância parada sem subir" "${STUBADO[@]}" STUB_UP=0 STUB_UP_INSTANCIA=1 STUB_SAUDAVEIS="18081 18082" \
  STUB_IMAGEM_A=ghcr.io/dono/paf-api:t1 STUB_IMAGEM_B=ghcr.io/dono/paf-api:t1 bash "$DEPLOY" producao t2
if grep -q "^RESIDUO api-a ficou parada" "$TEMP/saida"; then echo "PASS instância parada sem subir: RESIDUO api-a impresso"
else echo "FAIL instância parada sem subir: sem RESIDUO"; sed 's/^/    /' "$TEMP/saida"; falhas=$((falhas + 1)); fi
caso 0 "instância nova primeiro, sem RESIDUO" "${STUBADO[@]}" STUB_UP=0 STUB_SAUDAVEIS="18081 18082 18083 18084" \
  STUB_IMAGEM_A=ghcr.io/dono/paf-api:t1 STUB_IMAGEM_B=ghcr.io/dono/paf-api:t1 bash "$DEPLOY" producao t2
if grep -q "RESIDUO" "$TEMP/saida"; then echo "FAIL deploy completo imprimiu RESIDUO"; falhas=$((falhas + 1))
else echo "PASS deploy completo sem RESIDUO"; fi

# Produção without homologacao.env: refused with 4 before verificar-ambiente.sh (the cross checks are mandatory).
mv "$TEMP/paf/ambientes/homologacao.env" "$TEMP/paf/ambientes/homologacao.env.fora"
printf '#!/usr/bin/env bash\necho chamado >"%s"\nexit 0\n' "$TEMP/verificador.chamado" >"$TEMP/paf/verificar-ambiente.sh"
caso 4 "produção sem homologacao.env" "${STUBADO[@]}" STUB_IMAGEM_A= STUB_IMAGEM_B= bash "$DEPLOY" producao t1
if [ ! -e "$TEMP/verificador.chamado" ] && grep -q "homologacao.env inexistente" "$TEMP/saida"; then
  echo "PASS produção sem homologacao.env: recusada antes do verificador"
else
  echo "FAIL produção sem homologacao.env"; falhas=$((falhas + 1))
fi
mv "$TEMP/paf/ambientes/homologacao.env.fora" "$TEMP/paf/ambientes/homologacao.env"

# A host with homologação only (no producao.env): the pre-flight does not refuse the missing file and hands both
# paths to verificar-ambiente.sh, which skips the cross checks (verificar-ambiente.test.sh). The stub records them.
mv "$TEMP/paf/ambientes/producao.env" "$TEMP/paf/ambientes/homologacao.env"
printf '#!/usr/bin/env bash\necho "$@" >"%s"\nexit 0\n' "$TEMP/verificador.args" >"$TEMP/paf/verificar-ambiente.sh"
caso 6 "só homologação no host" "${STUBADO[@]}" STUB_IMAGEM_A= STUB_IMAGEM_B= bash "$DEPLOY" homologacao t1
if [ "$(cat "$TEMP/verificador.args")" = "$TEMP/paf/ambientes/producao.env $TEMP/paf/ambientes/homologacao.env" ] \
   && [ ! -e "$TEMP/paf/ambientes/producao.env" ]; then
  echo "PASS só homologação no host: verificar-ambiente.sh recebe o caminho de producao.env ausente e o deploy segue"
else
  echo "FAIL só homologação no host: argumentos '$(cat "$TEMP/verificador.args")'"; falhas=$((falhas + 1))
fi

mv "$TEMP/paf/ambientes/homologacao.env" "$TEMP/paf/ambientes/producao.env"
: >"$TEMP/paf/ambientes/homologacao.env"

# Step 7, the read-only check that no non-root tenant role holds a root-only permission. A full deploy on the stubs;
# `sleep` is stubbed too, so the retry window of the role sync (LIMITE_SINCRONIA_S) passes at once.
mkdir -p "$STUBS/rapido"
printf '#!/usr/bin/env bash\nexit 0\n' >"$STUBS/rapido/sleep"
chmod +x "$STUBS/rapido/sleep"
COMPLETO=(env PATH="$STUBS/rapido:$STUBS:$PATH" PAF_REGISTRO=ghcr.io/dono STUB_LOG="$STUB_LOG" STUB_UP=0
  STUB_SAUDAVEIS="18081 18082 18083 18084" STUB_IMAGEM_A=ghcr.io/dono/paf-api:t1 STUB_IMAGEM_B=ghcr.io/dono/paf-api:t1)
CONTAGENS="$TEMP/contagens"

conferir_saida() {
  local nome="$1" esperado="$2" proibido="$3"
  if grep -qF -- "$esperado" "$TEMP/saida" && ! grep -qF -- "$proibido" "$TEMP/saida"; then echo "PASS $nome"
  else echo "FAIL $nome (esperado '$esperado', sem '$proibido')"; sed 's/^/    /' "$TEMP/saida"; falhas=$((falhas + 1)); fi
}

: >"$STUB_LOG"
caso 0 "conferência root-only: zero linhas" "${COMPLETO[@]}" bash "$DEPLOY" producao t2
conferir_saida "conferência root-only: zero linhas, mensagem" "permissões root-only em papéis de tenants não-root: 0" "FAIL"
# The SQL reads the list file and only non-root tenants' roles.
if grep -q "exec -T postgres psql .*\"TenantId\" <> 'root'.*'Permissions.Billing.View'.*'Permissions.Users.Impersonate'" "$STUB_LOG"; then
  echo "PASS conferência root-only: o SQL usa permissoes-root.txt e só tenants não-root"
else
  echo "FAIL conferência root-only: SQL inesperado"; grep "psql" "$STUB_LOG" | sed 's/^/    /'; falhas=$((falhas + 1))
fi

printf '1\n0\n' >"$CONTAGENS"
caso 0 "conferência root-only: sincronia ainda rodando, depois zero" "${COMPLETO[@]}" STUB_CONTAGENS="$CONTAGENS" bash "$DEPLOY" producao t2

printf '1\n' >"$CONTAGENS"
caso 12 "conferência root-only: uma linha" "${COMPLETO[@]}" STUB_CONTAGENS="$CONTAGENS" bash "$DEPLOY" producao t2
conferir_saida "conferência root-only: uma linha, mensagem" "FAIL 1 permissão(ões) root-only em papéis de tenants não-root depois de 150s" "DEPLOY OK"

caso 13 "conferência root-only: psql falha" "${COMPLETO[@]}" STUB_PSQL_FALHA=1 bash "$DEPLOY" producao t2
conferir_saida "conferência root-only: psql falha, mensagem" "não leu o banco (psql falhou)" "DEPLOY OK"

printf '\n' >"$CONTAGENS"
caso 13 "conferência root-only: psql sem contagem" "${COMPLETO[@]}" STUB_CONTAGENS="$CONTAGENS" bash "$DEPLOY" producao t2
conferir_saida "conferência root-only: psql sem contagem, mensagem" "no lugar de uma contagem" "DEPLOY OK"

# A bad list fails in the pre-flight (4) with nothing touched: missing, empty, or a line that could close the SQL literal.
cp "$TEMP/paf/permissoes-root.txt" "$TEMP/permissoes-root.txt.original"
lista_reprovada() {
  local nome="$1"
  : >"$STUB_LOG"
  caso 4 "$nome" "${COMPLETO[@]}" bash "$DEPLOY" producao t2
  if grep -q -e " up " -e " stop " -e " run " "$STUB_LOG"; then
    echo "FAIL $nome: algo foi tocado"; sed 's/^/    /' "$STUB_LOG"; falhas=$((falhas + 1))
  else
    echo "PASS $nome: nada tocado"
  fi
}
rm "$TEMP/paf/permissoes-root.txt"
lista_reprovada "lista root-only ausente"
printf '# only a comment\n\n' >"$TEMP/paf/permissoes-root.txt"
lista_reprovada "lista root-only vazia"
printf "Permissions.Billing.View\nPermissions.X') or ('1' = '1\n" >"$TEMP/paf/permissoes-root.txt"
lista_reprovada "lista root-only com aspas"
cp "$TEMP/permissoes-root.txt.original" "$TEMP/paf/permissoes-root.txt"

if [ "$falhas" -eq 0 ]; then echo "DEPLOY-TEST OK"; exit 0; fi
echo "DEPLOY-TEST FALHOU ($falhas)"
exit 1
