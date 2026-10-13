# Contratos da API (SmartPLC V2)

Todos os corpos são JSON em camelCase. Erros de validação: `400 { status:"erro", mensagem, erros:[{campo, mensagem}] }`.
Leitura é aberta; ingestão exige `X-Api-Key` quando configurada; comandos exigem `Control:Enabled=true` (e `X-Operator-Key` se configurada, chave que o painel **nunca** guarda).

## Telemetria e supervisão (preservados do Marco 2)

| Rota | Descrição |
|---|---|
| `POST /api/telemetria` | Node-RED → backend. Campos: `nivel`*, `valvula`* (0–100), `setpoint`, `modoManual`, `valvulaManual`, `kp`, `ti`, `td`, `adquiridoEm` (opcional). Extras são ignorados. |
| `GET /api/status` | `{ telemetria:{nivel, valvula, leds, timestamp, plcOnline, idadeSegundos}, controle:{setpoint, modoManual, valvulaManual, kp, ti, td} }`. Antes da 1ª leitura: `timestamp:null`, `plcOnline:false`. |
| Hub `/hubs/telemetria` | Evento `telemetria_update` (mesmo formato de `/api/status`) e **`comando_update`** (novo). |
| `GET /health`, `GET /health/ready` | Liveness (não consulta o banco) e contadores do histórico. |

## Configuração pública (novo)

`GET /api/config` — o que é seguro divulgar (nunca chaves, strings de conexão ou IPs):

```json
{ "controle": { "habilitado": true, "exigeChaveDeOperador": false, "confirmacaoSegundos": 10, "limites": { "setpoint":[0,100], "valvulaManual":[0,100], "kp":[0,1000], "ti":[0,100000], "td":[0,100000] } },
  "processo": { "nivelBaixo": 10, "nivelAlto": 90, "obsoletoAposSegundos": 5 },
  "historico": { "habilitado": true, "periodoMaximoHoras": 72, "limitePadrao": 1000, "limiteMaximo": 5000 },
  "laboratorio": { "habilitado": true, "escrita": true },
  "integracao": { "tipo": "Siemens S7", "camada": "Node-RED", "variaveisConfiguradas": 8 } }
```

`GET /api/variaveis` — catálogo das 8 variáveis (`campo`, `identificador`, `nome`, `endereco` ou `null` se `Integration:ShowAddresses=false`, `tipo`, `unidade`, `acesso`, `descricao`). Os endereços vêm do fluxo do Node-RED (`docs/integracao-node-red.md`).

## Comandos com confirmação por releitura (novo comportamento)

`POST /api/controle` com qualquer subconjunto de `setpoint`, `modoManual`, `valvulaManual`, `kp`, `ti`, `td` (campos desconhecidos → 400):

- **200** `{ status:"ok", comandoId, estadoComando:"recebido", confirmadoPeloClp:false, comando:{…}, controle }`. O `controle` devolvido é o que o **CLP reportou**, sem otimismo.
- **502/504** `{ status:"erro", mensagem, comandoId, estadoComando:"falhou" }`; **403** desabilitado; **401** exige chave.

Estados (`GET /api/comandos?limite=`, `GET /api/comandos/{id}`, evento `comando_update`): `solicitado → recebido → confirmado`, ou `falhou`, `expirado` (sem o valor na telemetria em `Command:ConfirmationSeconds`, padrão 10 s), `substituido`. Confirmação: **todos** os campos pedidos iguais ao reportado (SP e CV manual ±0,05; Kp/Ti/Td ±0,001; modo exato) em telemetria recebida **depois** do pedido. Se o CLP já reportava o valor, o detalhe avisa que a releitura não prova escrita. Os comandos recentes ficam na memória (zerados ao reiniciar).

## Histórico (preservado do Marco 4)

`GET /api/historico?de=&ate=&limite=&proximo=` e `GET /api/eventos` (mesmos parâmetros): UTC ISO-8601, padrão 1 h, máx. 72 h, `limite` 1–5000, paginação por cursor. 503 sem banco.

## Laboratório (novo)

Ver `docs/laboratorio.md` (`/api/experimentos`).
