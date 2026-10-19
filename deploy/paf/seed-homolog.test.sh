#!/usr/bin/env bash
# Cases of seed-homolog.sh (O09, D19), on environment files generated from the .exemplo files.
#
# Part 1, no container: every guard refuses with exit 2 BEFORE the first docker call. A `docker` stub first on PATH
# drops a marker file when called, so "no connection opened" is checked by the marker, not by reading the script.
# The positive control (paf-homologacao with a valid password) must pass every guard and reach the stub.
#
# Part 2, real containers on the disposable project paf-ensaio-o09 (PostgreSQL and Valkey only):
#   - two runs of the wrapper exit 0 and leave the row count of every table identical;
#   - the demo admin's hash matches PAF_SENHA_DEMO and not the kit default (the password reached the container);
#   - the kit guard still refuses `seed-demo` under the file's own DOTNET_ENVIRONMENT=Production.
# Removes the project (containers, network, volumes) and the generated files on exit.
#
# Usage: deploy/paf/seed-homolog.test.sh <imagem-do-migrator>     e.g. paf-dbmigrator:<tag>
# Needs: docker compose v2, node >= 18, the migrator image already present (no build here).
set -uo pipefail

if [ "$#" -ne 1 ]; then
  echo "uso: $0 <imagem-do-migrator>" >&2
  exit 2
fi
IMAGEM_MIGRATOR="$1"
AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
AMBIENTES="$AQUI/ambientes"
# Inside ambientes/ because the compose resolves PAF_ARQUIVO_AMBIENTE from deploy/paf; *.env is gitignored there.
PROD="$AMBIENTES/teste-o09-producao.env"
HOMOLOG="$AMBIENTES/teste-o09-homologacao.env"
ENSAIO="$AMBIENTES/teste-o09-ensaio.env"
STUB="$(mktemp -d)"
MARCA="$STUB/docker-chamado"
SAIDA="$(mktemp)"
SENHA_TESTE="senha-demo-homolog-o09"
SENHA_PADRAO="Password123!"
LIMITE_PRONTO_S=180

falhas=0
resultados=()
registrar() {
  resultados+=("$1 $2")
  if [ "$1" != "PASS" ]; then falhas=$((falhas + 1)); fi
}

compose_ensaio() {
  docker compose -f "$AQUI/compose.yml" --env-file "$ENSAIO" "$@"
}

subiu_ensaio=0
limpar() {
  if [ "$subiu_ensaio" -eq 1 ]; then
    echo "--- limpeza"
    compose_ensaio down -v --remove-orphans
  fi
  rm -rf "$STUB" "$SAIDA" "$PROD" "$HOMOLOG" "$ENSAIO"
}
trap limpar EXIT

preencher() {
  local exemplo="$1" destino="$2" sufixo="$3"
  sed -e "s#^PAF_ARQUIVO_AMBIENTE=.*#PAF_ARQUIVO_AMBIENTE=ambientes/$(basename "$destino")#" \
      -e "s#^POSTGRES_PASSWORD=.*#POSTGRES_PASSWORD=pg-$sufixo-1#" \
      -e "s#^VALKEY_PASSWORD=.*#VALKEY_PASSWORD=vk-$sufixo-1#" \
      -e "s#^JwtOptions__SigningKey=.*#JwtOptions__SigningKey=paf-$sufixo-signing-key-ascii-0123456789abcdef#" \
      -e "s#^PAF_IMAGEM_MIGRATOR=.*#PAF_IMAGEM_MIGRATOR=$IMAGEM_MIGRATOR#" \
      -e "s#<account-id>#conta-teste#" -e "s#<segredo>#segredo-teste#g" -e "s#<usuario>#usuario-teste#g" \
      -e "s#<smtp>#smtp.teste#" -e "s#<tag>#teste#" \
      "$exemplo" >"$destino"
}

# Replaces the line of KEY in FILE; fails the case when KEY is missing.
trocar() {
  local arquivo="$1" chave="$2" linha="$3"
  if ! grep -q "^$chave=" "$arquivo"; then
    echo "ERRO do teste: $chave não existe em $arquivo" >&2
    return 1
  fi
  awk -v chave="$chave" -v linha="$linha" 'index($0, chave "=") == 1 { print linha; next } { print }' "$arquivo" >"$arquivo.tmp"
  mv "$arquivo.tmp" "$arquivo"
}

restaurar() {
  preencher "$AMBIENTES/producao.env.exemplo" "$PROD" producao
  preencher "$AMBIENTES/homologacao.env.exemplo" "$HOMOLOG" homologacao
}

cat >"$STUB/docker" <<'FIM'
#!/usr/bin/env bash
touch "$(dirname "$0")/docker-chamado"
exit 1
FIM
chmod +x "$STUB/docker"

# Runs the wrapper with the stub first on PATH. Args: case, expected exit, expect docker reached (0/1), env file,
# then the password ("-" = unset).
com_stub() {
  local caso="$1" codigo_esperado="$2" docker_esperado="$3" arquivo="$4" senha="$5"
  rm -f "$MARCA"
  if [ "$senha" = "-" ]; then
    env -u PAF_SENHA_DEMO PATH="$STUB:$PATH" bash "$AQUI/seed-homolog.sh" "$arquivo" >"$SAIDA" 2>&1
  else
    PAF_SENHA_DEMO="$senha" PATH="$STUB:$PATH" bash "$AQUI/seed-homolog.sh" "$arquivo" >"$SAIDA" 2>&1
  fi
  local codigo=$? docker_chamado=0
  [ -f "$MARCA" ] && docker_chamado=1
  if [ "$codigo" -eq "$codigo_esperado" ] && [ "$docker_chamado" -eq "$docker_esperado" ]; then
    registrar PASS "$caso (código $codigo, docker chamado=$docker_chamado)"
  else
    registrar FAIL "$caso: código $codigo (esperado $codigo_esperado), docker chamado=$docker_chamado (esperado $docker_esperado)"
    head -n 5 "$SAIDA"
  fi
}

echo "--- parte 1: guardas, sem contêiner"
restaurar
com_stub "controle: homologação com senha própria passa nas guardas e chega ao docker" 1 1 "$HOMOLOG" "$SENHA_TESTE"
com_stub "arquivo de produção recusado" 2 0 "$PROD" "$SENHA_TESTE"
com_stub "arquivo inexistente recusado" 2 0 "$AMBIENTES/nao-existe.env" "$SENHA_TESTE"
trocar "$HOMOLOG" COMPOSE_PROJECT_NAME "COMPOSE_PROJECT_NAME=paf-homolog"
com_stub "projeto desconhecido recusado" 2 0 "$HOMOLOG" "$SENHA_TESTE"
restaurar
trocar "$HOMOLOG" DatabaseOptions__ConnectionString \
  'DatabaseOptions__ConnectionString=Host=host.docker.internal;Port=15432;Database=fsh;Username=fsh;Password=${POSTGRES_PASSWORD};Timeout=15;Command Timeout=30'
com_stub "conexão com o PostgreSQL publicado da produção recusada" 2 0 "$HOMOLOG" "$SENHA_TESTE"
restaurar
trocar "$HOMOLOG" DatabaseOptions__ConnectionString \
  'DatabaseOptions__ConnectionString=Host=postgres;Server=10.0.0.5;Database=fsh;Username=fsh;Password=${POSTGRES_PASSWORD}'
com_stub "conexão com Server além de Host recusada" 2 0 "$HOMOLOG" "$SENHA_TESTE"
restaurar
com_stub "sem PAF_SENHA_DEMO recusado" 2 0 "$HOMOLOG" "-"
com_stub "PAF_SENHA_DEMO vazia recusada" 2 0 "$HOMOLOG" ""
com_stub "senha padrão do kit recusada" 2 0 "$HOMOLOG" "$SENHA_PADRAO"
com_stub "senha curta recusada" 2 0 "$HOMOLOG" "curta-11ch"
rm -f "$MARCA"
PATH="$STUB:$PATH" bash "$AQUI/seed-homolog.sh" >"$SAIDA" 2>&1
codigo=$?
if [ "$codigo" -eq 2 ] && [ ! -f "$MARCA" ]; then registrar PASS "sem argumento recusado (código $codigo)"
else registrar FAIL "sem argumento: código $codigo"; fi

echo "--- parte 2: duas execuções reais em paf-ensaio-o09"
preencher "$AMBIENTES/homologacao.env.exemplo" "$ENSAIO" ensaio
trocar "$ENSAIO" COMPOSE_PROJECT_NAME "COMPOSE_PROJECT_NAME=paf-ensaio-o09"
trocar "$ENSAIO" PAF_SUBNET "PAF_SUBNET=172.31.29.0/24"
trocar "$ENSAIO" PAF_PORTA_POSTGRES "PAF_PORTA_POSTGRES=29432"
trocar "$ENSAIO" PAF_PORTA_API_A "PAF_PORTA_API_A=29080"
trocar "$ENSAIO" PAF_PORTA_API_B "PAF_PORTA_API_B=29081"
trocar "$ENSAIO" PAF_PORTA_DASHBOARD "PAF_PORTA_DASHBOARD=29082"
trocar "$ENSAIO" PAF_PORTA_ADMIN "PAF_PORTA_ADMIN=29083"

subiu_ensaio=1
if ! compose_ensaio up -d --wait --wait-timeout "$LIMITE_PRONTO_S" postgres valkey; then
  registrar FAIL "subir postgres e valkey do paf-ensaio-o09"
  printf '%s\n' "${resultados[@]}"; exit 1
fi

psql_ensaio() {
  compose_ensaio exec -T postgres psql -U fsh -d fsh -v ON_ERROR_STOP=1 -Atc "$1" | tr -d '\r'
}

# Exact row count of every user table, one "schema.table count" line each, sorted.
CONTAGENS="select table_schema || '.' || table_name || ' ' || (xpath('/row/c/text()', query_to_xml(format('select count(*) as c from %I.%I', table_schema, table_name), false, true, '')))[1]::text from information_schema.tables where table_type = 'BASE TABLE' and table_schema not in ('pg_catalog', 'information_schema') order by 1"

for rodada in 1 2; do
  PAF_SENHA_DEMO="$SENHA_TESTE" bash "$AQUI/seed-homolog.sh" "$ENSAIO" >"$SAIDA" 2>&1
  codigo=$?
  if [ "$codigo" -eq 0 ] && grep -q "SEED-HOMOLOG OK" "$SAIDA"; then registrar PASS "execução $rodada do seed (código 0)"
  else registrar FAIL "execução $rodada do seed: código $codigo"; tail -n 20 "$SAIDA"; printf '%s\n' "${resultados[@]}"; exit 1; fi
  psql_ensaio "$CONTAGENS" >"$STUB/contagens-$rodada.txt"
done

tabelas="$(wc -l <"$STUB/contagens-1.txt" | tr -d ' ')"
unidades="$(grep '^paf\.Unidades ' "$STUB/contagens-1.txt" | cut -d' ' -f2)"
if [ "$tabelas" -gt 0 ] && [ "${unidades:-0}" -ge 2 ] && diff "$STUB/contagens-1.txt" "$STUB/contagens-2.txt" >/dev/null; then
  registrar PASS "contagens iguais nas duas execuções ($tabelas tabelas, paf.Unidades=$unidades)"
else
  registrar FAIL "contagens: $tabelas tabelas, paf.Unidades='$unidades'"
  diff "$STUB/contagens-1.txt" "$STUB/contagens-2.txt"
fi

hash_admin="$(psql_ensaio "select \"PasswordHash\" from identity.\"Users\" where \"NormalizedEmail\" = 'ADMIN@ACME.COM'")"
conferencia="$(printf '%s' "$hash_admin" | SENHA_ESPERADA="$SENHA_TESTE" SENHA_RECUSADA="$SENHA_PADRAO" node "$AQUI/seed-homolog-senha.mjs")"
if [ "$conferencia" = "esperada=true recusada=false" ]; then registrar PASS "admin demo com a senha de PAF_SENHA_DEMO, não a padrão"
else registrar FAIL "senha do admin demo: '$conferencia'"; fi

# The kit guard: the file says DOTNET_ENVIRONMENT=Production, so the verb without the wrapper's override must refuse.
compose_ensaio run --rm -T migrator seed-demo >"$SAIDA" 2>&1
codigo=$?
if [ "$codigo" -ne 0 ] && grep -q "REFUSING to run" "$SAIDA"; then registrar PASS "guarda do kit recusa seed-demo em Production (código $codigo)"
else registrar FAIL "guarda do kit em Production: código $codigo"; tail -n 5 "$SAIDA"; fi

echo "=== resultado"
printf '%s\n' "${resultados[@]}"
if [ "$falhas" -eq 0 ]; then echo "SEED-HOMOLOG-TESTE OK"; exit 0; fi
echo "SEED-HOMOLOG-TESTE FALHOU ($falhas)"
exit 1
