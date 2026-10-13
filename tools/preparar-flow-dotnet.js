#!/usr/bin/env node
// Gera uma CÓPIA do fluxo do Node-RED preparada para o backend .NET. O original não é alterado.
//
// Uso: node tools/preparar-flow-dotnet.js [entrada] [saida]
//      padrão: node-red/flows.original.json -> node-red/flows.smartplc-dotnet.json
//
// Mudanças (todas pequenas e listadas no fim da execução; detalhes em docs/roteiro-teste-node-red.md):
//   1. SMARTPLC-PREPARA (nó novo): só deixa passar quando as 8 variáveis já foram lidas do CLP
//      (o fluxo original envia zeros para o que ainda não leu) e define destino e chave por variável
//      de ambiente (SMARTPLC_URL, SMARTPLC_API_KEY): nenhum segredo dentro do fluxo.
//   2. "GET-valores" (http request): URL vazia => usa a que o nó acima define.
//   3. s7 in de Nivel_Tanque que alimenta a telemetria: diff=false => lê a cada ciclo (1 s). Sem isso, nível
//      constante = silêncio => o backend marca "CLP offline" mesmo com tudo funcionando.
//   4. PROCESSA-COMANDO: a resposta HTTP reaproveita a msg original (que carrega msg.res). Uma msg nova não tem
//      msg.res e o nó "http response" não consegue responder.
//   5. Nó debug "resposta do SmartPLC": mostra o status HTTP devolvido pelo backend (evidência do teste).

const fs = require('node:fs');

const entrada = process.argv[2] || 'node-red/flows.original.json';
const saida = process.argv[3] || 'node-red/flows.smartplc-dotnet.json';

const fluxo = JSON.parse(fs.readFileSync(entrada, 'utf8'));
const porId = (id) => {
    const no = fluxo.find((n) => n.id === id);
    if (!no) throw new Error(`Nó ${id} não encontrado em ${entrada}. O fluxo mudou? Revise este script.`);
    return no;
};

const ABA = '47cce3c81373f633';
const ID = { comunicacao: '6da3499f7da6984f', requisicao: '513eb33a9393c996', nivelTelemetria: 'f8764f0e1679c4c0', processaComando: '9e7744f7d96b3c0f' };
const mudancas = [];

// 1 + 2) destino configurável e guarda contra leitura incompleta
const comunicacao = porId(ID.comunicacao);
const requisicao = porId(ID.requisicao);
const urlAntiga = requisicao.url;

const prepara = {
    id: 'smartplc0000prepara',
    type: 'function',
    z: ABA,
    name: 'SMARTPLC-PREPARA',
    func: [
        '// Só envia quando o CLP já foi lido por completo (evita mandar zeros de variáveis ainda não lidas)',
        "const chaves = ['nivel','valvula','setpoint','modoManual','valvulaManual','kp','ti','td'];",
        'if (chaves.some(k => flow.get(k) === undefined)) { return null; }',
        '',
        '// Destino e chave vêm de variáveis de ambiente do Node-RED (nada de segredo dentro do fluxo)',
        "msg.url = env.get('SMARTPLC_URL') || 'http://localhost:5000/api/telemetria';",
        "msg.headers = { 'Content-Type': 'application/json' };",
        "const chave = env.get('SMARTPLC_API_KEY');",
        "if (chave) { msg.headers['X-Api-Key'] = chave; }",
        'return msg;'
    ].join('\n'),
    outputs: 1, timeout: 0, noerr: 0, initialize: '', finalize: '', libs: [],
    x: 580, y: 340,
    wires: [[ID.requisicao]]
};
comunicacao.wires = [[prepara.id]];
requisicao.url = '';
fluxo.push(prepara);
mudancas.push(`COMUNICACAO-GET -> SMARTPLC-PREPARA -> GET-valores (URL antiga: ${urlAntiga}; agora vem de SMARTPLC_URL)`);

// 5) evidência: resposta do backend
const resposta = {
    id: 'smartplc0000resposta',
    type: 'debug',
    z: ABA,
    name: 'resposta do SmartPLC (esperado 200)',
    active: true, tosidebar: true, console: false, tostatus: true,
    complete: 'true', targetType: 'full', statusVal: 'statusCode', statusType: 'auto',
    x: 930, y: 280,
    wires: []
};
requisicao.wires = [[resposta.id]];
fluxo.push(resposta);
mudancas.push('GET-valores -> debug "resposta do SmartPLC"');

// 3) leitura a cada ciclo do nível que alimenta a telemetria
const nivel = porId(ID.nivelTelemetria);
if (nivel.type !== 's7 in' || nivel.variable !== 'Nivel_Tanque') throw new Error('Nó de nível inesperado.');
nivel.diff = false;
mudancas.push('s7 in Nivel_Tanque (f8764...): diff true -> false (emite a cada ciclo de 1 s)');

// 4) resposta HTTP com msg.res
const processa = porId(ID.processaComando);
const trechoAntigo = 'let msgHttpResponse = { payload: { status: "sucesso" }, statusCode: 200 };';
if (!processa.func.includes(trechoAntigo)) throw new Error('Trecho esperado não encontrado em PROCESSA-COMANDO.');
processa.func = processa.func.replace(
    trechoAntigo,
    '// Reaproveita a msg original: só ela carrega msg.res, necessário ao nó "http response"\n' +
    'msg.payload = { status: "sucesso" };\nmsg.statusCode = 200;\nlet msgHttpResponse = msg;');
mudancas.push('PROCESSA-COMANDO: resposta HTTP reaproveita a msg original (msg.res)');

fs.writeFileSync(saida, JSON.stringify(fluxo, null, 4) + '\n');
console.log(`Gerado ${saida} (${fluxo.length} nós; original tinha ${fluxo.length - 2}).`);
mudancas.forEach((m, i) => console.log(` ${i + 1}. ${m}`));
