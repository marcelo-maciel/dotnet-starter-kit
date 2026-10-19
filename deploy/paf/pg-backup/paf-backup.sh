#!/usr/bin/env bash
# Daily backup of the PAF database to the environment's R2 backup bucket, and the restore test (O11, RNF14, ARQ l.388).
#
#   paf-backup agendar                  long-running: one backup a day at 03:00 in TZ (America/Sao_Paulo)
#   paf-backup agora                    one backup now; exit 75 when another run holds the lock
#   paf-backup saude                    container healthcheck: last run succeeded and is less than 26 h old
#   paf-backup verificar-bloqueio       proves the bucket lock: overwrite and delete of a fresh object refused
#   paf-backup restaurar <chave|ultima> restore test, part 1 (deploy/paf/restaurar-backup.sh drives it)
#   paf-backup conferir-restauracao     restore test, part 2, after the migrator ran on the restored database
#
# Backup objects, under <PAF_AMBIENTE>/copias/<YYYY-MM-DD>/fsh-<YYYY-MM-DDTHHMMSS> (BRT, never reused):
#   .dump       pg_dump -Fc taken inside an exported snapshot
#   .contagens  row count of every table, read inside the same snapshot (the restore compares against it)
#   .sha256     sha256sum of the two files above; written LAST, so a missing .sha256 means an incomplete upload
# Every PutObject is conditional (If-None-Match: *): this script never overwrites an object, and the bucket lock
# (human step in the README) makes overwrite and delete impossible for anyone during 7 days.
#
# Any failure: one JSON log line on stderr, POST to PAF_BACKUP_ALERTA_URL, a non-zero exit and the marker that turns
# the healthcheck unhealthy. Logs carry keys and table names only, never row data or credentials.
set -uo pipefail

TRABALHO=/var/lib/paf-backup
TRAVA="$TRABALHO/backup.lock"
MARCA_SUCESSO="$TRABALHO/ultimo-sucesso"
MARCA_FALHA="$TRABALHO/ultima-falha"
MARCA_INICIO="$TRABALHO/agendador-iniciado"
ESTADO_RESTAURACAO="$TRABALHO/restauracao"
CODIGO_TRAVA_OCUPADA=75
HORA_AGENDADA_S=$((3 * 3600))
IDADE_MAXIMA_SUCESSO_S=$((26 * 3600))
PRAZO_DUMP_S=3600
PRAZO_RESTAURACAO_S=3600
PRAZO_CONTAGEM_S=900
PRAZO_SNAPSHOT_S=30
PRAZO_ALERTA_S=10
PRAZO_CONEXAO_S3_S=15
PRAZO_LEITURA_S3_S=120
MODO="${1:-}"

json() {
  printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | tr '\n\r\t' '   '
}

log() {
  printf '{"momento":"%s","componente":"paf-backup","ambiente":"%s","modo":"%s","etapa":"%s","mensagem":"%s"}\n' \
    "$(date -Iseconds)" "$(json "${PAF_AMBIENTE:-?}")" "$(json "$MODO")" "$(json "$1")" "$(json "$2")" >&2
}

alertar() {
  local etapa="$1" mensagem="$2" corpo
  corpo="$(printf '{"text":"PAF backup FALHOU: ambiente=%s modo=%s etapa=%s: %s"}' \
    "$(json "${PAF_AMBIENTE:-?}")" "$(json "$MODO")" "$(json "$etapa")" "$(json "$mensagem")")"
  if ! curl -sS --fail --max-time "$PRAZO_ALERTA_S" -X POST -H 'content-type: application/json' \
      --data "$corpo" "$PAF_BACKUP_ALERTA_URL" >/dev/null 2>"$TRABALHO/alerta.stderr"; then
    log "alerta" "alerta NAO entregue: $(head -c 300 "$TRABALHO/alerta.stderr")"
  fi
}

falhar() {
  local etapa="$1" mensagem="$2"
  log "$etapa" "FALHOU: $mensagem"
  printf '%s %s\n' "$(date -Iseconds)" "$etapa" >"$MARCA_FALHA"
  alertar "$etapa" "$mensagem"
  exit 1
}

exigir() {
  local nome faltando=()
  for nome in "$@"; do
    if [ -z "${!nome:-}" ]; then faltando+=("$nome"); fi
  done
  if [ "${#faltando[@]}" -gt 0 ]; then
    # Without the alert URL there is nobody to tell: the exit code and the log line are all that is left.
    if [ -z "${PAF_BACKUP_ALERTA_URL:-}" ]; then
      log "configuracao" "FALHOU: variáveis ausentes: ${faltando[*]}"
      exit 78
    fi
    falhar "configuracao" "variáveis ausentes: ${faltando[*]}"
  fi
}

s3_backup() {
  aws --endpoint-url "$PAF_BACKUP_ENDPOINT" --cli-connect-timeout "$PRAZO_CONEXAO_S3_S" \
    --cli-read-timeout "$PRAZO_LEITURA_S3_S" s3api "$@"
}

enviar_sem_sobrescrever() {
  local arquivo="$1" chave="$2"
  s3_backup put-object --bucket "$PAF_BACKUP_BUCKET" --key "$chave" --body "$arquivo" --if-none-match '*' \
    >/dev/null 2>"$TRABALHO/s3.stderr" || falhar "envio" "$chave: $(head -c 400 "$TRABALHO/s3.stderr")"
}

baixar() {
  local chave="$1" destino="$2"
  s3_backup get-object --bucket "$PAF_BACKUP_BUCKET" --key "$chave" "$destino" \
    >/dev/null 2>"$TRABALHO/s3.stderr" || falhar "download" "$chave: $(head -c 400 "$TRABALHO/s3.stderr")"
}

# One line "schema.table|rows" per base table, sorted: the dump and the restored database must print the same lines.
readonly SQL_CONTAGENS="SELECT format('%s.%s|%s', table_schema, table_name,
  (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', table_schema, table_name),
   false, true, '')))[1]::text)
FROM information_schema.tables
WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema')
ORDER BY 1;"

psql_unico() {
  timeout "$PRAZO_CONTAGEM_S" psql -X -q -At -v ON_ERROR_STOP=1 -c "$1"
}

# Every applied EF migration of every __EFMigrationsHistory table, "schema|MigrationId", sorted.
historico_migracoes() {
  local esquemas esquema
  esquemas="$(psql_unico "SELECT n.nspname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.relname = '__EFMigrationsHistory' AND c.relkind IN ('r', 'p') ORDER BY 1;")" || return 1
  [ -n "$esquemas" ] || { echo "nenhuma tabela __EFMigrationsHistory" >&2; return 1; }
  for esquema in $esquemas; do
    psql_unico "SELECT '$esquema|' || \"MigrationId\" FROM \"$esquema\".\"__EFMigrationsHistory\" ORDER BY 1;" || return 1
  done
}

executar_backup() {
  exigir PAF_AMBIENTE PGHOST PGUSER PGDATABASE PGPASSWORD PAF_BACKUP_ENDPOINT PAF_BACKUP_BUCKET \
    AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY PAF_BACKUP_ALERTA_URL
  exec 9>"$TRAVA"
  if ! flock -n 9; then
    # Not a failure of the backup: the run that holds the lock alerts on its own outcome.
    log "trava" "outra execução segura a trava; saindo sem fazer nada"
    exit "$CODIGO_TRAVA_OCUPADA"
  fi

  local inicio base dir chave_base
  inicio="$(date +%Y-%m-%dT%H%M%S)"
  base="fsh-$inicio"
  chave_base="$PAF_AMBIENTE/copias/$(date +%Y-%m-%d)/$base"
  dir="$(mktemp -d "$TRABALHO/execucao.XXXXXX")" || falhar "preparo" "mktemp em $TRABALHO"
  trap "rm -rf '$dir'" EXIT
  log "inicio" "$chave_base"

  # A tenant with its own connection string keeps its data in another database, which this dump does not cover.
  local fora
  fora="$(psql_unico "SELECT count(*) FROM tenant.\"Tenants\" WHERE coalesce(\"ConnectionString\", '') <> '';" 2>"$dir/psql.stderr")" \
    || falhar "catalogo" "$(head -c 400 "$dir/psql.stderr")"
  [ "$fora" = "0" ] || falhar "catalogo" "$fora tenant(s) com banco próprio ficariam fora do backup"

  # Counts and dump read the same snapshot: concurrent writes at 03:00 cannot make the restore comparison flap.
  coproc SESSAO { psql -X -q -At -v ON_ERROR_STOP=1 2>&1; }
  local pid_sessao="$SESSAO_PID"
  printf '%s\n' "BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;" "SELECT pg_export_snapshot();" >&"${SESSAO[1]}"
  local snapshot linha
  read -r -t "$PRAZO_SNAPSHOT_S" snapshot <&"${SESSAO[0]}" || falhar "snapshot" "sem resposta do PostgreSQL"
  [[ "$snapshot" =~ ^[0-9A-F]+-[0-9A-F]+-[0-9]+$ ]] || falhar "snapshot" "resposta inesperada: $snapshot"

  printf '%s\n' "SET LOCAL statement_timeout = '${PRAZO_CONTAGEM_S}s';" "$SQL_CONTAGENS" '\echo FIM-CONTAGENS' >&"${SESSAO[1]}"
  : >"$dir/$base.contagens"
  while true; do
    read -r -t "$PRAZO_CONTAGEM_S" linha <&"${SESSAO[0]}" || falhar "contagens" "sessão encerrada ou sem resposta"
    [ "$linha" = "FIM-CONTAGENS" ] && break
    [[ "$linha" =~ ^[^|]+\|[0-9]+$ ]] || falhar "contagens" "linha inesperada: $linha"
    printf '%s\n' "$linha" >>"$dir/$base.contagens"
  done
  [ -s "$dir/$base.contagens" ] || falhar "contagens" "nenhuma tabela contada"

  timeout "$PRAZO_DUMP_S" pg_dump -Fc --snapshot="$snapshot" -f "$dir/$base.dump" 2>"$dir/pg_dump.stderr" \
    || falhar "dump" "$(head -c 400 "$dir/pg_dump.stderr")"
  printf '%s\n' "COMMIT;" '\q' >&"${SESSAO[1]}"
  wait "$pid_sessao" || falhar "snapshot" "a sessão do snapshot terminou com erro"

  # A dump pg_restore cannot list is truncated or corrupt: never ship it.
  pg_restore --list "$dir/$base.dump" >/dev/null 2>"$dir/pg_restore.stderr" \
    || falhar "conferencia-dump" "$(head -c 400 "$dir/pg_restore.stderr")"
  (cd "$dir" && sha256sum "$base.dump" "$base.contagens" >"$base.sha256") || falhar "sha256" "sha256sum falhou"

  enviar_sem_sobrescrever "$dir/$base.dump" "$chave_base.dump"
  enviar_sem_sobrescrever "$dir/$base.contagens" "$chave_base.contagens"
  enviar_sem_sobrescrever "$dir/$base.sha256" "$chave_base.sha256"

  printf '%s %s\n' "$(date -Iseconds)" "$chave_base" >"$MARCA_SUCESSO"
  rm -f "$MARCA_FALHA"
  log "fim" "OK $chave_base ($(stat -c %s "$dir/$base.dump") bytes, $(wc -l <"$dir/$base.contagens") tabelas)"
}

segundos_ate_proxima_execucao() {
  local h m s agora espera
  read -r h m s <<<"$(date '+%H %M %S')"
  agora=$((10#$h * 3600 + 10#$m * 60 + 10#$s))
  espera=$(((HORA_AGENDADA_S - agora + 86400) % 86400))
  [ "$espera" -eq 0 ] && espera=86400
  echo "$espera"
}

agendar() {
  exigir PAF_AMBIENTE PAF_BACKUP_ALERTA_URL TZ
  [ "$TZ" = "America/Sao_Paulo" ] || falhar "configuracao" "TZ=$TZ; o horário das 03:00 é de Brasília (RNF10)"
  date -Iseconds >"$MARCA_INICIO"
  trap 'log "agendador" "encerrado por sinal"; exit 0' TERM INT
  local espera codigo
  while true; do
    espera="$(segundos_ate_proxima_execucao)"
    log "agendador" "próxima execução em $espera s"
    sleep "$espera" &
    wait $!
    "$0" agora
    codigo=$?
    log "agendador" "execução terminou com código $codigo"
  done
}

saude() {
  local agora referencia
  agora="$(date +%s)"
  if [ -f "$MARCA_FALHA" ]; then
    echo "última execução falhou: $(cat "$MARCA_FALHA")"
    exit 1
  fi
  if [ -f "$MARCA_SUCESSO" ]; then referencia="$(stat -c %Y "$MARCA_SUCESSO")"
  elif [ -f "$MARCA_INICIO" ]; then referencia="$(stat -c %Y "$MARCA_INICIO")"
  else echo "agendador não iniciou"; exit 1; fi
  if [ $((agora - referencia)) -gt "$IDADE_MAXIMA_SUCESSO_S" ]; then
    echo "nenhum backup com sucesso nas últimas 26 h"
    exit 1
  fi
  echo "ok"
}

verificar_bloqueio() {
  exigir PAF_AMBIENTE PAF_BACKUP_ENDPOINT PAF_BACKUP_BUCKET AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY PAF_BACKUP_ALERTA_URL
  local chave dir falhas=0
  chave="$PAF_AMBIENTE/verificacao-bloqueio/$(date +%Y-%m-%dT%H%M%S)"
  dir="$(mktemp -d)"
  trap "rm -rf '$dir'" EXIT
  echo "original" >"$dir/original"
  echo "sobrescrita" >"$dir/sobrescrita"
  enviar_sem_sobrescrever "$dir/original" "$chave"
  # Unconditional on purpose: this is what a compromised host would send.
  if s3_backup put-object --bucket "$PAF_BACKUP_BUCKET" --key "$chave" --body "$dir/sobrescrita" >/dev/null 2>&1; then
    echo "FAIL sobrescrita aceita em $chave: o bucket não tem bloqueio"
    falhas=$((falhas + 1))
  else
    echo "PASS sobrescrita recusada"
  fi
  if s3_backup delete-object --bucket "$PAF_BACKUP_BUCKET" --key "$chave" >/dev/null 2>&1 \
      && ! s3_backup head-object --bucket "$PAF_BACKUP_BUCKET" --key "$chave" >/dev/null 2>&1; then
    echo "FAIL exclusão aceita em $chave: o bucket não tem bloqueio"
    falhas=$((falhas + 1))
  else
    echo "PASS exclusão recusada"
  fi
  [ "$falhas" -eq 0 ] || falhar "verificar-bloqueio" "bucket $PAF_BACKUP_BUCKET sem bloqueio efetivo ($falhas falha(s))"
  echo "VERIFICAR-BLOQUEIO OK ($chave fica retida pelo bloqueio e sai pelo ciclo de vida)"
}

ultima_copia_completa() {
  # Only copies whose .sha256 exists: the sidecar is written last, so its absence marks an interrupted upload.
  s3_backup list-objects-v2 --bucket "$PAF_BACKUP_BUCKET" --prefix "$PAF_AMBIENTE/copias/" \
    --query "Contents[].Key" --output text >"$TRABALHO/lista" 2>"$TRABALHO/s3.stderr" || return 1
  tr '\t' '\n' <"$TRABALHO/lista" | awk '/\/fsh-[^\/]*\.sha256$/' | sort | tail -n 1
}

restaurar() {
  local pedido="${2:-}"
  exigir PAF_AMBIENTE PGHOST PGUSER PGDATABASE PGPASSWORD PAF_BACKUP_ENDPOINT PAF_BACKUP_BUCKET \
    AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY PAF_BACKUP_ALERTA_URL
  [ -n "$pedido" ] || falhar "restaurar" "uso: paf-backup restaurar <chave sem extensão|ultima>"
  local chave_base base dir
  if [ "$pedido" = "ultima" ]; then
    chave_base="$(ultima_copia_completa)" || falhar "selecao" "$(head -c 400 "$TRABALHO/s3.stderr")"
    [ -n "$chave_base" ] || falhar "selecao" "nenhuma cópia completa em $PAF_BACKUP_BUCKET/$PAF_AMBIENTE/"
    chave_base="${chave_base%.sha256}"
  else
    chave_base="$pedido"
    if ! s3_backup head-object --bucket "$PAF_BACKUP_BUCKET" --key "$chave_base.sha256" >/dev/null 2>"$TRABALHO/s3.stderr"; then
      # Only a 404 means "incomplete upload"; anything else (network, credential) is reported as itself.
      grep -q "(404)" "$TRABALHO/s3.stderr" || falhar "selecao" "$(head -c 400 "$TRABALHO/s3.stderr")"
      falhar "selecao" "$chave_base.sha256 ausente: envio incompleto, cópia recusada"
    fi
  fi
  base="${chave_base##*/}"
  rm -rf "$ESTADO_RESTAURACAO"
  mkdir -p "$ESTADO_RESTAURACAO"
  dir="$(mktemp -d "$TRABALHO/restauracao-download.XXXXXX")"
  trap "rm -rf '$dir'" EXIT
  log "inicio" "$chave_base"

  baixar "$chave_base.sha256" "$dir/$base.sha256"
  baixar "$chave_base.dump" "$dir/$base.dump"
  baixar "$chave_base.contagens" "$dir/$base.contagens"
  (cd "$dir" && sha256sum -c "$base.sha256") >"$dir/sha256.saida" 2>&1 \
    || falhar "sha256" "cópia corrompida ou parcial: $(head -c 300 "$dir/sha256.saida")"

  timeout "$PRAZO_RESTAURACAO_S" pg_restore --no-owner --no-privileges --exit-on-error -d "$PGDATABASE" \
    "$dir/$base.dump" 2>"$dir/pg_restore.stderr" || falhar "pg_restore" "$(head -c 400 "$dir/pg_restore.stderr")"

  psql_unico "$SQL_CONTAGENS" >"$dir/restaurado.contagens" 2>"$dir/psql.stderr" \
    || falhar "contagens" "$(head -c 400 "$dir/psql.stderr")"
  if ! diff "$dir/$base.contagens" "$dir/restaurado.contagens" >"$dir/contagens.diff"; then
    falhar "contagens" "contagens diferentes do backup: $(grep '^[<>]' "$dir/contagens.diff" | head -n 10 | tr '\n' ' ')"
  fi
  historico_migracoes >"$ESTADO_RESTAURACAO/historico-antes" 2>"$dir/psql.stderr" \
    || falhar "historico" "$(head -c 400 "$dir/psql.stderr")"
  printf '%s\n' "$chave_base" >"$ESTADO_RESTAURACAO/chave"
  wc -l <"$dir/$base.contagens" >"$ESTADO_RESTAURACAO/tabelas"
  log "fim" "restaurado e conferido: sha256, $(cat "$ESTADO_RESTAURACAO/tabelas") contagens; falta o migrator e a mídia"
}

# Same rule as the kit's S3StorageService.NormalizeKey: the prefix goes in front unless the key already starts with it.
chave_no_bucket() {
  local chave="${1#/}" prefixo="${Storage__S3__Prefix:-}"
  if [ -z "$prefixo" ]; then printf '%s' "$chave"; return; fi
  local inicio="${chave:0:${#prefixo}}"
  if [ "${inicio,,}" = "${prefixo,,}" ]; then printf '%s' "$chave"; return; fi
  printf '%s/%s' "${prefixo%/}" "$chave"
}

conferir_restauracao() {
  exigir PAF_AMBIENTE PGHOST PGUSER PGDATABASE PGPASSWORD PAF_BACKUP_ENDPOINT PAF_BACKUP_BUCKET \
    AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY PAF_BACKUP_ALERTA_URL \
    Storage__S3__ServiceUrl Storage__S3__Bucket Storage__S3__AccessKey Storage__S3__SecretKey
  [ -f "$ESTADO_RESTAURACAO/chave" ] || falhar "conferir" "rode 'paf-backup restaurar' antes"
  local chave_base dir pendentes total=0 ausentes=0 chave alvo
  chave_base="$(cat "$ESTADO_RESTAURACAO/chave")"
  dir="$(mktemp -d "$TRABALHO/restauracao-conferencia.XXXXXX")"
  trap "rm -rf '$dir'" EXIT

  # The migrator of the deployed release ran `apply` on the restored copy in between: any row it added was pending.
  historico_migracoes >"$dir/historico-depois" 2>"$dir/psql.stderr" || falhar "historico" "$(head -c 400 "$dir/psql.stderr")"
  pendentes="$(comm -13 "$ESTADO_RESTAURACAO/historico-antes" "$dir/historico-depois" | wc -l)"
  [ "$pendentes" -eq 0 ] || falhar "migracoes" "$pendentes migração(ões) pendente(s) na cópia: $(comm -13 "$ESTADO_RESTAURACAO/historico-antes" "$dir/historico-depois" | head -n 5 | tr '\n' ' ')"

  psql_unico "SELECT \"ChaveObjeto\" FROM paf.\"Midias\" ORDER BY 1;" >"$dir/midias" 2>"$dir/psql.stderr" \
    || falhar "midias" "$(head -c 400 "$dir/psql.stderr")"
  # ponytail: one HEAD per mídia, serial; fine for the V2 volume (tens per unidade). Batch or parallelise when the
  # restore test takes longer than its prazo.
  while IFS= read -r chave; do
    [ -n "$chave" ] || continue
    total=$((total + 1))
    alvo="$(chave_no_bucket "$chave")"
    if ! AWS_ACCESS_KEY_ID="$Storage__S3__AccessKey" AWS_SECRET_ACCESS_KEY="$Storage__S3__SecretKey" \
        aws --endpoint-url "$Storage__S3__ServiceUrl" --cli-connect-timeout "$PRAZO_CONEXAO_S3_S" \
        --cli-read-timeout "$PRAZO_LEITURA_S3_S" s3api head-object --bucket "$Storage__S3__Bucket" --key "$alvo" \
        >/dev/null 2>>"$dir/head.stderr"; then
      ausentes=$((ausentes + 1))
      printf '%s\n' "$alvo" >>"$dir/ausentes"
    fi
  done <"$dir/midias"
  [ "$ausentes" -eq 0 ] || falhar "midias" "$ausentes de $total mídia(s) sem objeto no bucket da aplicação: $(head -n 5 "$dir/ausentes" | tr '\n' ' ')"

  local relatorio="$dir/relatorio.json" chave_relatorio
  chave_relatorio="$PAF_AMBIENTE/restauracoes/$(date +%Y-%m-%dT%H%M%S).json"
  printf '{"ambiente":"%s","copia":"%s","sha256":"ok","tabelasConferidas":%s,"migracoesPendentes":0,"midiasConferidas":%s,"midiasAusentes":0,"terminouEm":"%s","resultado":"OK"}\n' \
    "$(json "$PAF_AMBIENTE")" "$(json "$chave_base")" "$(cat "$ESTADO_RESTAURACAO/tabelas")" "$total" "$(date -Iseconds)" >"$relatorio"
  # Registered off-host, next to the copies it tested (RNF14: "teste de restauração registrado").
  enviar_sem_sobrescrever "$relatorio" "$chave_relatorio"
  cat "$relatorio"
  log "fim" "RESTAURACAO OK $chave_base, registrada em $chave_relatorio"
}

mkdir -p "$TRABALHO"
case "$MODO" in
  agendar) agendar ;;
  agora) executar_backup ;;
  saude) saude ;;
  verificar-bloqueio) verificar_bloqueio ;;
  restaurar) restaurar "$@" ;;
  conferir-restauracao) conferir_restauracao ;;
  *)
    echo "uso: paf-backup agendar|agora|saude|verificar-bloqueio|restaurar <chave|ultima>|conferir-restauracao" >&2
    exit 2
    ;;
esac
