# SmartPLC: backend ASP.NET Core (Marcos 2, 4 e V2)

Substituto do backend Express (`../server.js`), que continua intacto para comparação e rollback.
Marco 2: contratos HTTP, validação, SignalR e testes. Marco 4: histórico em PostgreSQL (Supabase) com política de gravação inteligente. **Sem** fila de comandos em nuvem; **sem** deploy realizado.

## Requisitos
- .NET SDK 10 (`global.json` fixa 10.0.x)

## Executar localmente
```bash
cd backend-dotnet
dotnet run --project src/SmartPlc.Api          # http://localhost:5000  (perfil Development)
```
O dashboard é servido em `/`. O Express original usa a porta 3000, então os dois podem rodar lado a lado.

Enviar uma telemetria de teste:
```bash
curl -X POST localhost:5000/api/telemetria -H "Content-Type: application/json" \
  -d '{"nivel":64.2,"valvula":38.5,"setpoint":60,"modoManual":false,"valvulaManual":0,"kp":2,"ti":15,"td":0}'
```

## Histórico (PostgreSQL / Supabase)

Desligado até existir `ConnectionStrings:SmartPlc` (o restante funciona igual). Para ligar localmente:

```bash
dotnet user-secrets set "ConnectionStrings:SmartPlc" "<string do Supabase ou de um PostgreSQL local>" --project src/SmartPlc.Api
```

Esquema (só com sua autorização no banco remoto): `dotnet ef database update --project src/SmartPlc.Api`. Consultas: `GET /api/historico?de=&ate=&limite=&proximo=` e `GET /api/eventos`. Diagnóstico: `GET /health/ready`.
Detalhes: [docs/modelo-dados.md](../docs/modelo-dados.md), [docs/deploy.md](../docs/deploy.md), [docs/testes.md](../docs/testes.md).

## Testes
```bash
dotnet test                                                    # xUnit (backend-dotnet)
node --test tests/contract/baseline.test.js                    # contrato do Express (na raiz do repo, com `node server.js` rodando)
BASE_URL=http://localhost:5000 node --test tests/contract/baseline.test.js   # mesmo arquivo contra o .NET: as falhas são as correções
```

## Configuração (`appsettings.json` ou variáveis de ambiente com `__`)
| Chave | Padrão | Função |
|---|---|---|
| `Process:NivelBaixo` / `NivelAlto` | 10 / 90 | Limiares dos LEDs |
| `Process:LeituraMin` / `LeituraMax` | 0 / 100 | Faixa aceita para nível e válvula |
| `Telemetry:StaleAfterSeconds` | 5 | Sem telemetria além disso => `plcOnline=false` |
| `Ingest:ApiKey` | vazio | Exigida no cabeçalho `X-Api-Key` do `POST /api/telemetria`. Vazia só é aceita em Development. |
| `Control:Enabled` | **false** | Habilita `POST /api/controle`. Desligado = supervisório somente leitura. |
| `Control:OperatorApiKey` | vazio | Cabeçalho `X-Operator-Key`. Provisório até haver login. Obrigatória fora de Development se o controle estiver ligado. |
| `NodeRed:ComandoUrl` / `TimeoutSeconds` | `http://127.0.0.1:1880/api/comando-clp` / 2 | Destino dos comandos |
| `ConnectionStrings:SmartPlc` | vazio | Liga o histórico (URI `postgresql://…` ou formato Npgsql). Use user-secrets/variável de ambiente. |
| `History:SampleIntervalSeconds` / `MinimumLevelChange` / `MinimumValveChange` | 10 / 0,5 / 2,0 | Política híbrida (pontos de partida, não definitivos) |
| `History:RecordEvents` | true | Registra alarmes, comunicação, SP, modo e PID |
| `History:MigrateOnStartup` | false | Aplica migrations ao iniciar (só se você pedir) |
| `History:Retention:*` | desligado | Plano B da retenção; o caminho preferido é `db/retencao-pg_cron.sql` |
| `Command:ConfirmationSeconds` | 10 | Prazo para o CLP reportar o valor de um comando antes de "expirado" |
| `Lab:Enabled` / `Lab:WriteEnabled` | true / true | Laboratório (exige o histórico) e criação/encerramento de sessões |
| `Integration:ShowAddresses` | true | Mostra os endereços S7 no catálogo de variáveis |
| `Cors:AllowedOrigins` | `[]` | Origens externas liberadas (o dashboard usa mesma origem) |

Fora de Development a aplicação **não inicia** sem `Ingest:ApiKey`.

## Telas (frontend V2)

`#/visao-geral`, `#/controle`, `#/historico`, `#/laboratorio`, `#/integracoes`. Guia: [docs/frontend-v2.md](../docs/frontend-v2.md). Contratos: [docs/contratos-api.md](../docs/contratos-api.md). Laboratório: [docs/laboratorio.md](../docs/laboratorio.md).

## Estrutura
```
src/SmartPlc.Api/
  Contracts/    DTOs de entrada e saída
  Json/         conversores tolerantes (números em texto, booleanos inequívocos)
  Validation/   leitura do corpo + regras de faixa
  Services/     estado do processo, cliente do Node-RED, SignalR, monitor de frescor
  Endpoints/    /health, /api/status, /api/telemetria, /api/controle, hub
  wwwroot/      cópia do dashboard adaptada para SignalR (o original segue em ../public)
tests/SmartPlc.Api.Tests/
```

## Diferenças em relação ao Express
Ver [docs/marco-2-estavel.md](../docs/marco-2-estavel.md).
