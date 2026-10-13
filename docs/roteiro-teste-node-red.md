# Roteiro de teste com o Node-RED e o PLCSIM reais

Para quem tem a máquina com TIA Portal, PLCSIM Advanced e Node-RED. Duração estimada: 30–40 min.
O fluxo **original** não é alterado: este roteiro usa uma **cópia** (`node-red/flows.smartplc-dotnet.json`), gerada por script a partir dele.

## O que a cópia muda (e por quê)

| # | Mudança | Motivo |
|---|---|---|
| 1 | Novo nó `SMARTPLC-PREPARA` entre `COMUNICACAO-GET` e `GET-valores` | O fluxo original envia **zeros e padrões** (nível 0, kp 1, ti 20) enquanto as 8 variáveis ainda não foram lidas. O nó só deixa passar quando todas foram lidas. Também define destino e chave por variável de ambiente (nenhum segredo no fluxo). |
| 2 | `GET-valores` (http request) com URL vazia | A URL vem de `SMARTPLC_URL`. Padrão: `http://localhost:5000/api/telemetria`. |
| 3 | `s7 in` de `Nivel_Tanque` (o que alimenta a telemetria): `diff` de true para **false** | Com `diff=true` só há mensagem quando o valor muda. Nível exatamente constante => silêncio => o backend marca "CLP offline" (>5 s) mesmo com tudo funcionando. Agora há uma leitura por ciclo (1 s). |
| 4 | `PROCESSA-COMANDO`: a resposta HTTP reaproveita a `msg` original | A msg nova do original não tem `msg.res`; o nó `http response` não consegue responder (provável aviso "no response object" no Debug). O backend esperaria 2 s e daria erro, mesmo com o comando já escrito no CLP. |
| 5 | Nó debug "resposta do SmartPLC (esperado 200)" | Mostra o status devolvido pelo backend. É a sua evidência. |

Nada muda nos nós `s7 in/out`, na lógica de `COMUNICACAO-GET`, no dashboard do Node-RED nem no CLP.

## Antes de começar

1. **Backup:** no Node-RED, ☰ → Exportar → Todos os fluxos → guarde o arquivo atual.
2. PLCSIM Advanced com o CLP em **RUN** (IP `192.168.0.150`, como no fluxo) e o projeto do TIA Portal online.
3. Decida onde roda o backend .NET:
   - **A) No computador do Juan** (nada a instalar aí). Precisa estar na mesma rede e liberar a porta 5000 no firewall. Use o IP dele em `SMARTPLC_URL`.
   - **B) No seu computador:** instale o .NET SDK 10, clone o repositório e rode `dotnet run --project src/SmartPlc.Api` em `backend-dotnet`. Use `http://localhost:5000/api/telemetria`.

## Passo a passo

1. Defina as variáveis de ambiente **antes** de iniciar o Node-RED (PowerShell):
   ```powershell
   $env:SMARTPLC_URL = "http://<IP-do-backend>:5000/api/telemetria"   # omita para usar localhost:5000
   # $env:SMARTPLC_API_KEY = "<chave>"   # só se o backend tiver Ingest__ApiKey configurada
   node-red
   ```
2. No Node-RED: ☰ → Importar → escolha `flows.smartplc-dotnet.json`. Se perguntar sobre nós existentes, escolha **Substituir** (os IDs são os mesmos do original). **Deploy.**
3. Abra a barra lateral **Debug**. Cada mudança no CLP deve gerar uma linha "resposta do SmartPLC" com `statusCode: 200` e `payload: {status: "ok"}`.
   - `400`: o payload mostra o campo recusado e o motivo (ex.: `nivel` fora de 0–100). **Copie a mensagem inteira.**
   - Nada aparece: confira as variáveis de ambiente e se o backend está acessível (`http://<IP>:5000/health` no navegador).
4. Abra o dashboard do SmartPLC (`http://<IP-do-backend>:5000`). O status deve ir de "Aguardando a primeira leitura do CLP" para "Conectado", com PV, SP e válvula.

### Teste de leitura (obrigatório)

| # | Ação | Resultado esperado | Anotar |
|---|---|---|---|
| L1 | Comparar PV (nível) do SmartPLC com a tabela de observação do TIA Portal | Mesmo valor (±0,01) | print dos dois |
| L2 | Mudar o setpoint pelo **TIA Portal** | SP do SmartPLC acompanha em ~1 s | print |
| L3 | Colocar o nível acima de 90 % e abaixo de 10 % | LED/alarme alto e baixo; evento no painel | print |
| L4 | Deixar o nível estável por >10 s | Continua "Conectado", sem alarme de comunicação | print |
| L5 | Parar o PLCSIM (ou desconectar o adaptador) por >5 s | Alarme "Falha de comunicação" | print |
| L6 | Religar | Alarme normaliza | print |

### Teste de comandos (só depois da leitura OK; opcional nesta rodada)

Exige que o backend alcance o Node-RED: no backend, `NodeRed__ComandoUrl=http://<IP-do-Node-RED>:1880/api/comando-clp` e `Control__Enabled=true` (por padrão o controle vem **desligado**).

| # | Ação | Resultado esperado | Anotar |
|---|---|---|---|
| C1 | Enviar um setpoint pelo SmartPLC | Resposta 200 no dashboard; o SP muda no **TIA Portal** | log do backend + valor no TIA |
| C2 | Alternar para manual e enviar válvula | `modo_manual` e `valvula_manual` mudam no CLP | print do TIA |
| C3 | Enviar Kp/Ti/Td | Valores mudam no CLP (e voltam na telemetria) | print |
| C4 | Parar o Node-RED e enviar um comando | O backend responde 502/504 e **não** altera o SP exibido | print |

## O que trazer de volta

1. Prints da barra Debug com `200` e de uma resposta de erro, se houver.
2. Os prints das tabelas L1–L6 (e C1–C4, se feitos).
3. O arquivo de log do backend (console) do período do teste.
4. Qualquer aviso no Debug do Node-RED, principalmente "no response object".
5. Versão do Node-RED e do Windows.

## Se algo der errado

Restaure o backup do passo "Antes de começar". O fluxo original continua intacto em `node-red/flows.original.json` e funciona com o backend Express (porta 3000) como sempre.
