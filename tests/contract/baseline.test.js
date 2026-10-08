// Testes de caracterização do contrato HTTP do back-end ATUAL (Express).
// Objetivo: registrar o comportamento real (inclusive defeitos) para comparar com o back-end .NET.
// Não altera o código do projeto. Usa apenas o test runner nativo do Node (sem dependências novas).
//
// Uso:   node server.js            (terminal 1)
//        node --test tests/contract/baseline.test.js (terminal 2)
// Outro alvo: BASE_URL=http://localhost:5000 node --test tests/contract/baseline.test.js
//
// Testes marcados [DEFEITO] documentam comportamento INDESEJADO do Express; o back-end .NET
// deve inverter essas asserções (ver docs/auditoria.md).

const { test } = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');

const BASE = process.env.BASE_URL || 'http://localhost:3000';
const post = (path, body) => fetch(BASE + path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
});
const status = async () => (await fetch(BASE + '/api/status')).json();

// Node-RED falso na porta 1880 (única porta que o Express aceita, está fixa no código).
function subirNodeRedFalso(codigo) {
    return new Promise((resolve) => {
        const recebidos = [];
        const srv = http.createServer((req, res) => {
            let corpo = '';
            req.on('data', (c) => (corpo += c));
            req.on('end', () => {
                recebidos.push({ url: req.url, body: JSON.parse(corpo || '{}') });
                res.writeHead(codigo).end('{}');
            });
        });
        srv.on('error', () => resolve(null)); // porta ocupada (Node-RED real rodando): pula
        srv.listen(1880, '127.0.0.1', () => resolve({ srv, recebidos }));
    });
}

test('GET /api/status devolve { telemetria, controle } com os campos do contrato', async () => {
    const s = await status();
    assert.deepEqual(Object.keys(s).sort(), ['controle', 'telemetria']);
    assert.deepEqual(Object.keys(s.telemetria).sort(), ['leds', 'nivel', 'timestamp', 'valvula']);
    assert.deepEqual(Object.keys(s.controle).sort(), ['kp', 'modoManual', 'setpoint', 'td', 'ti', 'valvulaManual']);
});

test('POST /api/telemetria válida atualiza estado, arredonda a 2 casas e calcula LEDs', async () => {
    const r = await post('/api/telemetria', { nivel: 55.5678, valvula: 30, setpoint: 60, modoManual: false, valvulaManual: 0, kp: 2, ti: 15, td: 0 });
    assert.equal(r.status, 200);
    const s = await status();
    assert.equal(s.telemetria.nivel, 55.57);
    assert.deepEqual(s.telemetria.leds, { low: false, normal: true, high: false });
    assert.equal(s.controle.setpoint, 60);
    assert.equal(s.controle.kp, 2);
});

test('LEDs: limites 10 e 90 são "normal" (low < 10, high > 90)', async () => {
    for (const [nivel, esperado] of [[9.99, 'low'], [10, 'normal'], [90, 'normal'], [90.01, 'high']]) {
        await post('/api/telemetria', { nivel, valvula: 0 });
        const { telemetria } = await status();
        assert.equal(telemetria.leds[esperado], true, `nivel=${nivel}`);
    }
});

test('[DEFEITO] telemetria sem "nivel" vira nivel=0 e dispara LED baixo (leitura inventada)', async () => {
    const r = await post('/api/telemetria', { valvula: 5 });
    assert.equal(r.status, 200); // deveria ser 400
    const { telemetria } = await status();
    assert.equal(telemetria.nivel, 0);
    assert.equal(telemetria.leds.low, true);
});

test('[DEFEITO] telemetria com nivel "abc" é aceita como 0 (HTTP 200)', async () => {
    const r = await post('/api/telemetria', { nivel: 'abc', valvula: 'xyz' });
    assert.equal(r.status, 200); // deveria ser 400
});

test('[DEFEITO] modoManual="false" (string) vira true (Boolean("false") === true)', async () => {
    await post('/api/telemetria', { nivel: 50, valvula: 50, modoManual: 'false' });
    assert.equal((await status()).controle.modoManual, true);
    await post('/api/telemetria', { nivel: 50, valvula: 50, modoManual: false }); // restaura
});

test('[DEFEITO] setpoint não numérico vira NaN e sai como null no JSON (quebra app.js: null.toFixed)', async () => {
    await post('/api/telemetria', { nivel: 50, valvula: 50, setpoint: 'abc' });
    assert.equal((await status()).controle.setpoint, null);
    await post('/api/telemetria', { nivel: 50, valvula: 50, setpoint: 50 }); // restaura
});

test('[DEFEITO] telemetria sem alvos fora da faixa: setpoint 9999 e nível 500 são aceitos', async () => {
    await post('/api/telemetria', { nivel: 500, valvula: -20, setpoint: 9999 });
    const s = await status();
    assert.equal(s.telemetria.nivel, 500);
    assert.equal(s.controle.setpoint, 9999);
    await post('/api/telemetria', { nivel: 50, valvula: 50, setpoint: 50 });
});

test('POST /api/controle com Node-RED confirmando (200) devolve ok e encaminha o corpo cru', async () => {
    const nr = await subirNodeRedFalso(200);
    if (!nr) return; // porta 1880 ocupada
    try {
        const r = await post('/api/controle', { setpoint: 42, extra: 'campo-desconhecido' });
        assert.equal(r.status, 200);
        assert.equal(nr.recebidos[0].url, '/api/comando-clp');
        // [DEFEITO] campos desconhecidos são repassados ao Node-RED sem filtro
        assert.equal(nr.recebidos[0].body.extra, 'campo-desconhecido');
    } finally { nr.srv.close(); }
});

test('Node-RED recusa (500): API responde 500', async () => {
    const nr = await subirNodeRedFalso(500);
    if (!nr) return;
    try {
        assert.equal((await post('/api/controle', { setpoint: 33 })).status, 500);
    } finally { nr.srv.close(); }
});

test('[DEFEITO] Node-RED fora do ar: API responde 500 MAS o estado local já foi alterado', async () => {
    // pressupõe porta 1880 livre
    const antes = (await status()).controle.setpoint;
    const alvo = antes === 77 ? 78 : 77;
    const r = await post('/api/controle', { setpoint: alvo });
    if (r.status === 200) return; // havia Node-RED real respondendo; teste não se aplica
    assert.equal(r.status, 500);
    assert.equal((await status()).controle.setpoint, alvo); // estado "otimista": UI mostraria SP que o CLP nunca recebeu
});

test('[DEFEITO] POST /api/controle sem corpo válido de número aceita NaN no estado', async () => {
    await post('/api/controle', { kp: 'abc' });
    assert.equal((await status()).controle.kp, null);
    await post('/api/telemetria', { nivel: 50, valvula: 50, kp: 1 }); // restaura
});
