# Roteiro do R2 por ambiente (M11)

Configuração do Cloudflare R2 que a mídia do PAF exige (REQ RF03, RNF09, RNF14; ADR 0007; ARQ seção 11). Nada aqui é edição do kit: é configuração do host e da conta Cloudflare (ADR 0008, parágrafo "Não é edição"). Cada passo que muda configuração real roda só com confirmação de Marcelo, um passo por vez, homologação antes de produção.

Cada linha termina com o estado da última execução: `VERIFICADO <evidência>` ou `SKIPPED <motivo>`. Ao executar em homologação, troque o estado da linha e cite a saída.

## Pré-requisitos

- Conta Cloudflare com R2 e `npx wrangler login` feito por Marcelo (o agente não tem credencial da conta nem do bucket).
- Arquivo do ambiente preenchido a partir de `deploy/paf/ambientes/<ambiente>.env.exemplo`, com `Storage__S3__*` e `PAF_DASHBOARD_URL` literais.
- .NET SDK 10 na máquina que roda as ferramentas abaixo (aplicativos de arquivo único, `dotnet run <arquivo>.cs`).

## Passos

| ID | Passo | Comando ou conferência | Estado |
|---|---|---|---|
| R2-01 | Um bucket da aplicação por ambiente, separado do bucket de backup (ARQ l.367, ADR 0007) | `npx wrangler r2 bucket create paf-homologacao` e `paf-producao`; o backup usa outro bucket (O11). `verificar-ambiente.sh` reprova buckets iguais nos dois ambientes | VERIFICADO em homologação em 19/10/2026: `wrangler r2 bucket list` mostra `paf-homologacao` e `paf-backup-homologacao`. Produção SKIPPED. A barreira de buckets diferentes é do O02 (`verificar-ambiente.mjs`, "cruzado: buckets do R2 diferentes") |
| R2-02 | Token da aplicação com permissão de objeto em leitura e escrita só no bucket do ambiente | Painel R2, "Manage API tokens", "Object Read & Write", escopo no bucket do ambiente; copiar Access Key e Secret para `Storage__S3__AccessKey` e `Storage__S3__SecretKey` | VERIFICADO em homologação em 19/10/2026: token de usuário com leitura e escrita de objeto só em `paf-homologacao`, criado pela API com o token da conta Codefi; `ListObjectsV2` com ele sai 0 em `paf-homologacao` e `AccessDenied` em `paf-backup-homologacao`. Chaves no `.env` de infraestrutura da Codefi, fora do repositório. Produção SKIPPED |
| R2-03 | Storage do kit apontado para o R2 | No arquivo do ambiente: `Storage__Provider=s3`, `Storage__S3__ServiceUrl=https://<account-id>.r2.cloudflarestorage.com`, `Storage__S3__Region=auto`, `Storage__S3__ForcePathStyle=true`, `Storage__S3__Prefix` vazio | SKIPPED: arquivo real do ambiente não existe nesta execução. O verificador do O02 confere `Provider=s3` e `ServiceUrl` com https, mas não confere o domínio `r2.cloudflarestorage.com` (lacuna anotada, não corrigida aqui) |
| R2-04 | Quotas do kit desligadas com valor falso explícito (RNF09) | `QuotaOptions__Enabled=false` no arquivo do ambiente; `verificar-ambiente.sh` reprova ausente ou verdadeiro | SKIPPED no ambiente real (arquivo inexistente). Barreira mecânica do O02 conferida em 19/10/2026, ver "Evidências" |
| R2-05 | Bloqueio de 30 dias só no prefixo `paf-midia/` do bucket da aplicação | `npx wrangler r2 bucket lock set <bucket> --file deploy/paf/r2/lock-midia.json` e `npx wrangler r2 bucket lock list <bucket>`. Se `Storage__S3__Prefix` não for vazio, o prefixo da regra vira `<prefixo>/paf-midia/`, porque é a chave final que o kit grava | VERIFICADO em homologação em 19/10/2026: `wrangler r2 bucket lock list paf-homologacao` mostra `paf-midia-30d`, prefixo `paf-midia/`, 30 dias; passos 2, 5 e 6 de `provar-r2.cs` PASS (409 na sobrescrita, 409 `ObjectLockedByBucketPolicy` no DELETE, DELETE fora do prefixo aceito). Produção SKIPPED |
| R2-06 | CORS do bucket: PUT e GET a partir do dashboard do ambiente | Painel R2, bucket, Settings, CORS Policy, aba JSON, com `deploy/paf/r2/cors-dashboard.json` trocando a origem pelo `PAF_DASHBOARD_URL` do ambiente. Uma origem por ambiente, nunca `*` | VERIFICADO em homologação em 19/10/2026 com origem `http://localhost:5174` até existir domínio de homologação: `wrangler r2 bucket cors list paf-homologacao` mostra GET, HEAD e PUT só dessa origem; passo 7 de `provar-r2.cs` PASS, origem estranha sem `Allow-Origin`. Trocar a origem quando houver domínio. Produção SKIPPED |
| R2-07 | Domínio S3 do R2 liberado na rede de cada cliente | Pedir ao cliente a liberação de `https://<account-id>.r2.cloudflarestorage.com` (443) junto com o endereço da aplicação. Conferência num computador da loja: `curl -s -o /dev/null -w '%{http_code}' https://<account-id>.r2.cloudflarestorage.com/` responde um código HTTP (qualquer um) em vez de expirar | SKIPPED: passo de implantação por cliente, sem cliente nesta fase |
| R2-08 | Prova de aceite em homologação | `dotnet run deploy/paf/r2/provar-r2.cs -- deploy/paf/ambientes/homologacao.env`; código 0 só com todos os passos PASS | VERIFICADO em homologação em 19/10/2026: `RESUMO passos=10 falhas=0`, código 0 (commit 87d187ff). Produção SKIPPED |
| R2-09 | Conferência das mídias no teste de restauração (RNF14) | `PAF_CONFERENCIA_CONEXAO='<conexão do banco restaurado>' dotnet run deploy/paf/r2/conferir-midias.cs -- deploy/paf/ambientes/<ambiente>.env` | VERIFICADO localmente contra PostgreSQL real e RustFS (`r2.test.sh`); contra o R2 real SKIPPED, entra no ensaio de restauração do O11 |

## O que `provar-r2.cs` confere

Contra o bucket do ambiente, com as credenciais do arquivo do ambiente:

1. PUT assinado em `paf-midia/_prova-m11/<execução>.txt` responde 200.
2. Segundo PUT na mesma chave é recusado e o conteúdo lido depois continua o original (o bloqueio impede sobrescrita, conforme a documentação de bucket locks do R2, conferida em 19/10/2026).
3. GET sem assinatura é recusado (400 no R2, 403 no RustFS).
4. O mesmo assinador responde 200 com 1 hora (controle) e é recusado com 8 dias. A assinatura de 8 dias é montada pela própria prova, porque o SDK limita a 7 dias na assinatura AWS4.
5. DELETE da mesma chave é recusado e o objeto continua existindo.
6. PUT e DELETE fora do prefixo (`_prova-m11/<execução>.txt`) são aceitos e o objeto some: os demais arquivos do kit continuam apagáveis.
7. Preflight CORS de PUT e GET a partir de `PAF_DASHBOARD_URL` devolve `Access-Control-Allow-Origin` igual à origem; origem estranha não recebe o cabeçalho.

Resíduo deliberado: o objeto do passo 1 fica 30 dias no prefixo bloqueado (alguns bytes) e a saída imprime a chave. Ele não tem `EnvioMidia`, então a limpeza do M09 não o enxerga; apagar à mão depois dos 30 dias.

## Interface para o teste de restauração (O11)

`conferir-midias.cs` lê `paf."Midias"."ChaveObjeto"` do banco restaurado por SQL direto (todas as empresas daquele banco, sem filtro de tenant), aplica a mesma regra de prefixo do `S3StorageService.NormalizeKey` do kit e faz um HEAD por chave distinta, 8 em paralelo, com prazo de 10 s por HEAD e 30 min no total.

- Entrada: o arquivo `.env` do ambiente (lê `Storage__S3__ServiceUrl`, `Bucket`, `Region`, `Prefix`, `AccessKey`, `SecretKey`) e a variável `PAF_CONFERENCIA_CONEXAO` com a conexão do banco restaurado (variável, para a senha não aparecer na lista de processos).
- Saída: uma linha `AUSENTE <chave>` por objeto ausente, `ERRO <chave>: <tipo>` por falha de conferência e `RESUMO referencias= presentes= ausentes= erros= bucket=`. Nenhum segredo é impresso.
- Código: 0 com tudo presente; 1 com ao menos um ausente; 2 quando a conferência não pôde rodar (arquivo, banco, credencial, prazo ou erro do S3 diferente de 404). O ensaio de restauração reprova com qualquer código diferente de zero.
- Limite: banco de tenant com string de conexão própria não é coberto; rodar uma vez por banco restaurado.

## Requisito registrado para CSP futura

Nenhuma CSP entra no SPA do dashboard sem `https://<account-id>.r2.cloudflarestorage.com` em `media-src` e `img-src`, porque o painel e a tela de cadastro buscam a mídia por URL assinada nesse domínio, e URL assinada não funciona em domínio próprio. Hoje a CSP do kit vale só para a API (`SecurityHeadersMiddleware`) e o nginx do dashboard não põe CSP (riscos do plano da fatia 2).

## Evidências

Execução do M11 em 19/10/2026, local, sem R2:

- VERIFICADO: `dotnet run <arquivo>.cs` com SDK 10.0.402 e `#:package AWSSDK.S3@4.0.103.4` compila e roda fora de `src/` (sem o `Directory.Packages.props` do kit).
- VERIFICADO: `bash deploy/paf/r2/r2.test.sh` -> `R2-TESTE OK`, código 0. PostgreSQL 18 com as migrações reais e o `seed-demo`, RustFS 1.0.0: `conferir-midias.cs` sai 0 com tudo presente, 1 com o objeto apagado (só a chave dele em `AUSENTE`), 1 com `Storage__S3__Prefix=app` (prova que a chave final ganha o prefixo), 2 com segredo errado, 2 sem conexão, 2 com banco inacessível.
- VERIFICADO: mutação em `conferir-midias.cs` que engole o 404 deixa o teste vermelho (`R2-TESTE FALHOU (2)`: prefixo e objeto apagado com código 0); revertida.
- VERIFICADO: `provar-r2.cs` no RustFS: PUT assinado 200, GET sem assinatura 403, assinador de controle 200 com 1 h, 8 dias recusado (400), DELETE fora do prefixo aceito; segundo PUT, DELETE no prefixo e CORS saem FAIL porque o RustFS não tem bloqueio nem CORS de bucket, e a prova sai com código 1. É a prova negativa: sem bloqueio, a prova reprova.
- VERIFICADO: `bash deploy/paf/verificar-ambiente.test.sh` -> `VERIFICAR-AMBIENTE-TESTE OK (27 casos)`, incluindo `quotas ausente` e `quotas true` reprovados.
- SKIPPED: tudo contra o R2 real (R2-01, R2-02, R2-03, R2-05 a R2-08), sem credencial da conta e sem confirmação de Marcelo por passo.

Execução em homologação em 19/10/2026, contra o R2 real (conta Codefi):

- VERIFICADO: `provar-r2.cs` em `paf-homologacao` -> 10 PASS, `RESUMO passos=10 falhas=0`, código 0; resíduo `paf-midia/_prova-m11/07790eb871084a9f991bfe1b60e7e324.txt`, apagar à mão depois de 18/11/2026.
- VERIFICADO: a mesma prova com credencial sem acesso ao bucket sai 2 com `ERRO` em vez de exceção.
- VERIFICADO: token da aplicação lê `paf-homologacao` e recebe `AccessDenied` em `paf-backup-homologacao`.
- SKIPPED: R2-03 e R2-04 no arquivo real do ambiente, que ainda não existe; R2-07 sem cliente; R2-09 contra o R2 real, que entra no ensaio de restauração do O11.
