// Executa o CÓDIGO REAL dos nós function do fluxo da equipe (e da cópia preparada) num sandbox que imita o Node-RED,
// para provar o mapeamento de tags -> JSON do contrato sem precisar do Node-RED nem do PLCSIM.
// Não substitui o teste com o CLP: as mensagens do S7 aqui são simuladas.
//
// Uso: node --test tests/node-red/flow.test.js

const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const ORIGINAL = JSON.parse(fs.readFileSync('node-red/flows.original.json', 'utf8'));
const COPIA = JSON.parse(fs.readFileSync('node-red/flows.smartplc-dotnet.json', 'utf8'));
const no = (fluxo, id) => fluxo.find((n) => n.id === id);

function contexto(inicial = {}, envVars = {}) {
    const dados = new Map(Object.entries(inicial));
    return {
        flow: { get: (k) => dados.get(k), set: (k, v) => dados.set(k, v) },
        env: { get: (k) => envVars[k] },
        dados
    };
}

function rodar(fluxo, id, msg, ctx) {
    const corpo = no(fluxo, id).func;
    const fn = new vm.Script(`(function(msg, flow, env, node, context, RED){ ${corpo}\n})`).runInThisContext();
    return fn(msg, ctx.flow, ctx.env, { warn() {}, error() {}, log() {} }, {}, {});
}

// O nó "s7 in" coloca o NOME DA VARIÁVEL em msg.topic e o valor em msg.payload.
const LEITURAS = {
    Nivel_Tanque: 55.5678, Valvula_Output: 38.456, setpoint: 60, modo_manual: false,
    kp: 2.5, ti: 15, td: 0.5, valvula_manual: 12
};

function lerTudo(fluxo, ctx, leituras = LEITURAS) {
    let ultima;
    for (const [topic, payload] of Object.entries(leituras)) ultima = rodar(fluxo, '6da3499f7da6984f', { topic, payload }, ctx);
    return ultima.payload;
}

test('COMUNICACAO-GET (original): as 8 tags reais viram os 8 campos do contrato, com os tipos certos', () => {
    const p = lerTudo(ORIGINAL, contexto());
    assert.deepEqual(p, {
        nivel: 55.57, valvula: 38.46, setpoint: 60, modoManual: false,
        valvulaManual: 12, kp: 2.5, ti: 15, td: 0.5
    });
    assert.equal(typeof p.modoManual, 'boolean'); // booleano JSON de verdade, não texto
});

test('COMUNICACAO-GET: valvula_manual não é confundida com Valvula_Output (e vice-versa)', () => {
    const ctx = contexto();
    lerTudo(ORIGINAL, ctx, { ...LEITURAS, valvula_manual: 77, Valvula_Output: 11 });
    assert.equal(ctx.dados.get('valvulaManual'), 77);
    assert.equal(ctx.dados.get('valvula'), 11);
});

test('COMUNICACAO-GET: modo_manual true/1 vira true; false/0 vira false', () => {
    for (const [entrada, esperado] of [[true, true], [1, true], [false, false], [0, false]]) {
        const ctx = contexto();
        const p = lerTudo(ORIGINAL, ctx, { ...LEITURAS, modo_manual: entrada });
        assert.equal(p.modoManual, esperado, `entrada=${entrada}`);
    }
});

test('[ORIGINAL] antes de ler tudo o fluxo já envia zeros e padrões (dado inventado)', () => {
    const ctx = contexto();
    const primeira = rodar(ORIGINAL, '6da3499f7da6984f', { topic: 'Valvula_Output', payload: 30 }, ctx).payload;
    assert.equal(primeira.nivel, 0);       // nunca foi lido
    assert.equal(primeira.kp, 1);          // padrão do código, não do CLP
    assert.equal(primeira.ti, 20);
});

test('[CÓPIA] SMARTPLC-PREPARA bloqueia o envio enquanto faltar alguma variável', () => {
    const ctx = contexto();
    const msg = rodar(ORIGINAL, '6da3499f7da6984f', { topic: 'Valvula_Output', payload: 30 }, ctx);
    assert.equal(rodar(COPIA, 'smartplc0000prepara', msg, ctx), null);
});

test('[CÓPIA] SMARTPLC-PREPARA libera quando tudo foi lido e usa as variáveis de ambiente', () => {
    const ctx = contexto({}, { SMARTPLC_URL: 'http://192.168.0.10:5000/api/telemetria', SMARTPLC_API_KEY: 'chave-de-teste' });
    lerTudo(ORIGINAL, ctx);
    const msg = rodar(COPIA, 'smartplc0000prepara', { payload: { nivel: 1 } }, ctx);
    assert.equal(msg.url, 'http://192.168.0.10:5000/api/telemetria');
    assert.equal(msg.headers['X-Api-Key'], 'chave-de-teste');
    assert.equal(msg.headers['Content-Type'], 'application/json');
});

test('[CÓPIA] sem variáveis de ambiente: destino padrão localhost:5000 e nenhum cabeçalho de chave', () => {
    const ctx = contexto();
    lerTudo(ORIGINAL, ctx);
    const msg = rodar(COPIA, 'smartplc0000prepara', { payload: {} }, ctx);
    assert.equal(msg.url, 'http://localhost:5000/api/telemetria');
    assert.equal('X-Api-Key' in msg.headers, false);
});

test('[CÓPIA] o payload completo cumpre o contrato do backend (campos obrigatórios e faixas)', () => {
    const p = lerTudo(COPIA, contexto());
    for (const campo of ['nivel', 'valvula']) {
        assert.equal(typeof p[campo], 'number');
        assert.ok(p[campo] >= 0 && p[campo] <= 100, campo);
    }
    for (const campo of ['setpoint', 'valvulaManual', 'kp', 'ti', 'td']) assert.equal(typeof p[campo], 'number');
    assert.equal(typeof p.modoManual, 'boolean');
});

test('[CÓPIA] a ligação dos nós é COMUNICACAO-GET -> PREPARA -> http request -> (debug, COMANDOS -> PROCESSA-COMANDO)', () => {
    assert.deepEqual(no(COPIA, '6da3499f7da6984f').wires, [['smartplc0000prepara']]);
    assert.deepEqual(no(COPIA, 'smartplc0000prepara').wires, [['513eb33a9393c996']]);
    assert.deepEqual(no(COPIA, '513eb33a9393c996').wires, [['smartplc0000resposta', 'smartplc0000comandos']]);
    assert.deepEqual(no(COPIA, 'smartplc0000comandos').wires, [['9e7744f7d96b3c0f']]);
    assert.equal(no(COPIA, '513eb33a9393c996').url, '');
});

test('[CÓPIA] o nível da telemetria lê a cada ciclo; o nível do gauge e as demais leituras seguem como no original', () => {
    assert.equal(no(COPIA, 'f8764f0e1679c4c0').diff, false);
    assert.equal(no(ORIGINAL, 'f8764f0e1679c4c0').diff, true);
    assert.equal(no(COPIA, '52d085730e5a39d3').diff, true);
});

test('PROCESSA-COMANDO: um comando do .NET vira escritas nas saídas certas do S7', () => {
    const ctx = contexto();
    const corpo = { setpoint: 70, modoManual: true, valvulaManual: 33, kp: 2, ti: 12, td: 1 };
    const saidas = rodar(ORIGINAL, '9e7744f7d96b3c0f', { payload: corpo, res: { marcador: 'res' } }, ctx);
    assert.equal(saidas.length, 7);
    assert.deepEqual(saidas.slice(0, 6).map((m) => m.payload), [70, true, 33, 2, 12, 1]);
});

test('PROCESSA-COMANDO: campos ausentes não geram escrita', () => {
    const saidas = rodar(ORIGINAL, '9e7744f7d96b3c0f', { payload: { setpoint: 50 }, res: {} }, contexto());
    assert.deepEqual(saidas.slice(0, 6).map((m) => m?.payload ?? null), [50, null, null, null, null, null]);
});

test('[ORIGINAL] a resposta HTTP é uma msg nova, sem msg.res: o nó "http response" não consegue responder', () => {
    const saidas = rodar(ORIGINAL, '9e7744f7d96b3c0f', { payload: { setpoint: 50 }, res: { marcador: 'res' } }, contexto());
    assert.equal(saidas[6].res, undefined);
});

test('[CÓPIA] a resposta HTTP carrega msg.res e status 200', () => {
    const res = { marcador: 'res' };
    const saidas = rodar(COPIA, '9e7744f7d96b3c0f', { payload: { setpoint: 50 }, res }, contexto());
    assert.equal(saidas[6].res, res);
    assert.equal(saidas[6].statusCode, 200);
    assert.deepEqual(saidas[6].payload, { status: 'sucesso' });
    assert.equal(saidas[0].payload, 50); // o setpoint continua sendo escrito
});

test('a cópia mantém os 43 nós do original e acrescenta só 3', () => {
    assert.equal(COPIA.length, ORIGINAL.length + 3);
    const novos = COPIA.filter((n) => !ORIGINAL.some((o) => o.id === n.id)).map((n) => n.id).sort();
    assert.deepEqual(novos, ['smartplc0000comandos', 'smartplc0000prepara', 'smartplc0000resposta']);
});

// ---------- Entrega de comandos por fila (Control__Delivery=queue) ----------

const resposta = (corpo, statusCode = 200) => ({ payload: corpo, statusCode });
const comandos = (...cs) => resposta({ status: 'ok', comandos: cs });

test('[CÓPIA] COMANDOS: sem comandos na resposta da telemetria, nada é emitido', () => {
    const ctx = contexto();
    assert.equal(rodar(COPIA, 'smartplc0000comandos', resposta({ status: 'ok' }), ctx), null);
    assert.equal(rodar(COPIA, 'smartplc0000comandos', comandos(), ctx), null);
    assert.equal(rodar(COPIA, 'smartplc0000comandos', { payload: 'texto', statusCode: 200 }, ctx), null);
    assert.equal(rodar(COPIA, 'smartplc0000comandos', { payload: null, statusCode: 200 }, ctx), null);
});

test('[CÓPIA] COMANDOS: resposta de erro (401, 500) nunca vira comando', () => {
    const msg = comandos({ id: 'a', campos: { setpoint: 70 } });
    for (const codigo of [401, 403, 500, 0]) assert.equal(rodar(COPIA, 'smartplc0000comandos', { ...msg, statusCode: codigo }, contexto()), null);
});

test('[CÓPIA] COMANDOS: cada comando vira uma mensagem para o PROCESSA-COMANDO, na ordem recebida', () => {
    const [saidas] = rodar(COPIA, 'smartplc0000comandos', comandos(
        { id: 'a', campos: { kp: 2 } },
        { id: 'b', campos: { setpoint: 70, modoManual: true } }
    ), contexto());
    assert.deepEqual(saidas.map((m) => m.payload), [{ kp: 2 }, { setpoint: 70, modoManual: true }]);
    assert.deepEqual(saidas.map((m) => m.comandoId), ['a', 'b']);
});

test('[CÓPIA] COMANDOS: campos desconhecidos, fora da faixa ou do tipo errado não chegam ao CLP', () => {
    const [saidas] = rodar(COPIA, 'smartplc0000comandos', comandos(
        { id: 'a', campos: { setpoint: 101 } },                       // fora da faixa
        { id: 'b', campos: { kp: -1, ti: 5 } },                       // só o válido passa
        { id: 'c', campos: { ligarBomba: true, DB1: 5 } },            // desconhecidos
        { id: 'd', campos: { setpoint: 'abc', modoManual: 'true' } }, // tipos errados
        { id: 'e', campos: null },
        { id: 'f' },
        null
    ), contexto());
    assert.deepEqual(saidas.map((m) => m.payload), [{ ti: 5 }]);
});

test('[CÓPIA] COMANDOS -> PROCESSA-COMANDO: um comando da fila escreve nas saídas certas e NÃO gera resposta HTTP', () => {
    const [saidas] = rodar(COPIA, 'smartplc0000comandos', comandos({ id: 'a', campos: { setpoint: 70, valvulaManual: 33, modoManual: true } }), contexto());
    const escritas = rodar(COPIA, '9e7744f7d96b3c0f', saidas[0], contexto());
    assert.equal(escritas.length, 7);
    assert.deepEqual(escritas.slice(0, 6).map((m) => m?.payload ?? null), [70, true, 33, null, null, null]);
    assert.equal(escritas[6], null, 'sem requisição HTTP não há o que responder (evita aviso do nó http response)');
});

test('[CÓPIA] PROCESSA-COMANDO continua respondendo 200 quando o comando veio por HTTP', () => {
    const res = { marcador: 'res' };
    const escritas = rodar(COPIA, '9e7744f7d96b3c0f', { payload: { setpoint: 50 }, res }, contexto());
    assert.equal(escritas[6].res, res);
    assert.equal(escritas[6].statusCode, 200);
});

test('a cópia não contém segredos', () => {
    const texto = JSON.stringify(COPIA);
    assert.doesNotMatch(texto, /"X-Api-Key"\s*:\s*"[^"]+"/);
});
