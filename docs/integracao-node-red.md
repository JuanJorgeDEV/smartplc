# Integração Node-RED ↔ SmartPLC

Estado: **Marco 3 em andamento — aguardando `flows.json`.** Este documento separa o que está confirmado no código do que ainda depende do Node-RED/CLP reais. Nada abaixo da seção "Confirmado" foi observado no ambiente industrial.

## 1. Confirmado (lendo o código do projeto)

Contrato que o backend espera (idêntico no Express e no .NET):

| Sentido | Chamada | Corpo |
|---|---|---|
| CLP → Node-RED → backend | `POST /api/telemetria` | `nivel`*, `valvula`*, `setpoint`, `modoManual`, `valvulaManual`, `kp`, `ti`, `td` (* obrigatórios no .NET) |
| backend → Node-RED | `POST http://127.0.0.1:1880/api/comando-clp` | subconjunto de `setpoint`, `modoManual`, `valvulaManual`, `kp`, `ti`, `td` |
| dashboard ← backend | SignalR `telemetria_update` | `{ telemetria, controle }` |

Leitura e escrita acontecem no Node-RED; o backend nunca fala com o CLP. A lógica de controle (PID, intertravamentos) fica no CLP.

## 2. Desconhecido — precisa do ambiente real

| # | Informação | Por que importa | Como obter |
|---|---|---|---|
| 1 | `flows.json` do Node-RED (menu ☰ → Exportar → Todos os fluxos → JSON). Credenciais ficam em `flows_cred.json`; **não enviar**. | Protocolo, tags, intervalo, formato real | Exportar do editor do Node-RED |
| 2 | Lista de nós instalados (menu ☰ → Gerenciar paleta) | Identifica o nó de comunicação (S7, OPC UA, Snap7…) | Print ou texto |
| 3 | Mapeamento das tags no TIA Portal (DB, offsets, tipos, escala de engenharia) | O nome da variável no JSON ≠ endereço no CLP; precisa de unidade e escala | Tabela de variáveis do projeto TIA |
| 4 | Como o PLCSIM é acessado (PLCSIM V1x/Advanced, IP/adaptador, "NetToPLCsim", S7-1200/1500) | Determina se dá para repetir o teste em outra máquina | Config. do PLCSIM e do nó de comunicação |
| 5 | O que o endpoint `/api/comando-clp` do Node-RED responde, **e quando** (antes ou depois de escrever no CLP) | Decide o que "Recebido" e "Encaminhado" significam | Fluxo + teste |
| 6 | Se o fluxo devolve `setpoint`, `modoManual`, `valvulaManual`, `kp`, `ti`, `td` lidos do CLP na telemetria | Habilita a confirmação por releitura (ver `comandos-estados.md`) | Fluxo + payload real |
| 7 | Tipo enviado em `modoManual` (bool, 0/1, texto) | O backend aceita bool e 0/1; rejeita texto ambíguo | Payload real |
| 8 | Escala de `nivel` e `valvula` (0–100 %? valor bruto do conversor?) | Backend rejeita fora de 0–100 | TIA + payload real |
| 9 | Versão do Node-RED, SO e se roda como serviço | Reprodução da demonstração | Console |

## 3. Procedimento quando o `flows.json` chegar

```bash
node tools/analisar-flows.js caminho/flows.json > docs/inventario-flows.md
```

A ferramenta (somente leitura) lista abas, tipos de nó (revela o protocolo), nós de configuração com segredos omitidos, gatilhos e intervalos, endpoints HTTP, código das funções, mapeamentos e confere os campos do contrato (`nivel`, `valvula` etc.). Depois:

1. **Tabela de tags** (preencher aqui, com base no inventário e no TIA):

   | Campo JSON | Variável CLP (DB/offset) | Tipo | Unidade/escala | Sentido (R/W) | Nó Node-RED |
   |---|---|---|---|---|---|
   | _a preencher_ | | | | | |

2. **Validação do caminho de leitura** CLP → Node-RED → .NET → Dashboard:
   - subir PLCSIM + CLP em RUN, Node-RED e o backend .NET (`Ingest:ApiKey` vazia só em Development);
   - apontar o `http request` de telemetria para `http://localhost:5000/api/telemetria` **numa cópia do fluxo** (o fluxo atual não é alterado sem sua autorização);
   - evidências: log do backend (nenhuma linha "Telemetria rejeitada"), `GET /api/status` com `plcOnline:true`, captura do dashboard, variação de PV ao mudar a simulação no TIA/PLCSIM.
3. **Validação do caminho de comando**, sem alterar a lógica do CLP, com `Control:Enabled=true` só em Development:
   - enviar um setpoint pelo dashboard e registrar em ordem: log do backend, debug do Node-RED (`msg` recebida), valor no CLP (variável observada no TIA/PLCSIM) e telemetria seguinte;
   - repetir com Node-RED parado (esperado 504/502 e estado intacto).

## 4. Ensaio sem o ambiente real

`tools/simulador-nodered.js` imita o Node-RED (envia telemetria e recebe `/api/comando-clp`) com um tanque simulado. **Não é evidência do CLP**: serve para ensaiar o backend e o dashboard.

```bash
node tools/simulador-nodered.js --backend=http://localhost:5000      # --reflete=nao, --responde=erro|mudo para casos de falha
```

## 5. Resultados dos testes

| Data | Teste | Ambiente | Resultado | Evidência |
|---|---|---|---|---|
| — | Leitura PLCSIM → dashboard .NET | real | **pendente** (falta `flows.json`) | |
| — | Comando dashboard → CLP | real | **pendente** | |
