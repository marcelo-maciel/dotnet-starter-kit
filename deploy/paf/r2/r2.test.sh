#!/usr/bin/env bash
# Local proof of the M11 tools against real PostgreSQL (real migrations through the DbMigrator, seed-demo unit) and
# RustFS. conferir-midias.cs: every object present -> 0; one deleted -> 1 naming that key; kit prefix applied -> 1;
# wrong secret -> 2; no connection string -> 2. provar-r2.cs: the steps that do not depend on R2 pass on RustFS, and
# the run exits non-zero because RustFS has no bucket lock (the probe catches a missing lock).
# Exit 0 only when every case behaved as expected. Leaves nothing running: containers and network go on EXIT.
#
# Usage: deploy/paf/r2/r2.test.sh
# Needs: docker, dotnet SDK 10, curl with --aws-sigv4 (7.75+).
set -uo pipefail

AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RAIZ="$(cd "$AQUI/../../.." && pwd)"
SUFIXO="paf-m11-$$"
REDE="$SUFIXO-rede"
PG="$SUFIXO-pg"
VALKEY="$SUFIXO-valkey"
S3="$SUFIXO-s3"
SENHA_PG="m11-pg-$$"
SENHA_VALKEY="m11-valkey-$$"
SENHA_DEMO="Password123!"
CHAVE_S3="rustfsadmin"
BUCKET="paf-m11-teste"
# *.env is gitignored in ambientes/.
AMBIENTE="$AQUI/../ambientes/teste-r2-$$.env"
SAIDA="$(mktemp)"
LIMITE_PRONTO_S=60
LIMITE_MIGRATOR_S=900
LIMITE_FERRAMENTA_S=600

falhas=0
resultados=()
registrar() {
  resultados+=("$1 $2")
  if [ "$1" != "PASS" ]; then falhas=$((falhas + 1)); fi
}

limpar() {
  echo "--- limpeza"
  docker rm -f "$PG" "$VALKEY" "$S3" >/dev/null
  docker network rm "$REDE" >/dev/null
  rm -f "$AMBIENTE" "$SAIDA"
}
trap limpar EXIT

aguardar() {
  local nome="$1"; shift
  local i
  for ((i = 0; i < LIMITE_PRONTO_S; i++)); do
    if docker exec "$nome" "$@" >/dev/null; then return 0; fi
    sleep 1
  done
  return 1
}

# S3 call signed by curl; prints only the HTTP status.
s3() {
  local metodo="$1" caminho="$2"; shift 2
  curl -s -o /dev/null -w '%{http_code}' --max-time 20 --aws-sigv4 "aws:amz:us-east-1:s3" \
    --user "$CHAVE_S3:$CHAVE_S3" -X "$metodo" "$@" "http://127.0.0.1:$PORTA_S3/$caminho"
}

psql_teste() {
  docker exec -i "$PG" psql -v ON_ERROR_STOP=1 -U fsh -d fsh -At "$@"
}

ambiente() {
  local prefixo="$1" segredo="$2"
  cat >"$AMBIENTE" <<EOF
PAF_DASHBOARD_URL=http://127.0.0.1:5173
Storage__Provider=s3
Storage__S3__ServiceUrl=http://127.0.0.1:$PORTA_S3
Storage__S3__Region=us-east-1
Storage__S3__ForcePathStyle=true
Storage__S3__Bucket=$BUCKET
Storage__S3__Prefix=$prefixo
Storage__S3__AccessKey=$CHAVE_S3
Storage__S3__SecretKey=$segredo
EOF
}

conferir() {
  PAF_CONFERENCIA_CONEXAO="$1" timeout "$LIMITE_FERRAMENTA_S" dotnet run "$AQUI/conferir-midias.cs" -- "$AMBIENTE" >"$SAIDA" 2>&1
}

echo "--- 1. PostgreSQL, Valkey e RustFS efêmeros"
if ! docker network create "$REDE" >/dev/null; then
  echo "FAIL infra: docker network create"
  exit 1
fi
docker run -d --name "$PG" --network "$REDE" -p 127.0.0.1::5432 \
  -e POSTGRES_DB=fsh -e POSTGRES_USER=fsh -e POSTGRES_PASSWORD="$SENHA_PG" postgres:18-alpine >/dev/null
docker run -d --name "$VALKEY" --network "$REDE" -p 127.0.0.1::6379 \
  valkey/valkey:9.1.0-alpine valkey-server --requirepass "$SENHA_VALKEY" >/dev/null
docker run -d --name "$S3" --network "$REDE" -p 127.0.0.1::9000 \
  -e RUSTFS_ACCESS_KEY="$CHAVE_S3" -e RUSTFS_SECRET_KEY="$CHAVE_S3" rustfs/rustfs:1.0.0 >/dev/null
PORTA_PG="$(docker port "$PG" 5432/tcp | head -n 1 | sed 's/.*://')"
PORTA_VALKEY="$(docker port "$VALKEY" 6379/tcp | head -n 1 | sed 's/.*://')"
PORTA_S3="$(docker port "$S3" 9000/tcp | head -n 1 | sed 's/.*://')"
s3_pronto=1
for ((i = 0; i < LIMITE_PRONTO_S; i++)); do
  if [ "$(s3 PUT "$BUCKET")" = "200" ]; then s3_pronto=0; break; fi
  sleep 1
done
if aguardar "$PG" pg_isready -U fsh -d fsh && aguardar "$VALKEY" valkey-cli -a "$SENHA_VALKEY" --no-auth-warning PING && [ "$s3_pronto" -eq 0 ]; then
  registrar PASS "infra: postgres, valkey e rustfs prontos, bucket criado"
else
  registrar FAIL "infra: algum serviço não ficou pronto em ${LIMITE_PRONTO_S}s"
  printf '%s\n' "${resultados[@]}"
  exit 1
fi
CONEXAO="Host=127.0.0.1;Port=$PORTA_PG;Database=fsh;Username=fsh;Password=$SENHA_PG"

echo "--- 2. migrações reais e seed-demo pelo DbMigrator"
migrar() {
  DOTNET_ENVIRONMENT=Development \
  DatabaseOptions__ConnectionString="$CONEXAO;Timeout=15;Command Timeout=120" \
  CachingOptions__Redis="127.0.0.1:$PORTA_VALKEY,password=$SENHA_VALKEY,connectTimeout=5000,syncTimeout=5000" \
  Seed__DefaultAdminPassword="$SENHA_DEMO" \
  Seed__DemoPassword="$SENHA_DEMO" \
  timeout "$LIMITE_MIGRATOR_S" dotnet run --project "$RAIZ/src/Host/FSH.Starter.DbMigrator" -c Release -p:NuGetAudit=false -- "$@"
}
if migrar apply --seed >"$SAIDA" 2>&1 && migrar seed-demo >>"$SAIDA" 2>&1; then
  registrar PASS "banco: apply --seed e seed-demo"
else
  tail -n 30 "$SAIDA"
  registrar FAIL "banco: migrator falhou ou passou de ${LIMITE_MIGRATOR_S}s"
  printf '%s\n' "${resultados[@]}"
  exit 1
fi

echo "--- 3. duas mídias da unidade demo, objetos enviados ao RustFS"
unidade="$(psql_teste -c 'SELECT "Id" || $$|$$ || "TenantId" FROM paf."Unidades" ORDER BY "Id" LIMIT 1')"
UNIDADE="${unidade%%|*}"
TENANT="${unidade##*|}"
chaves=()
for n in 1 2; do
  envio="$(docker exec "$PG" sh -c 'cat /proc/sys/kernel/random/uuid')"
  midia="$(docker exec "$PG" sh -c 'cat /proc/sys/kernel/random/uuid')"
  chave="paf-midia/$TENANT/$UNIDADE/${envio//-/}.mp4"
  chaves+=("$chave")
  psql_teste >/dev/null <<SQL
INSERT INTO paf."EnviosMidia" ("Id","UnidadeId","ChaveObjeto","Tipo","ContentType","ConfirmadoEm","TenantId")
VALUES ('$envio','$UNIDADE','$chave','Video','video/mp4',now(),'$TENANT');
INSERT INTO paf."Midias" ("Id","UnidadeId","Descricao","Tipo","EnvioId","ChaveObjeto","DuracaoSegundos","Ativa","TenantId")
VALUES ('$midia','$UNIDADE','Mídia M11 $n','Video','$envio','$chave',30,true,'$TENANT');
SQL
  s3 PUT "$BUCKET/$chave" -H 'Content-Type: video/mp4' --data-binary "conteudo-$n" >/dev/null
done
total="$(psql_teste -c 'SELECT count(*) FROM paf."Midias"')"
if [ -n "$UNIDADE" ] && [ "$total" = "2" ] && [ "$(s3 HEAD "$BUCKET/${chaves[0]}" -I)" = "200" ]; then
  registrar PASS "massa: 2 mídias no banco e objetos no bucket"
else
  registrar FAIL "massa: unidade '$UNIDADE', mídias '$total'"
fi

echo "--- 4. conferir-midias.cs"
ambiente "" "$CHAVE_S3"
conferir "$CONEXAO"; codigo=$?
if [ "$codigo" -eq 0 ] && grep -q "RESUMO referencias=2 presentes=2 ausentes=0 erros=0" "$SAIDA"; then
  registrar PASS "todos presentes: código 0"
else
  cat "$SAIDA"; registrar FAIL "todos presentes: código $codigo"
fi

ambiente "app" "$CHAVE_S3"
conferir "$CONEXAO"; codigo=$?
if [ "$codigo" -eq 1 ] && grep -qF "AUSENTE app/${chaves[0]}" "$SAIDA"; then
  registrar PASS "prefixo do kit aplicado à chave (app/...): código 1"
else
  cat "$SAIDA"; registrar FAIL "prefixo do kit: código $codigo"
fi

ambiente "" "segredo-errado"
conferir "$CONEXAO"; codigo=$?
if [ "$codigo" -eq 2 ] && grep -q "erros=2" "$SAIDA"; then
  registrar PASS "credencial errada: código 2, nada reportado como ausente"
else
  cat "$SAIDA"; registrar FAIL "credencial errada: código $codigo"
fi

ambiente "" "$CHAVE_S3"
conferir ""; codigo=$?
if [ "$codigo" -eq 2 ] && grep -q "PAF_CONFERENCIA_CONEXAO vazia" "$SAIDA"; then
  registrar PASS "sem string de conexão: código 2"
else
  cat "$SAIDA"; registrar FAIL "sem string de conexão: código $codigo"
fi

conferir "Host=127.0.0.1;Port=$PORTA_PG;Database=fsh;Username=fsh;Password=senha-errada"; codigo=$?
if [ "$codigo" -eq 2 ] && grep -q "leitura do banco restaurado falhou" "$SAIDA"; then
  registrar PASS "banco inacessível: código 2"
else
  cat "$SAIDA"; registrar FAIL "banco inacessível: código $codigo"
fi

apagado="$(s3 DELETE "$BUCKET/${chaves[1]}")"
conferir "$CONEXAO"; codigo=$?
if [ "$codigo" -eq 1 ] && grep -qF "AUSENTE ${chaves[1]}" "$SAIDA" && ! grep -qF "AUSENTE ${chaves[0]}" "$SAIDA" \
   && grep -q "presentes=1 ausentes=1" "$SAIDA"; then
  registrar PASS "objeto apagado (DELETE $apagado): código 1 com a chave ausente"
else
  cat "$SAIDA"; registrar FAIL "objeto apagado: código $codigo"
fi

echo "--- 5. provar-r2.cs no RustFS (sem bloqueio nem CORS de bucket: tem de sair diferente de zero)"
ambiente "" "$CHAVE_S3"
timeout "$LIMITE_FERRAMENTA_S" dotnet run "$AQUI/provar-r2.cs" -- "$AMBIENTE" >"$SAIDA" 2>&1; codigo=$?
cat "$SAIDA"
esperados=(
  "PASS PUT assinado em paf-midia/: 200"
  "PASS DELETE fora do prefixo aceito"
  "PASS GET sem assinatura: 403"
  "PASS controle do assinador: assinatura de 1 h aceita: 200"
  "PASS assinatura de 8 dias recusada pelo servidor"
  "FAIL segundo PUT na mesma chave recusado"
  "FAIL DELETE em paf-midia/ recusado"
)
faltou=""
for linha in "${esperados[@]}"; do
  grep -qF "$linha" "$SAIDA" || faltou="$faltou [$linha]"
done
if [ "$codigo" -eq 1 ] && [ -z "$faltou" ]; then
  registrar PASS "prova no RustFS: passos independentes do R2 verdes, bloqueio ausente detectado, código 1"
else
  registrar FAIL "prova no RustFS: código $codigo, linhas faltando:$faltou"
fi

echo "--- resultado"
printf '%s\n' "${resultados[@]}"
if [ "$falhas" -ne 0 ]; then
  echo "R2-TESTE FALHOU ($falhas)"
  exit 1
fi
echo "R2-TESTE OK"
