# Testes

## Como executar

```bash
cd backend-dotnet
dotnet test                       # todos; os de PostgreSQL aparecem como "ignorados" sem SMARTPLC_TEST_DB
```

Do repositório raiz (Express e contrato):

```bash
node server.js                                                      # terminal 1
node --test tests/contract/baseline.test.js                         # 12 testes do Express
BASE_URL=http://localhost:5000 node --test tests/contract/baseline.test.js   # mesmo arquivo contra o .NET
```

### Testes com PostgreSQL real

Rodam contra um PostgreSQL de **teste** e **recriam o banco** (`EnsureDeleted` + migrations). Por segurança recusam qualquer banco cujo nome não contenha `test`. Nunca aponte para o banco do Supabase de demonstração.

```bash
docker run --name smartplc-pg-test -e POSTGRES_PASSWORD=teste -p 5433:5432 -d postgres:17
# PowerShell:
$env:SMARTPLC_TEST_DB = "Host=localhost;Port=5433;Username=postgres;Password=teste;Database=smartplc_test"
dotnet test --filter "FullyQualifiedName~PostgresIntegration"
```

(O banco `smartplc_test` é criado pela própria migration. Também serve um PostgreSQL instalado localmente ou um projeto Supabase separado só para testes.)

## O que cada arquivo cobre

| Arquivo | Foco | Precisa de banco? |
|---|---|---|
| `TelemetriaTests`, `ControleTests`, `StatusEFrescorTests`, `SignalRTests`, `NodeRedClientTests` | Marco 2: contrato, validação, frescor, comandos, SignalR | não |
| `HistoryPolicyTests` | regras de gravação: periódica, variação (contra a última gravada, limites inclusivos), eventos só na transição, campo ausente, perda/retorno de comunicação, duplicatas por `AdquiridoEm`, perda de gravação | não |
| `HistoryPipelineTests` | API → política → fila → armazenamento: gravação só após confirmação, inválidos não gravam, falha do banco não derruba o tempo real, retentativas, recuperação, fila cheia, duplicata no banco, `/health/ready` | não (armazenamento em memória) |
| `HistoryQueryTests` | `/api/historico` e `/api/eventos`: período, ordenação, paginação por cursor, limites, 400/503, retenção (plano B) | não |
| `DbConnectionTests` | URI/Npgsql, TLS, pool, senha fora do log | não |
| `PostgresIntegrationTests` | migrations, índices, UTC/timestamptz, atomicidade, duplicata (índice único parcial), paginação real, retenção em lotes, conexão indisponível, ponta a ponta API→PostgreSQL→`/api/historico` | **sim** (`SMARTPLC_TEST_DB`) |

Os testes com armazenamento em memória validam a **lógica**; só os de PostgreSQL validam o comportamento do banco. Os payloads são controlados — nenhum deles é evidência de integração com o PLCSIM.

## Estado da última execução (08/10/2026)

| Conjunto | Resultado |
|---|---|
| xUnit completo (Marco 2 + Marco 4) com PostgreSQL 17.11 em contêiner | **224 aprovados, 0 falhas, 0 ignorados** |
| dos quais, PostgreSQL real (17) | aprovados: migration aplicada do zero, índices, UTC, atomicidade, duplicata, paginação, retenção em lotes, conexão indisponível, ponta a ponta API→PostgreSQL→`/api/historico` |
| Sincronia modelo × migrations (`MigrationSnapshotTests`) | aprovado (equivale a `has-pending-model-changes`) |
| Express, contrato (12) | passou no Marco 3; não foi alterado desde então |

Ambiente da execução: PostgreSQL 17.11 (imagem oficial `postgres:17`, contêiner `smartplc-pg-test`, porta 5433 só em localhost), banco `smartplc_test`. **Não** foi o Supabase.

Observação sobre o Smart App Control: um binário recém-compilado costuma ser bloqueado (`0x800711C7`) por alguns minutos e depois liberado. Se `dotnet test` falhar com "Uma política de Controle de Aplicativo bloqueou este arquivo", repita `dotnet test --no-build` depois de alguns minutos (sem recompilar).

Docker Desktop 4.63 não iniciava (sockets Unix obsoletos que o Windows não deixava apagar). Solução usada: renomear `%LOCALAPPDATA%\Docker\run` e `%LOCALAPPDATA%\docker-secrets-engine` (o Docker recria). As pastas antigas ficaram como `*.stale-20261008` e podem ser apagadas depois de reiniciar o Windows.
