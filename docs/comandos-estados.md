# Proposta: estados de um comando (enviado × recebido × confirmado)

Problema: hoje "HTTP 200" do Node-RED é tudo que o backend sabe. Isso prova que **alguém recebeu** o pedido, não que o CLP o executou. A proposta distingue o que cada evidência prova.

## Estados

| Estado | Significa | Evidência necessária |
|---|---|---|
| `solicitado` | Backend validou e registrou o pedido | Validação OK (id gerado pelo backend) |
| `recebido` | O Node-RED aceitou o pedido | Resposta 2xx do `/api/comando-clp` |
| `encaminhado` | O Node-RED escreveu no CLP | Resposta do fluxo **depois** da escrita (precisa do fluxo; ver nível B) |
| `confirmado` | O CLP passou a reportar o valor pedido | Telemetria seguinte traz o valor dentro da tolerância |
| `rejeitado` | Recusado antes ou depois | 400 de validação, 4xx/5xx do Node-RED, ou o CLP manteve outro valor |
| `expirado` | Nada confirmou no prazo | Sem eco em `Comando:ConfirmacaoSegundos` (padrão 10 s) |
| `substituido` | Outro comando para o mesmo campo chegou antes da confirmação | Novo pedido do mesmo campo |

## Níveis de implementação (de acordo com o que o fluxo permitir)

**Nível A — sem alterar o fluxo.** `recebido` = 2xx. `confirmado` = releitura: o backend compara o valor da telemetria com o pedido.
Exige que a telemetria traga `setpoint`, `modoManual`, `valvulaManual`, `kp`, `ti`, `td` **lidos do CLP** (pendência nº 6 em `integracao-node-red.md`). Sem isso só existe `recebido`, e o dashboard deve dizer apenas "enviado ao Node-RED".

**Nível B — alteração mínima e aprovada no fluxo.** O corpo enviado passa a conter `comandoId`; o Node-RED responde `{ "comandoId": "...", "escrito": true }` depois de escrever; a telemetria devolve `ultimoComandoId`. Distingue `recebido` de `encaminhado` e dá confirmação inequívoca, inclusive de valores iguais ao anterior.

## Regras da releitura (nível A)

- `setpoint`, `valvulaManual`: tolerância 0,05 (a mesma usada em `marcarDirty` no frontend).
- `kp`, `ti`, `td`: tolerância 0,001. `modoManual`: igualdade exata.
- `valvulaManual` só vale como confirmada com `modoManual=true` no eco.
- Um comando com vários campos só é `confirmado` quando **todos** batem.
- Telemetria antiga (anterior ao envio do comando) não confirma nada: comparar o instante de recebimento.

## Mudanças de API (compatíveis)

- `POST /api/controle` passa a devolver `comandoId` e `estadoComando` (já devolve `estadoComando`/`confirmadoPeloClp`).
- `GET /api/comandos/{id}` consulta o estado; evento SignalR `comando_update` o empurra ao dashboard.
- Frontend: o aviso "Setpoint enviado" vira "enviado ao Node-RED" e depois "confirmado pelo CLP" ou "não confirmado".
- Marco 4: a tabela `comando` guarda estado e carimbos de tempo de cada transição.

## Decisão pendente

Escolher A ou B depende das respostas 5 e 6 de `integracao-node-red.md`. Nada disto foi implementado.

## Atualização (12/10/2026): o que o `flows.json` real permite

- O `s7 in` de `setpoint`, `modo_manual`, `kp`, `ti`, `td` e `valvula_manual` alimenta a telemetria: **o nível A é viável sem alterar o fluxo** (confirmação por releitura).
- O `/api/comando-clp` responde `{"status":"sucesso"}` em paralelo com as escritas `s7 out`, sem esperar o CLP. Portanto o 200 do Node-RED prova apenas **recebido** (e, no máximo, "despachado para o nó S7"), nunca **confirmado**. `encaminhado` de verdade (escrita concluída) exigiria o nível B.
- Há um defeito provável na resposta HTTP do fluxo original (msg nova sem `msg.res`). A cópia `node-red/flows.smartplc-dotnet.json` o corrige; detalhes em `docs/integracao-node-red.md`.
- Recomendação: implementar o nível A (estados `solicitado → recebido → confirmado | expirado`) e adiar o B.
