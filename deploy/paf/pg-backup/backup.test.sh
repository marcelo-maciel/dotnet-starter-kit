#!/usr/bin/env bash
# Tests of the PAF backup (O11) against real containers: PostgreSQL migrated and seeded by the PAF migrator image,
# MinIO standing in for the two R2 buckets (backup and application), and a webhook catcher for the alerts.
# Each case records PASS/FAIL; exit 0 only when every case passed. Removes every container, volume and network.
#
# What MinIO cannot stand in for: the R2 bucket lock. Overwrite and delete refused within 7 days is proved only by
# `paf-backup verificar-bloqueio` against the real bucket (README, human step); here the same command must FAIL on a
# bucket without lock, which proves the check is not vacuous.
#
# Usage: deploy/paf/pg-backup/backup.test.sh <imagem-backup> <imagem-migrator>
#   docker build -t paf-backup:teste deploy/paf/pg-backup && deploy/paf/publicar-imagem-migrator.sh teste
set -uo pipefail
# Git Bash: container paths (/var/lib/...) must reach docker untouched; host paths go through host_path.
export MSYS_NO_PATHCONV=1

if [ "$#" -ne 2 ]; then
  echo "uso: $0 <imagem-backup> <imagem-migrator>" >&2
  exit 2
fi
IMAGEM_BACKUP="$1"
IMAGEM_MIGRATOR="$2"
AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PAF="$(cd "$AQUI/.." && pwd)"
host_path() { if command -v cygpath >/dev/null; then cygpath -m "$1"; else printf '%s' "$1"; fi; }
SUFIXO="$(date +%s)"
REDE="paf-o11-teste-$SUFIXO"
PG="$REDE-pg"
S3="$REDE-s3"
GANCHO="$REDE-alerta"
VOLUME="$REDE-trabalho"
AMBIENTE="paf-teste-o11"
ARQUIVO_ENV="$PAF/ambientes/teste-o11-$SUFIXO.env"
SENHA_PG="pg-teste-o11"
S3_USUARIO="o11admin"
S3_SENHA="o11-segredo-minio"
BUCKET_BACKUP="paf-backup-teste"
BUCKET_APP="paf-app-teste"
CHAVE_MIDIA="paf-midia/teste/midia-o11.png"
PRAZO_S=600
SAIDA="$(mktemp)"

limpar() {
  docker rm -f "$PG" "$S3" "$GANCHO" >/dev/null 2>&1
  docker volume rm "$VOLUME" >/dev/null 2>&1
  docker network rm "$REDE" >/dev/null 2>&1
  rm -f "$ARQUIVO_ENV" "$SAIDA"
}
trap limpar EXIT

falhas=0
resultados=()
registrar() {
  resultados+=("$1 $2")
  if [ "$1" = "FAIL" ]; then falhas=$((falhas + 1)); fi
}
abortar() {
  echo "PREPARO FALHOU: $1" >&2
  exit 1
}

psql_pg() { docker exec "$PG" psql -X -q -At -U fsh -d fsh -v ON_ERROR_STOP=1 -c "$1"; }

aws_s3() {
  docker run --rm --network "$REDE" -e AWS_ACCESS_KEY_ID="$S3_USUARIO" -e AWS_SECRET_ACCESS_KEY="$S3_SENHA" \
    -e AWS_DEFAULT_REGION=auto --entrypoint aws "$IMAGEM_BACKUP" --endpoint-url "http://$S3:9000" s3api "$@"
}

# One backup run in the environment's shape: same variables the compose backup service gets.
backup() {
  local segredo="$1"; shift
  timeout "$PRAZO_S" docker run --rm --network "$REDE" -v "$VOLUME:/var/lib/paf-backup" \
    -e TZ=America/Sao_Paulo -e PAF_AMBIENTE="$AMBIENTE" -e PGHOST="$PG" -e PGUSER=fsh -e PGDATABASE=fsh \
    -e PGPASSWORD="$SENHA_PG" -e PGCONNECT_TIMEOUT=15 -e PAF_BACKUP_ENDPOINT="http://$S3:9000" \
    -e PAF_BACKUP_BUCKET="$BUCKET_BACKUP" -e AWS_ACCESS_KEY_ID="$S3_USUARIO" -e AWS_SECRET_ACCESS_KEY="$segredo" \
    -e AWS_DEFAULT_REGION=auto -e PAF_BACKUP_ALERTA_URL="http://$GANCHO:8080/alerta" \
    "$IMAGEM_BACKUP" "$@"
}

alertas() { docker exec "$GANCHO" sh -c 'cat /alertas/log 2>/dev/null' | grep -c 'PAF backup FALHOU' || true; }

copias() {
  aws_s3 list-objects-v2 --bucket "$BUCKET_BACKUP" --prefix "$AMBIENTE/copias/" --query "Contents[].Key" --output text \
    | tr '\t' '\n' | awk '/\/fsh-/' | sort
}

restaurar() {
  timeout "$PRAZO_S" env -u MSYS_NO_PATHCONV bash "$PAF/restaurar-backup.sh" "$ARQUIVO_ENV" "$1"
}

# ---------------------------------------------------------------- fixture
docker network create "$REDE" >/dev/null || abortar "rede"
docker volume create "$VOLUME" >/dev/null || abortar "volume"
docker run -d --name "$PG" --network "$REDE" -e TZ=America/Sao_Paulo -e POSTGRES_DB=fsh -e POSTGRES_USER=fsh \
  -e POSTGRES_PASSWORD="$SENHA_PG" -v "$(host_path "$PAF/../docker/postgres-init"):/docker-entrypoint-initdb.d:ro" \
  postgres:18-alpine -c timezone=America/Sao_Paulo >/dev/null || abortar "postgres"
# Published on loopback: the restore project lives on its own network and reaches the "R2" the way it reaches R2.
docker run -d --name "$S3" --network "$REDE" -p 127.0.0.1::9000 -e MINIO_ROOT_USER="$S3_USUARIO" \
  -e MINIO_ROOT_PASSWORD="$S3_SENHA" quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z server /data >/dev/null \
  || abortar "minio"
docker run -d --name "$GANCHO" --network "$REDE" -p 127.0.0.1::8080 alpine:3.20 sh -c \
  'mkdir -p /alertas; while true; do printf "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n" | nc -l -p 8080 >>/alertas/log; done' \
  >/dev/null || abortar "gancho de alerta"
PORTA_S3="$(docker port "$S3" 9000/tcp | head -n 1 | sed 's/.*://')"
PORTA_GANCHO="$(docker port "$GANCHO" 8080/tcp | head -n 1 | sed 's/.*://')"

pronto=0
for _ in $(seq 1 60); do
  if docker exec "$PG" pg_isready -U fsh -d fsh -h 127.0.0.1 >/dev/null 2>&1 \
      && aws_s3 list-buckets >/dev/null 2>&1; then pronto=1; break; fi
  sleep 1
done
[ "$pronto" -eq 1 ] || abortar "postgres ou minio não ficaram prontos"
aws_s3 create-bucket --bucket "$BUCKET_BACKUP" >/dev/null || abortar "bucket de backup"
aws_s3 create-bucket --bucket "$BUCKET_APP" >/dev/null || abortar "bucket da aplicação"

CONEXAO="Host=$PG;Database=fsh;Username=fsh;Password=$SENHA_PG;Timeout=15;Command Timeout=30;GSS Encryption Mode=Disable"
for verbo in apply seed-demo; do
  timeout "$PRAZO_S" docker run --rm --network "$REDE" -e DOTNET_ENVIRONMENT=Development -e TZ=America/Sao_Paulo \
    -e "DatabaseOptions__ConnectionString=$CONEXAO" -e JwtOptions__SigningKey=paf-teste-o11-signing-key-0123456789abcdef \
    "$IMAGEM_MIGRATOR" "$verbo" >"$SAIDA" 2>&1 || { tail -n 20 "$SAIDA"; abortar "migrator $verbo"; }
done

# One confirmed mídia whose object exists in the application bucket.
psql_pg "WITH u AS (SELECT \"Id\", \"TenantId\" FROM paf.\"Unidades\" ORDER BY \"Id\" LIMIT 1),
  e AS (INSERT INTO paf.\"EnviosMidia\" (\"Id\", \"TenantId\", \"UnidadeId\", \"ChaveObjeto\", \"Tipo\", \"ContentType\", \"ConfirmadoEm\")
        SELECT gen_random_uuid(), u.\"TenantId\", u.\"Id\", '$CHAVE_MIDIA', 'Imagem', 'image/png', now() FROM u
        RETURNING \"Id\", \"TenantId\", \"UnidadeId\", \"ChaveObjeto\")
  INSERT INTO paf.\"Midias\" (\"Id\", \"TenantId\", \"UnidadeId\", \"Descricao\", \"Tipo\", \"EnvioId\", \"ChaveObjeto\", \"DuracaoSegundos\", \"Ativa\")
  SELECT gen_random_uuid(), e.\"TenantId\", e.\"UnidadeId\", 'teste o11', 'Imagem', e.\"Id\", e.\"ChaveObjeto\", 10, true FROM e;" \
  || abortar "mídia de teste"
docker run --rm --network "$REDE" -e AWS_ACCESS_KEY_ID="$S3_USUARIO" -e AWS_SECRET_ACCESS_KEY="$S3_SENHA" \
  -e AWS_DEFAULT_REGION=auto --entrypoint sh "$IMAGEM_BACKUP" -c "printf png >/tmp/midia && aws \
  --endpoint-url http://$S3:9000 s3api put-object --bucket $BUCKET_APP --key $CHAVE_MIDIA --body /tmp/midia >/dev/null" \
  || abortar "objeto da mídia"

cat >"$ARQUIVO_ENV" <<EOF
COMPOSE_PROJECT_NAME=$AMBIENTE
PAF_ARQUIVO_AMBIENTE=ambientes/$(basename "$ARQUIVO_ENV")
PAF_IMAGEM_BACKUP=$IMAGEM_BACKUP
PAF_IMAGEM_MIGRATOR=$IMAGEM_MIGRATOR
DOTNET_ENVIRONMENT=Production
TZ=America/Sao_Paulo
JwtOptions__SigningKey=paf-teste-o11-signing-key-0123456789abcdef
QuotaOptions__Enabled=false
CachingOptions__Redis=valkey:6379,password=nenhuma,connectTimeout=5000,syncTimeout=5000,asyncTimeout=5000
PAF_BACKUP_ENDPOINT=http://host.docker.internal:$PORTA_S3
PAF_BACKUP_BUCKET=$BUCKET_BACKUP
PAF_BACKUP_ACCESS_KEY=$S3_USUARIO
PAF_BACKUP_SECRET_KEY=$S3_SENHA
PAF_BACKUP_ALERTA_URL=http://host.docker.internal:$PORTA_GANCHO/alerta
Storage__Provider=s3
Storage__S3__ServiceUrl=http://host.docker.internal:$PORTA_S3
Storage__S3__Bucket=$BUCKET_APP
Storage__S3__AccessKey=$S3_USUARIO
Storage__S3__SecretKey=$S3_SENHA
EOF

# ---------------------------------------------------------------- cases
# 1. Happy path: three objects, sidecar last, hash matches, healthcheck green.
backup "$S3_SENHA" agora >"$SAIDA" 2>&1
codigo=$?
COPIA="$(copias | awk '/\.sha256$/' | tail -n 1)"
COPIA="${COPIA%.sha256}"
if [ "$codigo" -eq 0 ] && [ -n "$COPIA" ] && [ "$(copias | grep -c "^$COPIA\.")" -eq 3 ]; then
  registrar PASS "backup grava .dump, .contagens e .sha256 (código 0, $COPIA)"
else
  registrar FAIL "backup feliz: código $codigo, cópia '$COPIA'"; tail -n 5 "$SAIDA"
fi
if [[ "$COPIA" =~ ^$AMBIENTE/copias/[0-9]{4}-[0-9]{2}-[0-9]{2}/fsh-[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{6}$ ]]; then
  registrar PASS "chave da cópia com data de Brasília e sem reaproveitamento"
else
  registrar FAIL "chave da cópia fora do formato: $COPIA"
fi
if backup "$S3_SENHA" saude >"$SAIDA" 2>&1; then registrar PASS "saude verde depois do sucesso"
else registrar FAIL "saude vermelho depois do sucesso: $(cat "$SAIDA")"; fi

# 2. Restore test of the good copy: sha256, counts, pending migrations = 0, mídia HEAD, report registered.
restaurar ultima >"$SAIDA" 2>&1
codigo=$?
relatorios="$(aws_s3 list-objects-v2 --bucket "$BUCKET_BACKUP" --prefix "$AMBIENTE/restauracoes/" --query 'length(Contents[])' --output text 2>/dev/null)"
if [ "$codigo" -eq 0 ] && grep -q '"midiasConferidas":1' "$SAIDA" && grep -q "RESTAURACAO OK" "$SAIDA" && [ "$relatorios" = "1" ]; then
  registrar PASS "restauração da última cópia aprovada e registrada no bucket"
else
  registrar FAIL "restauração feliz: código $codigo, relatórios '$relatorios'"; grep -E "ETAPA|FALHOU|mensagem" "$SAIDA" | tail -n 8
fi

# 3. Concurrent run: while another run holds the flock, a second run leaves with 75, uploads nothing, alerts nothing.
antes_copias="$(copias | wc -l)"
antes_alertas="$(alertas)"
TRAVA="$REDE-trava"
docker run -d --name "$TRAVA" -v "$VOLUME:/var/lib/paf-backup" --entrypoint sh "$IMAGEM_BACKUP" \
  -c 'exec 9>/var/lib/paf-backup/backup.lock; flock 9; touch /var/lib/paf-backup/trava-ok; sleep 120' >/dev/null
for _ in $(seq 1 30); do docker exec "$TRAVA" test -f /var/lib/paf-backup/trava-ok 2>/dev/null && break; sleep 1; done
backup "$S3_SENHA" agora >"$SAIDA" 2>&1
codigo=$?
docker rm -f "$TRAVA" >/dev/null
docker run --rm -v "$VOLUME:/var/lib/paf-backup" --entrypoint rm "$IMAGEM_BACKUP" -f /var/lib/paf-backup/trava-ok
if [ "$codigo" -eq 75 ] && [ "$(copias | wc -l)" -eq "$antes_copias" ] && [ "$(alertas)" -eq "$antes_alertas" ]; then
  registrar PASS "execução simultânea sai no flock (código 75, nada enviado, sem alerta)"
else
  registrar FAIL "execução simultânea: código $codigo"; tail -n 3 "$SAIDA"
fi

# 4. Wrong credential: non-zero, alert delivered, healthcheck red.
antes_alertas="$(alertas)"
backup "credencial-errada" agora >"$SAIDA" 2>&1
codigo=$?
if [ "$codigo" -ne 0 ] && [ "$(alertas)" -gt "$antes_alertas" ] && grep -q '"etapa":"envio"' "$SAIDA"; then
  registrar PASS "credencial errada falha com alerta (código $codigo)"
else
  registrar FAIL "credencial errada: código $codigo, alertas $(alertas) (antes $antes_alertas)"; tail -n 3 "$SAIDA"
fi
if ! backup "$S3_SENHA" saude >"$SAIDA" 2>&1 && grep -q "falhou" "$SAIDA"; then registrar PASS "saude vermelho depois da falha"
else registrar FAIL "saude não acusou a falha: $(cat "$SAIDA")"; fi

# 5. Upload interrupted (storage gone mid-run): non-zero, alert, and no .sha256 for the run.
antes_completas="$(copias | grep -c '\.sha256$')"
antes_alertas="$(alertas)"
docker stop "$S3" >/dev/null
backup "$S3_SENHA" agora >"$SAIDA" 2>&1
codigo=$?
docker start "$S3" >/dev/null
for _ in $(seq 1 30); do aws_s3 list-buckets >/dev/null 2>&1 && break; sleep 1; done
# A restarted container gets a new ephemeral host port: the restore project reaches the "R2" through it.
NOVA_PORTA_S3="$(docker port "$S3" 9000/tcp | head -n 1 | sed 's/.*://')"
sed -i "s#host.docker.internal:$PORTA_S3#host.docker.internal:$NOVA_PORTA_S3#g" "$ARQUIVO_ENV"
PORTA_S3="$NOVA_PORTA_S3"
if [ "$codigo" -ne 0 ] && [ "$(alertas)" -gt "$antes_alertas" ] && [ "$(copias | grep -c '\.sha256$')" -eq "$antes_completas" ]; then
  registrar PASS "envio interrompido falha com alerta e sem .sha256 (código $codigo)"
else
  registrar FAIL "envio interrompido: código $codigo"; tail -n 3 "$SAIDA"
fi

# 6. Partial copy: a truncated dump beside the original hash is rejected by the sha256 step, before pg_restore.
BASE="${COPIA##*/}"
PARCIAL="$AMBIENTE/copias/2000-01-01/$BASE"
INCOMPLETA="$AMBIENTE/copias/2999-12-31/fsh-2999-12-31T235959"
aws_baixar() { docker run --rm --network "$REDE" -e AWS_ACCESS_KEY_ID="$S3_USUARIO" \
  -e AWS_SECRET_ACCESS_KEY="$S3_SENHA" -e AWS_DEFAULT_REGION=auto --entrypoint sh "$IMAGEM_BACKUP" -c "$1"; }
aws_baixar "set -e; E='--endpoint-url http://$S3:9000'; mkdir -p /w
  for x in dump contagens sha256; do aws \$E s3api get-object --bucket $BUCKET_BACKUP --key $COPIA.\$x /w/\$x >/dev/null; done
  head -c 1000 /w/dump >/w/parcial
  aws \$E s3api put-object --bucket $BUCKET_BACKUP --key $PARCIAL.dump --body /w/parcial >/dev/null
  aws \$E s3api put-object --bucket $BUCKET_BACKUP --key $PARCIAL.contagens --body /w/contagens >/dev/null
  aws \$E s3api put-object --bucket $BUCKET_BACKUP --key $PARCIAL.sha256 --body /w/sha256 >/dev/null
  aws \$E s3api put-object --bucket $BUCKET_BACKUP --key $INCOMPLETA.dump --body /w/dump >/dev/null" \
  || registrar FAIL "preparo da cópia parcial"
restaurar "$PARCIAL" >"$SAIDA" 2>&1
codigo=$?
if [ "$codigo" -ne 0 ] && grep -q '"etapa":"sha256"' "$SAIDA"; then
  registrar PASS "sha256 rejeita a cópia parcial (código $codigo)"
else
  registrar FAIL "cópia parcial: código $codigo, sem recusa na etapa sha256"; grep -E "ETAPA|FALHOU" "$SAIDA" | tail -n 5
fi

# 7. Copy without .sha256 (upload interrupted before the sidecar): explicit key refused; `ultima` skips it.
restaurar "$INCOMPLETA" >"$SAIDA" 2>&1
codigo=$?
if [ "$codigo" -ne 0 ] && grep -q "$INCOMPLETA.sha256 ausente: envio incompleto" "$SAIDA"; then registrar PASS "cópia sem .sha256 recusada (código $codigo)"
else registrar FAIL "cópia sem .sha256: código $codigo"; grep -E "ETAPA|FALHOU" "$SAIDA" | tail -n 5; fi

# 8. Missing mídia object: the restore fails on the HEAD of the restored key.
aws_s3 delete-object --bucket "$BUCKET_APP" --key "$CHAVE_MIDIA" >/dev/null
restaurar ultima >"$SAIDA" 2>&1
codigo=$?
if [ "$codigo" -ne 0 ] && grep -q '"etapa":"midias"' "$SAIDA" && grep -q "ETAPA conferir: código 1" "$SAIDA" \
    && grep -q "ETAPA restaurar: código 0" "$SAIDA"; then
  registrar PASS "mídia ausente reprova a restauração (código $codigo)"
else
  registrar FAIL "mídia ausente: código $codigo"; grep -E "ETAPA|FALHOU" "$SAIDA" | tail -n 5
fi

# 9. Pending migration in the copy: the restore fails (the migrator applies it or cannot).
ULTIMA_MIGRACAO="$(psql_pg 'SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 1;')"
psql_pg "DELETE FROM public.\"__EFMigrationsHistory\" WHERE \"MigrationId\" = '$ULTIMA_MIGRACAO';" >/dev/null
backup "$S3_SENHA" agora >"$SAIDA" 2>&1 || registrar FAIL "backup para o caso de migração pendente"
psql_pg "INSERT INTO public.\"__EFMigrationsHistory\" VALUES ('$ULTIMA_MIGRACAO', '10.0.0');" >/dev/null
restaurar ultima >"$SAIDA" 2>&1
codigo=$?
if [ "$codigo" -ne 0 ] && grep -q "ETAPA restaurar: código 0" "$SAIDA" && grep -Eq 'ETAPA (migrator: código [1-9]|conferir: código 1)' "$SAIDA"; then
  registrar PASS "migração pendente na cópia reprova a restauração (código $codigo)"
else
  registrar FAIL "migração pendente: código $codigo"; grep -E "ETAPA|FALHOU" "$SAIDA" | tail -n 5
fi

# 10. A tenant with its own database would be outside the dump: the backup refuses and alerts.
antes_alertas="$(alertas)"
psql_pg "UPDATE tenant.\"Tenants\" SET \"ConnectionString\" = 'Host=outro' WHERE \"Id\" = 'globex';" >/dev/null
backup "$S3_SENHA" agora >"$SAIDA" 2>&1
codigo=$?
psql_pg "UPDATE tenant.\"Tenants\" SET \"ConnectionString\" = '' WHERE \"Id\" = 'globex';" >/dev/null
if [ "$codigo" -ne 0 ] && grep -q '"etapa":"catalogo"' "$SAIDA" && [ "$(alertas)" -gt "$antes_alertas" ]; then
  registrar PASS "tenant com banco próprio recusado com alerta (código $codigo)"
else
  registrar FAIL "tenant com banco próprio: código $codigo"; tail -n 3 "$SAIDA"
fi

# 11. Bucket without lock: verificar-bloqueio must fail (the check is not vacuous; the pass case is R2 only).
backup "$S3_SENHA" verificar-bloqueio >"$SAIDA" 2>&1
codigo=$?
if [ "$codigo" -ne 0 ] && grep -q "FAIL sobrescrita aceita" "$SAIDA" && grep -q "FAIL exclusão aceita" "$SAIDA"; then
  registrar PASS "verificar-bloqueio reprova bucket sem bloqueio (código $codigo)"
else
  registrar FAIL "verificar-bloqueio: código $codigo"; tail -n 5 "$SAIDA"
fi
registrar SKIPPED "sobrescrita e exclusão recusadas em 7 dias: só no R2 real (verificar-bloqueio, passo humano)"

# 12. Scheduler: the next run is at 03:00 in Brasília; any other TZ is refused.
docker run -d --name "$REDE-agenda" -e TZ=America/Sao_Paulo -e PAF_AMBIENTE="$AMBIENTE" \
  -e PAF_BACKUP_ALERTA_URL="http://$GANCHO:8080/alerta" --network "$REDE" "$IMAGEM_BACKUP" agendar >/dev/null
sleep 3
espera="$(docker logs "$REDE-agenda" 2>&1 | sed -n 's/.*próxima execução em \([0-9]*\) s.*/\1/p' | head -n 1)"
agora_brt="$(docker exec "$REDE-agenda" date '+%H %M %S')"
docker rm -f "$REDE-agenda" >/dev/null
read -r h m s <<<"$agora_brt"
alvo=$(((10#$h * 3600 + 10#$m * 60 + 10#$s + ${espera:-0}) % 86400))
if [ -n "$espera" ] && [ "$espera" -ge 1 ] && [ "$espera" -le 86400 ] && [ "$alvo" -ge 10795 ] && [ "$alvo" -le 10805 ]; then
  registrar PASS "agendador mira 03:00 BRT (espera $espera s)"
else
  registrar FAIL "agendador: espera '$espera', hora local $agora_brt, alvo $alvo"
fi
timeout 60 docker run --rm -e TZ=UTC -e PAF_AMBIENTE="$AMBIENTE" -e PAF_BACKUP_ALERTA_URL="http://$GANCHO:8080/alerta" \
  --network "$REDE" "$IMAGEM_BACKUP" agendar >"$SAIDA" 2>&1
codigo=$?
if [ "$codigo" -eq 1 ] && grep -q "TZ=UTC" "$SAIDA"; then registrar PASS "agendador recusa TZ fora de Brasília"
else registrar FAIL "agendador com TZ=UTC: código $codigo"; tail -n 3 "$SAIDA"; fi

echo "=== resultado"
printf '%s\n' "${resultados[@]}"
if [ "$falhas" -eq 0 ]; then echo "BACKUP-TESTE OK (${#resultados[@]} casos)"; exit 0; fi
echo "BACKUP-TESTE FALHOU ($falhas de ${#resultados[@]})"
exit 1
