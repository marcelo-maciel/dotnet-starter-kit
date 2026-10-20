#!/usr/bin/env bash
# Negative and positive cases of verificar-ambiente.sh (O02). Builds a valid pair of environment files from the
# .exemplo files, proves it passes, then breaks one invariant per case and proves the script fails on that check.
# Exit 0 only when every case behaved as expected. Needs what verificar-ambiente.sh needs; starts no container.
#
# Usage: deploy/paf/verificar-ambiente.test.sh
set -uo pipefail

AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
AMBIENTES="$AQUI/ambientes"
# Inside ambientes/ because the compose resolves PAF_ARQUIVO_AMBIENTE from deploy/paf; *.env is gitignored there.
PROD="$AMBIENTES/teste-producao.env"
HOMOLOG="$AMBIENTES/teste-homologacao.env"
SAIDA="$(mktemp)"
trap 'rm -f "$PROD" "$HOMOLOG" "$SAIDA"' EXIT

preencher() {
  local exemplo="$1" destino="$2" sufixo="$3" tunel
  # One tunnel per environment: a UUID derived from the suffix length keeps the two different.
  tunel="$(printf '00000000-0000-4000-8000-%012d' "${#sufixo}")"
  sed -e "s#<uuid-do-tunel>#$tunel#" \
      -e "s#<credenciais-json-do-tunel>#{\"AccountTag\":\"conta-teste\",\"TunnelSecret\":\"c2VncmVkby10ZXN0ZQ==\",\"TunnelID\":\"$tunel\"}#" \
      -e "s#^PAF_ARQUIVO_AMBIENTE=.*#PAF_ARQUIVO_AMBIENTE=ambientes/$(basename "$destino")#" \
      -e "s#^POSTGRES_PASSWORD=.*#POSTGRES_PASSWORD=pg-$sufixo-1#" \
      -e "s#^VALKEY_PASSWORD=.*#VALKEY_PASSWORD=vk-$sufixo-1#" \
      -e "s#^JwtOptions__SigningKey=.*#JwtOptions__SigningKey=paf-$sufixo-signing-key-ascii-0123456789abcdef#" \
      -e "s#<account-id>#conta-teste#" -e "s#<segredo>#segredo-teste#g" -e "s#<usuario>#usuario-teste#g" \
      -e "s#<smtp>#smtp.teste#" -e "s#<tag>#teste#" \
      -e "s#^PAF_BACKUP_ACCESS_KEY=.*#PAF_BACKUP_ACCESS_KEY=backup-$sufixo-1#" \
      -e "s#<webhook-de-alerta>#alerta.teste/hook#" \
      "$exemplo" >"$destino"
}

restaurar() {
  preencher "$AMBIENTES/producao.env.exemplo" "$PROD" producao
  preencher "$AMBIENTES/homologacao.env.exemplo" "$HOMOLOG" homologacao
}

# Replaces (or deletes, with an empty replacement) the line of KEY in FILE; fails the case when KEY is missing.
trocar() {
  local arquivo="$1" chave="$2" linha="$3"
  if ! grep -q "^$chave=" "$arquivo"; then
    echo "ERRO do teste: $chave não existe em $arquivo" >&2
    return 1
  fi
  local temporario="$arquivo.tmp"
  awk -v chave="$chave" -v linha="$linha" 'index($0, chave "=") == 1 { if (linha != "") print linha; next } { print }' "$arquivo" >"$temporario"
  mv "$temporario" "$arquivo"
}

# Appends a line the .exemplo files do not have (a setting that must never be set there).
acrescentar() {
  printf '%s\n' "$2" >>"$1"
}

falhas=0
resultados=()
registrar() {
  resultados+=("$1 $2")
  if [ "$1" != "PASS" ]; then falhas=$((falhas + 1)); fi
}

# Runs the verifier and expects a non-zero exit with EXPECTED among its FAIL lines.
esperar_falha() {
  local caso="$1" esperado="$2"
  bash "$AQUI/verificar-ambiente.sh" "$PROD" "$HOMOLOG" >"$SAIDA" 2>&1
  local codigo=$?
  if [ "$codigo" -ne 0 ] && grep -F "FAIL" "$SAIDA" | grep -qF "$esperado"; then
    registrar PASS "$caso (código $codigo)"
  else
    registrar FAIL "$caso: código $codigo, esperado FAIL com '$esperado'"
    grep -F "FAIL" "$SAIDA" | head -n 5
  fi
}

caso() {
  local nome="$1" esperado="$2"; shift 2
  restaurar
  if "$@"; then esperar_falha "$nome" "$esperado"; else registrar FAIL "$nome: mutação não aplicada"; fi
}

restaurar
bash "$AQUI/verificar-ambiente.sh" "$PROD" "$HOMOLOG" >"$SAIDA" 2>&1
codigo=$?
if [ "$codigo" -eq 0 ] && grep -q "VERIFICAR-AMBIENTE OK" "$SAIDA"; then registrar PASS "par válido aprovado"
else registrar FAIL "par válido reprovado (código $codigo)"; grep -F "FAIL" "$SAIDA" | head -n 10; fi

caso "quotas ausente" "QuotaOptions__Enabled=false explícito" trocar "$PROD" QuotaOptions__Enabled ""
caso "quotas true" "QuotaOptions__Enabled=false explícito" trocar "$HOMOLOG" QuotaOptions__Enabled "QuotaOptions__Enabled=true"
caso "chave não ASCII" "chave de assinatura só ASCII imprimível" trocar "$PROD" JwtOptions__SigningKey "JwtOptions__SigningKey=paf-chave-de-assinatura-com-acentuação-0123456789"
caso "chave curta" "chave de assinatura com 32+ caracteres" trocar "$PROD" JwtOptions__SigningKey "JwtOptions__SigningKey=curta-0123456789"
caso "chave de exemplo" "chave de assinatura não é o exemplo" trocar "$PROD" JwtOptions__SigningKey "JwtOptions__SigningKey=replace-with-a-random-ascii-key-of-64-chars"
caso "mesma chave nos dois" "chaves de assinatura diferentes" trocar "$HOMOLOG" JwtOptions__SigningKey "JwtOptions__SigningKey=paf-producao-signing-key-ascii-0123456789abcdef"
caso "KnownNetworks vazio" "KnownNetworks não vazio" trocar "$PROD" TrustedProxyOptions__KnownNetworks__0 "TrustedProxyOptions__KnownNetworks__0="
caso "KnownNetworks de outra rede" "KnownNetworks = subnet da rede do projeto" trocar "$PROD" TrustedProxyOptions__KnownNetworks__0 "TrustedProxyOptions__KnownNetworks__0=172.17.0.0/16"
caso "limite auth do kit" "limite de taxa Auth >= 15" trocar "$PROD" RateLimitingOptions__Auth__PermitLimit "RateLimitingOptions__Auth__PermitLimit=10"
caso "limite por IP ausente" "limite de taxa Ip >= 387" trocar "$HOMOLOG" RateLimitingOptions__Ip__PermitLimit ""
caso "limite do tenant do kit" "limite de taxa Tenant >= 7740" trocar "$PROD" RateLimitingOptions__Tenant__PermitLimit "RateLimitingOptions__Tenant__PermitLimit=1000"
caso "mesmo Valkey nos dois" "Valkey do próprio projeto" trocar "$HOMOLOG" CachingOptions__Redis 'CachingOptions__Redis=host.docker.internal:16379,password=vk-producao-1,connectTimeout=5000,syncTimeout=5000,asyncTimeout=5000'
caso "tempo de token diferente" "mesmo AccessTokenMinutes" trocar "$HOMOLOG" JwtOptions__AccessTokenMinutes "JwtOptions__AccessTokenMinutes=45"
caso "tempo de token ausente" "AccessTokenMinutes explícito" trocar "$PROD" JwtOptions__AccessTokenMinutes ""
caso "mesmo projeto nos dois" "nomes de projeto diferentes" trocar "$HOMOLOG" COMPOSE_PROJECT_NAME "COMPOSE_PROJECT_NAME=paf-producao"
caso "subnets sobrepostas" "subnets sem sobreposição" trocar "$HOMOLOG" PAF_SUBNET "PAF_SUBNET=172.31.10.128/25"
caso "mesma porta nos dois" "portas publicadas distintas" trocar "$HOMOLOG" PAF_PORTA_POSTGRES "PAF_PORTA_POSTGRES=15432"
caso "mesmo bucket nos dois" "buckets do R2 diferentes" trocar "$HOMOLOG" Storage__S3__Bucket "Storage__S3__Bucket=paf-producao"
caso "CORS sem x-paf-unidade" "CORS aceita x-paf-unidade" trocar "$PROD" CorsOptions__AllowedHeaders__8 ""
caso "CORS perde cabeçalho do kit" "CORS mantém os cabeçalhos do kit" trocar "$PROD" CorsOptions__AllowedHeaders__2 "CorsOptions__AllowedHeaders__2=x-paf-unidade"
caso "banco sem Command Timeout" "PostgreSQL Command Timeout explícito" trocar "$PROD" DatabaseOptions__ConnectionString 'DatabaseOptions__ConnectionString=Host=postgres;Database=fsh;Username=fsh;Password=${POSTGRES_PASSWORD};Timeout=15'
caso "banco com prazo acima do teto" "PostgreSQL Timeout explícito" trocar "$PROD" DatabaseOptions__ConnectionString 'DatabaseOptions__ConnectionString=Host=postgres;Database=fsh;Username=fsh;Password=${POSTGRES_PASSWORD};Timeout=60;Command Timeout=30'
caso "Valkey sem syncTimeout" "Valkey synctimeout explícito" trocar "$PROD" CachingOptions__Redis 'CachingOptions__Redis=valkey:6379,password=${VALKEY_PASSWORD},connectTimeout=5000,asyncTimeout=5000'
caso "fuso ausente na API" "TZ=America/Sao_Paulo" trocar "$PROD" TZ ""
caso "segredo com cifrão" "compose sem aviso de interpolação" trocar "$PROD" HangfireOptions__Password 'HangfireOptions__Password=abc$def'
caso "arquivo aponta outro ambiente" "env_file é o próprio arquivo do ambiente" trocar "$HOMOLOG" PAF_ARQUIVO_AMBIENTE "PAF_ARQUIVO_AMBIENTE=ambientes/teste-producao.env"
caso "Storage local" "Storage__Provider=s3" trocar "$PROD" Storage__Provider "Storage__Provider=local"
caso "backup no bucket da aplicação" "bucket de backup separado do bucket da aplicação" trocar "$PROD" PAF_BACKUP_BUCKET "PAF_BACKUP_BUCKET=paf-producao"
caso "mesmo bucket de backup nos dois" "buckets de backup diferentes" trocar "$HOMOLOG" PAF_BACKUP_BUCKET "PAF_BACKUP_BUCKET=paf-backup-producao"
caso "backup com a credencial do Storage" "backup sem a credencial do Storage da aplicação" trocar "$PROD" PAF_BACKUP_ACCESS_KEY "PAF_BACKUP_ACCESS_KEY=segredo-teste"
caso "backup sem alerta" "docker compose config falhou" trocar "$PROD" PAF_BACKUP_ALERTA_URL ""
caso "backup com alerta http" "backup com alerta https" trocar "$PROD" PAF_BACKUP_ALERTA_URL "PAF_BACKUP_ALERTA_URL=http://alerta.teste/hook"
caso "prazo de inatividade inválido" "prazo de inatividade (D04)" trocar "$PROD" PAF_INATIVIDADE_MS "PAF_INATIVIDADE_MS=oito-horas"
caso "OTLP ausente" "OTEL_EXPORTER_OTLP_ENDPOINT https" trocar "$PROD" OTEL_EXPORTER_OTLP_ENDPOINT ""
caso "OTLP sem TLS" "OTEL_EXPORTER_OTLP_ENDPOINT https" trocar "$HOMOLOG" OTEL_EXPORTER_OTLP_ENDPOINT "OTEL_EXPORTER_OTLP_ENDPOINT=http://otlp.nr-data.net:4318"
caso "OTLP em gRPC" "OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf" trocar "$PROD" OTEL_EXPORTER_OTLP_PROTOCOL "OTEL_EXPORTER_OTLP_PROTOCOL=grpc"
caso "OTLP sem api-key" "OTEL_EXPORTER_OTLP_HEADERS com api-key" trocar "$PROD" OTEL_EXPORTER_OTLP_HEADERS "OTEL_EXPORTER_OTLP_HEADERS=x-license-key=abc"
caso "OTLP sem prazo" "OTEL_EXPORTER_OTLP_TIMEOUT explícito" trocar "$PROD" OTEL_EXPORTER_OTLP_TIMEOUT ""
caso "mesmo serviço nos dois" "OTEL_SERVICE_NAME diferentes" trocar "$HOMOLOG" OTEL_SERVICE_NAME "OTEL_SERVICE_NAME=paf-api-producao"
caso "sink OTLP do appsettings ligado" "sink OTLP do appsettings desligado" trocar "$PROD" Serilog__WriteTo__1__Name "Serilog__WriteTo__1__Name=OpenTelemetry"
caso "console em texto" "console em JSON" trocar "$HOMOLOG" Serilog__WriteTo__0__Args__formatter ""
caso "tunel sem id" "docker compose config falhou" trocar "$PROD" PAF_TUNEL_ID ""
caso "tunel com id que não é UUID" "PAF_TUNEL_ID é um UUID" trocar "$PROD" PAF_TUNEL_ID "PAF_TUNEL_ID=paf-producao"
caso "tunel sem credenciais" "docker compose config falhou" trocar "$HOMOLOG" PAF_TUNEL_CREDENCIAIS ""
caso "credenciais que não são JSON" "PAF_TUNEL_CREDENCIAIS é o JSON de credenciais" trocar "$PROD" PAF_TUNEL_CREDENCIAIS "PAF_TUNEL_CREDENCIAIS=eyJhIjoiYiJ9"
caso "credenciais sem TunnelSecret" "PAF_TUNEL_CREDENCIAIS é o JSON de credenciais" trocar "$PROD" PAF_TUNEL_CREDENCIAIS 'PAF_TUNEL_CREDENCIAIS={"AccountTag":"conta-teste","TunnelID":"00000000-0000-4000-8000-000000000008"}'
caso "credenciais de outro túnel" "PAF_TUNEL_CREDENCIAIS é do túnel de PAF_TUNEL_ID" trocar "$PROD" PAF_TUNEL_CREDENCIAIS 'PAF_TUNEL_CREDENCIAIS={"AccountTag":"conta-teste","TunnelSecret":"c2VncmVkby10ZXN0ZQ==","TunnelID":"00000000-0000-4000-8000-000000000011"}'
caso "mesmo túnel nos dois" "túneis diferentes" trocar "$HOMOLOG" PAF_TUNEL_ID "PAF_TUNEL_ID=00000000-0000-4000-8000-000000000008"
caso "admin sem URL" "docker compose config falhou" trocar "$PROD" PAF_ADMIN_URL ""
caso "API em http" "caddy com PAF_API_URL https://<host>" trocar "$HOMOLOG" PAF_API_URL "PAF_API_URL=http://api.homolog.paf.exemplo.com.br"
caso "hub com long polling" "PafHub só em WebSockets" acrescentar "$PROD" "PAF__TempoReal__Transportes=WebSockets, LongPolling"
caso "hub só long polling" "PafHub só em WebSockets" acrescentar "$HOMOLOG" "PAF__TempoReal__Transportes=LongPolling"

# The .exemplo files themselves must never pass: placeholders and the sample key.
cp "$AMBIENTES/producao.env.exemplo" "$PROD"
cp "$AMBIENTES/homologacao.env.exemplo" "$HOMOLOG"
trocar "$PROD" PAF_ARQUIVO_AMBIENTE "PAF_ARQUIVO_AMBIENTE=ambientes/teste-producao.env"
trocar "$HOMOLOG" PAF_ARQUIVO_AMBIENTE "PAF_ARQUIVO_AMBIENTE=ambientes/teste-homologacao.env"
esperar_falha "exemplos sem preencher" "sem valor de exemplo"

echo "=== resultado"
printf '%s\n' "${resultados[@]}"
if [ "$falhas" -eq 0 ]; then echo "VERIFICAR-AMBIENTE-TESTE OK (${#resultados[@]} casos)"; exit 0; fi
echo "VERIFICAR-AMBIENTE-TESTE FALHOU ($falhas de ${#resultados[@]})"
exit 1
