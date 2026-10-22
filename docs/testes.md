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

## Estado da última execução (22/10/2026)

| Conjunto | Resultado |
|---|---|
| xUnit completo, com PostgreSQL 17 em contêiner (`smartplc_test`) | **438 aprovados, 0 falhas, 0 ignorados** (inclui login, simulação, fila de comandos, RLS e reinício com sessão válida) |
| Frontend, unitários (`tests/frontend/unit.test.mjs`) | **22 aprovados** |
| Frontend, ponta a ponta em Chrome real (`tests/ui/e2e.test.mjs`, servidor sem login) | **16 aprovados**, com entrega direta e também com entrega por fila |
| Login, ponta a ponta em Chrome real (`tests/ui/auth.e2e.test.mjs`, servidor com login) | **8 aprovados** |
| Fluxo do Node-RED, código dos nós (`tests/node-red/flow.test.js`) | **22 aprovados** |
| Express, contrato (`tests/contract/baseline.test.js`) | 12 aprovados (arquivos do Express não foram alterados) |

### Como rodar o teste de login

```bash
# servidor com login ligado, em outra porta, contra o banco de TESTE (cria o administrador inicial)
Auth__Enabled=true Auth__AdminEmail=admin@teste.local Auth__AdminPassword=<senha-de-teste-forte> \
  Ingest__ApiKey=chave-local ConnectionStrings__SmartPlc="Host=localhost;Port=5433;Username=postgres;Password=teste;Database=smartplc_dev" \
  History__MigrateOnStartup=true dotnet run --project backend-dotnet/src/SmartPlc.Api --urls http://localhost:5100
E2E_EMAIL=admin@teste.local E2E_SENHA=<senha-de-teste-forte> BASE_URL=http://localhost:5100 node --test tests/ui/auth.e2e.test.mjs
```

Para testar a entrega por fila, suba o backend com `Control__Enabled=true Control__Delivery=queue` e o simulador com `node tools/simulador-nodered.js --entrega=fila`.

## Como rodar os testes de interface

Pré-requisitos: backend com `Control__Enabled=true` e `ConnectionStrings__SmartPlc` (PostgreSQL de teste), e telemetria chegando.

```bash
docker run --name smartplc-pg-test -e POSTGRES_PASSWORD=teste -p 127.0.0.1:5433:5432 -d postgres:17
# PowerShell (backend):
$env:ASPNETCORE_ENVIRONMENT="Development"; $env:Control__Enabled="true"; $env:History__MigrateOnStartup="true"
$env:ConnectionStrings__SmartPlc="Host=localhost;Port=5433;Username=postgres;Password=teste;Database=smartplc_dev"
dotnet run --project backend-dotnet/src/SmartPlc.Api --urls http://localhost:5000
node tools/simulador-nodered.js --backend=http://localhost:5000          # fonte de telemetria (NÃO é o PLCSIM)
node --test tests/ui/e2e.test.mjs                                        # cria 1 experimento e envia setpoints ao "CLP"
node --test tests/frontend/unit.test.mjs
```

O teste ponta a ponta envia comandos de setpoint a quem estiver respondendo como CLP: com o simulador é inofensivo; **não o aponte para o PLCSIM/CLP real sem querer**.

## Cobertura nova do Marco V2

| Arquivo | Foco |
|---|---|
| `CommandTrackerTests`, `ControleTests`, `SignalRTests` | Estados do comando, confirmação por releitura (tolerâncias, todos os campos, eco antes do 200, expiração, substituição), nenhum estado otimista, evento `comando_update` |
| `ConfigEndpointTests` | `/api/config` sem segredos nem IPs, catálogo das 8 variáveis com os endereços do fluxo, ocultar endereços |
| `ExperimentAnalyzerTests` | Métricas contra soluções analíticas e todos os casos de "não calcular" |
| `ExperimentEndpointTests` | Iniciar/encerrar/cancelar, uma sessão ativa, valores do servidor, sem telemetria, falha do banco, escrita desligada, recuperação após reinício, ponta a ponta com a política real |
| `PostgresExperimentTests` | Migration, índice único parcial, concorrência (6 criações simultâneas ⇒ 1), ordenação, ponta a ponta no PostgreSQL |
| `StatusEFrescorTests` | Arquivos estáticos e tipos, e que o frontend não embute chaves |
