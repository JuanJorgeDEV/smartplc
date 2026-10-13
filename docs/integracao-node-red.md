# Integração Node-RED ↔ SmartPLC

Estado: **`flows.json` recebido e analisado (12/10/2026).** O que está abaixo veio da leitura do arquivo (`node-red/flows.original.json`, inventário completo em `docs/inventario-flows.md`). **Nada foi observado ao vivo no PLCSIM**: a validação com o CLP depende do teste descrito em `docs/roteiro-teste-node-red.md`.

## 1. Como a integração funciona (confirmado no fluxo)

```
CLP (PLCSIM Advanced, 192.168.0.150:102, rack 0 slot 1)
   │  node-red-contrib-s7 3.1.3, ISO-on-TCP, ciclo de leitura 1000 ms
   ▼
10 nós "s7 in" (diff=true: só emitem quando o valor MUDA)
   ▼
COMUNICACAO-GET (function) ── guarda cada variável em flow context e monta o payload completo
   ▼
GET-valores (http request) ── POST http://localhost:3000/api/telemetria   (Express)

Dashboard ── POST /api/controle ──► backend ──► POST http://127.0.0.1:1880/api/comando-clp
   ▼
http in "POST-VALORES" ─► PROCESSA-COMANDO (function, 7 saídas) ─► 6 nós "s7 out" + "http response"
```

### Mapa de variáveis (tags do PLCSIM)

| Campo JSON | Variável no fluxo | Endereço S7 | Tipo | Leitura | Escrita (comando) |
|---|---|---|---|---|---|
| `nivel` | `Nivel_Tanque` | `DB3,REAL0` | REAL | sim | não |
| `valvula` | `Valvula_Output` | `DB4,REAL0` | REAL | sim | não |
| `setpoint` | `setpoint` | `DB5,REAL0` | REAL | sim | sim |
| `modoManual` | `modo_manual` | `DB5,X8.0` | BOOL | sim | sim |
| `kp` | `kp` | `DB5,REAL10` | REAL | sim | sim |
| `ti` | `ti` | `DB5,REAL14` | REAL | sim | sim |
| `td` | `td` | `DB5,REAL18` | REAL | sim | sim |
| `valvulaManual` | `valvula_manual` | `DB5,REAL22` | REAL | sim | sim |

Escala: 0–100 % (os gauges do Node-RED usam min 0, max 100). Limiares de alarme: 10 e 90 (iguais aos do backend). O fluxo envia sempre **os 8 campos**, números e `modoManual` como booleano JSON.

## 2. Resposta às perguntas pendentes

| # | Pergunta | Resposta (fonte: `flows.json`) |
|---|---|---|
| 1 | Protocolo com o PLCSIM | S7 (ISO-on-TCP, porta 102) via `node-red-contrib-s7` 3.1.3, endpoint "PLCSIM_Advanced" |
| 2 | Intervalo de leitura | `cycletime` = 1000 ms, mas com `diff=true`: **só há mensagem quando o valor muda** |
| 3 | Formato real da telemetria | `{nivel, valvula, setpoint, modoManual, valvulaManual, kp, ti, td}`, todos presentes, 2 casas decimais |
| 4 | Envia os campos obrigatórios? | **Sim**: `nivel` e `valvula` sempre presentes e dentro de 0–100 |
| 5 | `modoManual` | booleano JSON (`true`/`false`) |
| 6 | A telemetria devolve SP/modo/PID lidos do CLP? | **Sim** (`s7 in` de cada variável). Logo a confirmação por releitura (nível A de `comandos-estados.md`) é viável |
| 7 | Resposta de `/api/comando-clp` | `{"status":"sucesso"}` 200, enviada **em paralelo** com as escritas, sem esperar o CLP. Significa "recebido", nunca "confirmado" |
| 8 | Destino atual da telemetria | `http://localhost:3000/api/telemetria` (Express); sem cabeçalho de chave |

## 3. Pontos de atenção encontrados

| # | Achado | Impacto | Tratamento |
|---|---|---|---|
| A | `diff=true` em todos os `s7 in` | Com nível exatamente constante não chega telemetria; o backend marca "CLP offline" após 5 s (o Express/dashboard antigo também alarmava) | Cópia preparada: `diff=false` no `s7 in` de `Nivel_Tanque` que alimenta a telemetria |
| B | `COMUNICACAO-GET` envia **zeros e padrões** (nível 0, kp 1, ti 20) enquanto as variáveis não foram todas lidas | Dado inventado no início: apareceria como "nível baixo" no histórico | Cópia: nó `SMARTPLC-PREPARA` só libera após as 8 leituras |
| C | `PROCESSA-COMANDO` responde com uma msg nova, sem `msg.res` | O nó `http response` não consegue responder; o backend esperaria 2 s e daria erro mesmo com o comando já escrito no CLP. **Hipótese a confirmar** no Debug do Node-RED ("no response object") | Cópia: reaproveita a msg original |
| D | Limites da interface do Node-RED: Kp, Ti, Td entre 0 e 100 | O backend e o dashboard .NET aceitam Kp ≤ 1000 e Ti/Td ≤ 100000 nos comandos | Pendente de decisão: alinhar comandos a 0–100 (a telemetria deve continuar aceitando o que o CLP reportar) |
| E | Os `ui-number-input` têm `passthru` e ficam entre `s7 in` e `s7 out` | Quando o CLP muda um valor, o widget o reescreve no CLP (mesmo valor, inócuo). Pode atropelar uma alteração concorrente | Observação; sem ação agora |
| F | Existe um segundo dashboard (FlowFuse, `/dashboard/page1`) que também escreve no CLP | Dois operadores/painéis podem comandar ao mesmo tempo | Regra operacional: um painel de comando por vez |
| G | O endereço `192.168.0.150` é o adaptador virtual do PLCSIM Advanced **daquele** computador | O fluxo só funciona na máquina de quem tem o PLCSIM | Teste feito pelo colega |

## 4. Ensaio sem o CLP (feito)

`tests/node-red/flow.test.js` executa o **código real** dos nós function (original e cópia) num sandbox com mensagens `s7 in` simuladas (tópico = nome da variável): mapeamento das 8 tags, booleanos, `valvula_manual` × `Valvula_Output`, guarda do `SMARTPLC-PREPARA`, ligação dos nós, escrita de um comando nas 6 saídas, resposta HTTP com e sem `msg.res`. Além disso, o payload produzido por esse código foi enviado ao backend .NET real e aceito (HTTP 200, refletido em `/api/status`). **Isto não substitui o teste com o PLCSIM**: as leituras do S7 são simuladas.

## 5. Resultados dos testes

| Data | Teste | Ambiente | Resultado | Evidência |
|---|---|---|---|---|
| 12/10/2026 | Código dos nós + contrato do backend | simulado (sandbox + .NET local) | **passou** (16 testes) | `tests/node-red/flow.test.js` |
| — | Leitura PLCSIM → dashboard .NET | real | **pendente** (roteiro L1–L6) | |
| — | Comando dashboard → CLP | real | **pendente** (roteiro C1–C4) | |
