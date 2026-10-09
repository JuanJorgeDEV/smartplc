# Modelo de dados e política de histórico (Marco 4)

## Os conceitos, com as variáveis do SmartPLC

| Conceito | No SmartPLC |
|---|---|
| **Telemetria em tempo real** | Cada `POST /api/telemetria` (`nivel`, `valvula`, `setpoint`…) vira o estado em memória e o evento SignalR `telemetria_update`. O dashboard mostra isto. Não depende do banco. |
| **Persistência** | Gravar no PostgreSQL. Só o que a política escolhe — nunca "toda mensagem". |
| **Amostragem** | A regra que escolhe quais leituras viram linha: por tempo (a cada 10 s), por variação (nível ≥ 0,5 ponto) e por evento. |
| **Histórico** | O que ficou gravado: `telemetry_samples` (curvas) e `process_events` (o que aconteceu). Consultado por `/api/historico` e `/api/eventos`. |
| **Agregação** | (futuro, só se o volume pedir) resumir dias antigos em mín/méd/máx por minuto. Hoje o dado bruto expira em 14 dias e não há agregação. |
| **Retenção** | Quanto tempo cada tabela guarda: amostras 14 dias, eventos 365 dias (configurável; ver `db/retencao-pg_cron.sql`). |

Exemplo: o Node-RED envia `nivel` a cada 1 s. O dashboard recebe 3600 atualizações/hora. O banco recebe ~360/hora quando o nível está estável (uma por intervalo de 10 s) e mais durante transitórios (cada vez que o nível se afasta 0,5 ponto da última amostra gravada).

## Caminho dos dados

```
Node-RED --POST--> API (valida) --> estado em memória --> SignalR --> Dashboard      (tempo real, sempre)
                      |
                      +--> HistoryPolicy.Evaluate (em memória, instantâneo)
                              |  decide: amostra? evento?
                              v
                        fila limitada (1000) --> HistoryWriter --> PostgreSQL (Supabase)
```

Se o banco cair, o tempo real não percebe: a fila segura os registros, o gravador tenta de novo (3 vezes, 1 s, 2 s, 4 s) e, esgotadas as tentativas, **declara a perda** (`PerdidosAposFalhas` em `/health/ready` e no log) e a próxima leitura é gravada como ponto de partida. Fila cheia: o registro novo é descartado e contado (`DescartadosFilaCheia`). Reiniciar o processo perde o que estiver na fila (milissegundos em operação normal).

## Regras da política (`History/HistoryPolicy.cs`)

Precedência do motivo da amostra: `Initial` > `Event` > `Change` > `Periodic`.

| Regra | Padrão | Config (`History:`) |
|---|---|---|
| A) intervalo desde a última amostra gravada | 10 s | `SampleIntervalSeconds` |
| B) variação do nível desde a última amostra gravada (≥, nos dois sentidos) | 0,5 ponto | `MinimumLevelChange` |
| B) variação da válvula | 2,0 pontos | `MinimumValveChange` |
| C) eventos | ligado | `RecordEvents` |
| tolerância p/ SP e PID | 0,001 | `ParameterTolerance` |
| fila / tentativas | 1000 / 3 | `QueueCapacity`, `MaxWriteRetries`, `RetryDelayMilliseconds` |

- A comparação é com a **última amostra gravada**, não com a mensagem anterior (senão uma deriva lenta de 0,3 por mensagem nunca gravaria).
- A política só decide **quando chega leitura**. Passar o tempo sem telemetria nunca gera linha: com o CLP offline não há "amostras de preenchimento".
- Eventos só na **transição**: alarme alto/baixo (limiares de `Process:NivelAlto/NivelBaixo`), perda e retorno de comunicação, mudança de SP, de modo e de PID.
- Mudanças de SP, modo e PID são **o que o CLP reportou na telemetria**, não "comando aplicado". Campo ausente na telemetria não é mudança.
- Na primeira leitura após subir o servidor, SP/modo/PID são só referência (sem evento). Um alarme já ativo é registrado. Reiniciar o servidor com o nível alto repete o evento `NivelAlto`.
- PID e válvula manual **não** são repetidos em cada amostra: aparecem em `process_events` quando mudam.
- `AdquiridoEm` só é preenchido se o Node-RED enviar `adquiridoEm` (ISO-8601 com fuso, ou milissegundos Unix). O fluxo atual não envia, então a coluna fica `NULL`; nada é inventado. Com ele, leituras repetidas ou antigas são descartadas.

## Tabelas (migration `20261008120000_InitialCreate`)

**`telemetry_samples`** — curvas. Maior volume.

| Coluna | Tipo | Observação |
|---|---|---|
| `Id` | bigint identity | PK |
| `RecebidoEm` | timestamptz | UTC; hora do servidor. Índice. |
| `AdquiridoEm` | timestamptz null | hora da origem, se informada. Índice único parcial (`WHERE NOT NULL`) = deduplicação |
| `Nivel`, `Valvula` | double precision | valores brutos (a tela arredonda) |
| `Setpoint` | double precision null | |
| `ModoManual` | boolean null | |
| `MotivoGravacao` | smallint | 0 Initial, 1 Periodic, 2 Change, 3 Event |

**`process_events`** — baixo volume, retenção longa. `Id`, `OcorridoEm` (timestamptz, índice), `Tipo` (texto: NivelAlto, NivelAltoNormalizado, NivelBaixo, NivelBaixoNormalizado, ComunicacaoPerdida, ComunicacaoRestabelecida, SetpointAlterado, ModoAlterado, PidAlterado), `Detalhe` (≤500), `ValorAnterior`, `ValorNovo`.

**`control_commands`** — **reservada, sem escrita**. Existe para o Marco de comandos (`docs/comandos-estados.md`): `Id` (uuid), `SolicitadoEm`, `AtualizadoEm`, `Estado`, `Campos` (jsonb), `Detalhe`. Não há ID de equipamento: o sistema tem um único processo (TQ-101) e nenhuma fonte real de identificador.

## Consulta

`GET /api/historico?de=&ate=&limite=&proximo=` e `GET /api/eventos` (mesmos parâmetros).

- `de`/`ate`: ISO-8601 (sem fuso = UTC). Padrão: última hora. Máximo: 72 h por consulta (`History:MaxQueryRangeHours`).
- `limite`: 1–5000, padrão 1000. `proximo`: cursor opaco devolvido quando há mais dados (paginação por `RecebidoEm, Id`, usa o índice, sem `OFFSET`).
- Resposta: `{ de, ate, quantidade, amostras:[{ t, adquiridoEm, nivel, valvula, setpoint, modoManual, motivo }], proximo }`.
- Erros: 400 (parâmetros), 503 (histórico desligado ou banco indisponível; sem detalhes internos).

## Volume (estimativa — ainda não medida)

Linha ≈ 70–80 B de dados + ≈ 50 B de índices ≈ **130 B**. Sem medição real, o limite superior é:

| Cenário | Linhas/dia | Tamanho/dia | 14 dias |
|---|---|---|---|
| Só periódica (10 s), sistema ligado 24 h | 8.640 | ≈ 1,1 MB | ≈ 16 MB |
| Pior caso: 1 linha por segundo | 86.400 | ≈ 11 MB | ≈ 157 MB |

A política híbrida fica entre os dois, conforme a dinâmica da malha. **Medir:** `db/medir-consumo.sql` (tamanho por tabela, gravações por dia/motivo, bytes por linha). Os números só valem depois de algumas horas de telemetria real; ajustar `SampleIntervalSeconds` e `MinimumLevelChange` com base nessa medição.

## Retenção

Preferida: **Supabase Cron** (pg_cron), `db/retencao-pg_cron.sql` — roda no próprio banco, sem depender de o serviço web estar ativo (o Render gratuito hiberna). Plano B: `History:Retention:Enabled=true` ativa uma rotina interna (apaga em lotes de 5000), mas só roda enquanto o serviço estiver acordado. Ambas são opt-in: nada é apagado por padrão.
