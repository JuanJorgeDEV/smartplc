# Marco 2 — versão estável (tag `marco-2-estavel`)

Backend ASP.NET Core 10 equivalente ao Express, com SignalR e testes. O Express original continua na raiz (`server.js`, `public/`) para comparação e rollback.

## Reproduzir do zero

Requisitos: Git, Node.js 24 (apenas para o Express e o teste de contrato), .NET SDK 10.0.x (`backend-dotnet/global.json`).

```bash
git clone <repositório> SmartPlc && cd SmartPlc
git checkout marco-2-estavel

# 1) Testes do backend .NET (102 testes, não precisam de Node-RED nem de rede)
cd backend-dotnet
dotnet test

# 2) Executar o backend .NET  ->  http://localhost:5000
dotnet run --project src/SmartPlc.Api

# 3) Express original  ->  http://localhost:3000  (outro terminal, na raiz)
cd .. && npm install && node server.js

# 4) Contrato do Express (12 testes) e o mesmo arquivo contra o .NET
node --test tests/contract/baseline.test.js
BASE_URL=http://localhost:5000 node --test tests/contract/baseline.test.js
```

No passo 4 com `BASE_URL` apontando para o .NET, **10 testes falham de propósito** (cada um documenta um defeito do Express corrigido, tabela abaixo) e 2 passam.

Resultado esperado: `dotnet test` => 102 aprovados, 0 falhas. Express => 12 aprovados.

## Diferenças Express × ASP.NET Core

| Assunto | Express | ASP.NET Core |
|---|---|---|
| `nivel` ausente ou "abc" | vira 0, HTTP 200, acende nível baixo | 400 com campo e motivo; estado intacto |
| `modoManual: "false"` | vira `true` | vira `false`; texto ambíguo => 400 |
| `setpoint`/`kp` inválido | vira `null`, quebra o dashboard | 400 |
| Valores fora da faixa | aceitos | 400 |
| Campos desconhecidos em `/api/controle` | repassados ao Node-RED | 400 |
| Node-RED indisponível | HTTP 500, mas o estado já mudou | estado intacto; 502 (recusou/inacessível) ou 504 (timeout) |
| Estado inicial | parece leitura recente | `plcOnline:false`, `timestamp:null`, LEDs apagados |
| CLP parou de enviar | só o navegador percebe | servidor informa e avisa os dashboards |
| CORS | `*` | fechado; origens por configuração |
| Telemetria sem chave | aceita | com `Ingest:ApiKey`, exige `X-Api-Key` |
| Comandos | sempre ativos | desligados por padrão (`Control:Enabled=false`) |

Campos aditivos em `telemetria`: `plcOnline`, `idadeSegundos`. A resposta de comando aceito acrescenta `estadoComando:"encaminhado"` e `confirmadoPeloClp:false`.

## O que NÃO foi validado neste marco

Nada com o Node-RED/CLP reais. Os testes usam um Node-RED falso em memória. Ver `docs/integracao-node-red.md`.
