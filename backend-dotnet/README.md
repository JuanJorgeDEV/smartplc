# SmartPLC: backend ASP.NET Core (Marco 2)

Substituto do backend Express (`../server.js`), que continua intacto para comparação e rollback.
Escopo deste marco: contratos HTTP, validação, SignalR e testes. **Sem** PostgreSQL, fila de comandos em nuvem ou deploy.

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
| `Cors:AllowedOrigins` | `[]` | Origens externas liberadas (o dashboard usa mesma origem) |

Fora de Development a aplicação **não inicia** sem `Ingest:ApiKey`.

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
