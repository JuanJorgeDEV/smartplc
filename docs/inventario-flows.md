# Inventário do Node-RED

Arquivo: `node-red/flows.original.json` — 43 nós, 1 aba(s).

## Abas
- Fluxo 1

## Tipos de nó (revela o protocolo usado com o CLP)
- `s7 in`: 10
- `s7 out`: 6
- `ui-number-input`: 5
- `function`: 4
- `ui-gauge`: 2
- `ui-switch`: 1
- `ui-template`: 1
- `http request`: 1
- `http in`: 1
- `http response`: 1
- `debug`: 1

## Nós de configuração (conexões com o CLP / brokers) — segredos omitidos
- "PLCSIM_Advanced" `s7 endpoint` (3497b98615982451): `{"transport":"iso-on-tcp","address":"192.168.0.150","port":"102","rack":"0","slot":"1","localtsaphi":"01","localtsaplo":"00","remotetsaphi":"01","remotetsaplo":"00","connmode":"rack-slot","adapter":"","busaddr":2,"cycletime":1000,"timeout":"1500","vartable":[{"addr":"DB3,REAL0","name":"Nivel_Tanque"},{"addr":"DB5,REAL0","name":"setpoint"},{"addr":"DB4,REAL0","name":"Valvula_Output"},{"addr":"DB5,X8.0","name":"modo_manual"},{"addr":"DB5,REAL10","name":"kp"},{"addr":"DB5,REAL14","name":"ti"},{"addr":"DB5,REAL18","name":"td"},{"addr":"DB5,REAL22","name":"valvula_manual"}]}`
- "My Dashboard" `ui-base` (05a28f8b36bd918a): `{"path":"/dashboard","appIcon":"","includeClientData":true,"acceptsClientConfig":["ui-notification","ui-control"],"showPathInSidebar":false,"headerContent":"page","navigationStyle":"default","titleBarStyle":"default","showReconnectNotification":true,"notificationDisplayTime":1,"showDisconnectNotification":true,"allowInstall":false}`
- "Default Theme" `ui-theme` (53077704d561673d): `{"colors":{"surface":"#fcfcfd","primary":"#0d74ce","bgPage":"#f9f9fb","groupBg":"#ffffff","groupOutline":"#d9d9e0"},"sizes":{"density":"default","pagePadding":"12px","groupGap":"12px","groupBorderRadius":"4px","widgetGap":"12px"}}`
- "DASBOARD" `ui-page` (d6a367df9787e9c6): `{"ui":"05a28f8b36bd918a","path":"/page1","icon":"home","layout":"grid","theme":"53077704d561673d","breakpoints":[{"name":"Default","px":"0","cols":"3"},{"name":"Tablet","px":"576","cols":"6"},{"name":"Small Desktop","px":"768","cols":"9"},{"name":"Desktop","px":"880","cols":"12"}],"order":1,"className":"","visible":"true","disabled":"false"}`
- "Monitoramento do Processo" `ui-group` (0ad64ab41359df5d): `{"page":"d6a367df9787e9c6","width":"10","height":"6","order":1,"showTitle":true,"className":"","visible":"true","disabled":"false","groupType":"default"}`
- "spacer" `ui-spacer` (22da24e815351c8b): `{"group":"0ad64ab41359df5d","tooltip":"","order":4,"width":1,"height":1,"className":""}`
- "Comando & Setpoint" `ui-group` (8d3d347cd6e456cb): `{"page":"d6a367df9787e9c6","width":"5","height":"6","order":2,"showTitle":true,"className":"","visible":"true","disabled":"false","groupType":"default"}`
- "Sintonia do PID" `ui-group` (418e7fc0ae53ef3c): `{"page":"d6a367df9787e9c6","width":"5","height":"6","order":3,"showTitle":true,"className":"","visible":"true","disabled":"false","groupType":"default"}`
- `global-config` (6cb917afb25de020): `{"env":[],"modules":{"node-red-contrib-s7":"3.1.3","@flowfuse/node-red-dashboard":"1.31.0"}}`

## Gatilhos e intervalos de leitura
- `s7 in` (52d085730e5a39d3): `{"endpoint":"3497b98615982451","mode":"single","variable":"Nivel_Tanque","diff":true}`
- "Enviar para CLP" `s7 out` (dd0d3d556b53ba00): `{"endpoint":"3497b98615982451","variable":"setpoint"}`
- "OUTPUT-VALVULA" `s7 in` (6463fa2354bc1ae6): `{"endpoint":"3497b98615982451","mode":"single","variable":"Valvula_Output","diff":true}`
- "setpoint" `s7 in` (652ea3c9578009c8): `{"endpoint":"3497b98615982451","mode":"single","variable":"setpoint","diff":true}`
- "Ler Modo" `s7 in` (99a1bee9cb5a1461): `{"endpoint":"3497b98615982451","mode":"single","variable":"modo_manual","diff":true}`
- "Ativar/Desativar" `s7 out` (0a64dd69e18c7b96): `{"endpoint":"3497b98615982451","variable":"modo_manual"}`
- "Enviar para Kp CLP" `s7 out` (85cb3829741500cc): `{"endpoint":"3497b98615982451","variable":"kp"}`
- "Recebe Kp" `s7 in` (180677792a14f8d1): `{"endpoint":"3497b98615982451","mode":"single","variable":"kp","diff":true}`
- "Enviar para Ti CLP" `s7 out` (8283e10c094e387a): `{"endpoint":"3497b98615982451","variable":"ti"}`
- "Recebe Ti" `s7 in` (b10df3cbdb010ee5): `{"endpoint":"3497b98615982451","mode":"single","variable":"ti","diff":true}`
- "Enviar para Td CLP" `s7 out` (b8d44d8a10c0d64c): `{"endpoint":"3497b98615982451","variable":"td"}`
- "Recebe Td" `s7 in` (8be5d5c14d720e3f): `{"endpoint":"3497b98615982451","mode":"single","variable":"td","diff":true}`
- "Enviar para CLP" `s7 out` (f1b6fa98701874f9): `{"endpoint":"3497b98615982451","variable":"valvula_manual"}`
- "Recebe Valvula" `s7 in` (faa76f2c78f71089): `{"endpoint":"3497b98615982451","mode":"single","variable":"valvula_manual","diff":true}`
- `s7 in` (f8764f0e1679c4c0): `{"endpoint":"3497b98615982451","mode":"single","variable":"Nivel_Tanque","diff":true}`
- "sda" `s7 in` (16de2d3ccb082d33): `{"endpoint":"3497b98615982451","mode":"single","variable":"ti","diff":true}`

## Endpoints HTTP
- ENTRADA "POST-VALORES" `http in` (f6081270210824a3): POST `/api/comando-clp`
- SAÍDA "GET-valores" `http request` (513eb33a9393c996): POST `http://localhost:3000/api/telemetria`
- RESPOSTA `http response` (f5415458852fe529): status `(msg.statusCode)`

## Nós function (lógica de conversão/mapeamento)

### "ARREDONDAR" `function` (ba445d0da1c22cc2)
```js
// Converte o valor para texto com apenas 1 casa decimal (ex: "10.3")
msg.payload = Number(msg.payload).toFixed(2);
return msg;
```

### "function 1" `function` (8eedbf3e4c25f3ca)
```js
let nivel = Number(msg.payload);

msg.payload = {
    low: nivel < 10.0,
    normal: nivel >= 10.0 && nivel <= 90.0,
    high: nivel > 90.0
};

return msg;
```

### "COMUNICACAO-GET" `function` (6da3499f7da6984f)
```js
// Captura o tópico ou o nome da variável do S7
let rawTopic = String(msg.topic || msg.variable || '').toLowerCase();

// Substitui sublinhados por espaços para evitar erros de comparação (ex: valvula_manual -> valvula manual)
let topico = rawTopic.replace(/[_]/g, ' ').trim();
let valor = msg.payload;

// 1. Válvula Manual (deve ser verificada ANTES da válvula normal)
if (topico.includes('valvula manual') || topico.includes('valvulamanual') || topico.includes('recebe valvula')) {
    flow.set('valvulaManual', Number(valor) || 0);
}
// 2. Setpoint
else if (topico.includes('setpoint')) {
    flow.set('setpoint', Number(valor) || 0);
} 
// 3. Nível do Tanque
else if (topico.includes('nivel') || topico.includes('tanque')) {
    flow.set('nivel', Number(valor) || 0);
} 
// 4. Válvula de Saída PID
else if (topico.includes('output') || topico.includes('valvula')) {
    flow.set('valvula', Number(valor) || 0);
} 
// 5. Modo Manual / Automático
else if (topico.includes('modo') || topico.includes('manual')) {
    flow.set('modoManual', Boolean(valor === true || valor == 1 || valor === 'true'));
} 
// 6. Parâmetros PID (Kp, Ti, Td)
else if (topico.includes('kp')) {
    flow.set('kp', Number(valor) || 0);
} 
else if (topico.includes('ti')) {
    flow.set('ti', Number(valor) || 0);
} 
else if (topico.includes('td')) {
    flow.set('td', Number(valor) || 0);
}

// Monta o payload final enviado ao servidor
msg.payload = {
    nivel: Number((flow.get('nivel') || 0).toFixed(2)),
    valvula:  …
```

### "PROCESSA-COMANDO" `function` (9e7744f7d96b3c0f)
```js
let p = msg.payload || {};

// 1. Setpoint
let msgSetpoint = (p.setpoint !== undefined && p.setpoint !== null) ? { payload: Number(p.setpoint) } : null;

// 2. Modo Manual (Booleano estrito true/false para a variável BOOL do CLP Siemens)
let msgModo = null;
if (p.modoManual !== undefined && p.modoManual !== null) {
    let eVerdadeiro = (p.modoManual === true || p.modoManual === 1 || p.modoManual === 'true' || p.modoManual === '1');
    msgModo = { payload: eVerdadeiro }; // Envia obrigatoriamente true ou false
}

// 3. Válvula Manual
let msgValvula = (p.valvulaManual !== undefined && p.valvulaManual !== null) ? { payload: Number(p.valvulaManual) } : null;

// 4. Parâmetros PID
let msgKp = (p.kp !== undefined && p.kp !== null) ? { payload: Number(p.kp) } : null;
let msgTi = (p.ti !== undefined && p.ti !== null) ? { payload: Number(p.ti) } : null;
let msgTd = (p.td !== undefined && p.td !== null) ? { payload: Number(p.td) } : null;

// 5. Confirmação HTTP (Garante que o Express receba a resposta imediatamente)
let msgHttpResponse = { payload: { status: "sucesso" }, statusCode: 200 };

return [msgSetpoint, msgModo, msgValvula, msgKp, msgTi, msgTd, msgHttpResponse];
```

## Nós change/switch (mapeamento de campos)

## Verificação contra o contrato do backend
- obrigatório `nivel`: mencionado nos nós function/change
- obrigatório `valvula`: mencionado nos nós function/change
- opcional `setpoint`: mencionado
- opcional `modoManual`: mencionado
- opcional `valvulaManual`: mencionado
- opcional `kp`: mencionado
- opcional `ti`: mencionado
- opcional `td`: mencionado
- nós que chamam `/api/telemetria`: 513eb33a9393c996
- destino configurado em 513eb33a9393c996: `http://localhost:3000/api/telemetria` (confirmar para qual backend aponta)

## Caminhos (origem -> ... -> http request)
- s7 in -> Nivel do Tanque
- OUTPUT-VALVULA -> ARREDONDAR -> Valuvla
- OUTPUT-VALVULA -> COMUNICACAO-GET -> GET-valores
- setpoint -> Configuração do SetPoint -> Enviar para CLP
- setpoint -> COMUNICACAO-GET -> GET-valores
- Ler Modo -> Modo Manual -> Ativar/Desativar
- Ler Modo -> COMUNICACAO-GET -> GET-valores
- Recebe Kp -> Configuração PID -> Enviar para Kp CLP
- Recebe Kp -> COMUNICACAO-GET -> GET-valores
- Recebe Ti -> Configuração PID -> Enviar para Ti CLP
- Recebe Ti -> COMUNICACAO-GET -> GET-valores
- Recebe Td -> Configuração PID -> Enviar para Td CLP
- Recebe Td -> COMUNICACAO-GET -> GET-valores
- Recebe Valvula -> Valor Manual da Valvula -> Enviar para CLP
- Recebe Valvula -> COMUNICACAO-GET -> GET-valores
- s7 in -> function 1 -> ui-template
- s7 in -> COMUNICACAO-GET -> GET-valores
- POST-VALORES -> PROCESSA-COMANDO -> Enviar para CLP
- POST-VALORES -> PROCESSA-COMANDO -> Ativar/Desativar
- POST-VALORES -> PROCESSA-COMANDO -> Enviar para CLP
- POST-VALORES -> PROCESSA-COMANDO -> Enviar para Kp CLP
- POST-VALORES -> PROCESSA-COMANDO -> Enviar para Ti CLP
- POST-VALORES -> PROCESSA-COMANDO -> Enviar para Td CLP
- POST-VALORES -> PROCESSA-COMANDO -> http response
- sda -> debug 1
