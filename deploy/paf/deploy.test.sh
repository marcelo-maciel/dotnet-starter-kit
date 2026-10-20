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
# docker call is appended to STUB_LOG; curl answers 200 only for the ports listed in STUB_SAUDAVEIS.
STUBS="$TEMP/stubs"
mkdir -p "$STUBS"
cat >"$STUBS/docker" <<'STUB'
#!/usr/bin/env bash
ultimo="${!#}"
echo "$*" >>"$STUB_LOG"
case " $* " in
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

# The other instance does not answer health: abort with 4 before stopping or recreating anything (Caddy would be
# left with no instance).
: >"$STUB_LOG"
caso 4 "outra instância sem saúde" "${STUBADO[@]}" STUB_UP=0 STUB_SAUDAVEIS="18081" \
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

if [ "$falhas" -eq 0 ]; then echo "DEPLOY-TEST OK"; exit 0; fi
echo "DEPLOY-TEST FALHOU ($falhas)"
exit 1
