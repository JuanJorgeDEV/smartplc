// Testes unitários do frontend (módulos puros do wwwroot), sem navegador: node --test tests/frontend
// Cobrem o que decide o que o usuário vê: formatação sem "0" falso, estados de comunicação, alarmes, buffer de amostras,
// preferências de apresentação e glossário.

import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const raiz = path.resolve('backend-dotnet/src/SmartPlc.Api/wwwroot/js');
const carregar = (arq) => import(pathToFileURL(path.join(raiz, arq)).href);

// ---- Ambiente mínimo de navegador ----
const memoria = new Map();
globalThis.localStorage = {
    getItem: (k) => (memoria.has(k) ? memoria.get(k) : null),
    setItem: (k, v) => memoria.set(k, String(v)),
    removeItem: (k) => memoria.delete(k)
};
globalThis.document = { documentElement: { getAttribute: () => 'dark' }, getElementById: () => null };

const fmt = await carregar('format.js');
const prefsMod = await carregar('prefs.js');
const store = await carregar('store.js');
const glos = await carregar('glossary.js');

const tel = (o = {}) => ({
    telemetria: { nivel: 50, valvula: 30, leds: { low: false, normal: true, high: false }, timestamp: '2026-01-01T12:00:00Z', plcOnline: true, idadeSegundos: 0, ...o.telemetria },
    controle: { setpoint: 60, modoManual: false, valvulaManual: 0, kp: 1, ti: 20, td: 0, ...o.controle }
});

// ---------------- format ----------------
test('num: valor ausente vira traço, nunca zero', () => {
    for (const v of [null, undefined, NaN, 'abc']) assert.equal(fmt.num(v), '—');
    assert.equal(fmt.num(0), '0,0');                       // zero REAL continua zero
    assert.equal(fmt.num(55.567, 2), '55,57');
});

test('duracao e idade', () => {
    assert.equal(fmt.duracao(5), '5 s');
    assert.equal(fmt.duracao(125), '2 min 05 s');
    assert.equal(fmt.duracao(3720), '1 h 02 min');
    assert.equal(fmt.duracao(null), '—');
    assert.equal(fmt.idade(0.4), 'agora');
    assert.equal(fmt.idade(8), 'há 8 s');
    assert.equal(fmt.idade(null), '—');
});

test('esc: neutraliza HTML', () => {
    assert.equal(fmt.esc('<img onerror="x">&\''), '&lt;img onerror=&quot;x&quot;&gt;&amp;&#39;');
    assert.equal(fmt.esc(null), '');
});

test('clamp e csvNum', () => {
    assert.equal(fmt.clamp(120, 0, 100), 100);
    assert.equal(fmt.clamp(-5, 0, 100), 0);
    assert.equal(fmt.csvNum(null), '');
    assert.equal(fmt.csvNum(55.5), '55.5');                // ponto decimal, independente do idioma
});

test('paraInputLocal gera o formato do datetime-local', () => {
    assert.match(fmt.paraInputLocal(Date.UTC(2026, 0, 1, 15, 4)), /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/);
});

// ---------------- prefs (apresentação fixa) ----------------
beforeEach(() => { memoria.clear(); });

test('prefs: padrão destaca PV, SP, MV e modo; PID fica de fora', () => {
    const p = prefsMod.prefs();
    assert.deepEqual(['nivel', 'valvula', 'setpoint', 'modoManual'].map((c) => p.variaveis[c].destaque), [true, true, true, true]);
    assert.deepEqual(['kp', 'ti', 'td', 'valvulaManual'].map((c) => p.variaveis[c].destaque), [false, false, false, false]);
});

test('prefs: a apresentação é fixa e nada é gravado no navegador', () => {
    assert.equal(prefsMod.nomeDe('nivel', 'padrão'), 'padrão');
    assert.equal(prefsMod.casasDe('nivel'), 1);
    assert.equal(prefsMod.casasDe('kp'), 2);
    assert.equal(prefsMod.casasDe('inexistente'), 1);
    assert.equal(memoria.size, 0);
    assert.equal(typeof prefsMod.salvar, 'undefined');
});

// ---------------- store: comunicação, amostras e alarmes ----------------
function reiniciar() {
    store.state.hub = 'conectado';
    store.state.config = { processo: { obsoletoAposSegundos: 5 } };
    store.state.amostras.length = 0;
    store.state.eventos.length = 0;
    store.state.recebidoEm = null;
    store.state.status = tel({ telemetria: { timestamp: null, plcOnline: false, idadeSegundos: null, leds: { low: false, normal: false, high: false } } });
    for (const a of Object.values(store.state.alarmes)) { a.ativo = false; a.ack = true; }
}

test('comunicacao: servidor desconectado > aguardando > ok > sem_clp', () => {
    reiniciar();
    store.state.hub = 'desconectado';
    assert.equal(store.comunicacao(), 'servidor_off');
    store.state.hub = 'reconectando';
    assert.equal(store.comunicacao(), 'servidor_off');
    store.state.hub = 'conectado';
    assert.equal(store.comunicacao(), 'aguardando');       // nunca houve leitura
    store.setStatus(tel());
    assert.equal(store.comunicacao(), 'ok');
    store.state.recebidoEm = Date.now() - 6000;            // 6 s sem novidades
    assert.equal(store.comunicacao(), 'sem_clp');
});

test('comunicacao: usa a idade informada pelo servidor (leitura velha ao abrir a página)', () => {
    reiniciar();
    store.setStatus(tel({ telemetria: { idadeSegundos: 30, plcOnline: false } }));
    assert.equal(store.comunicacao(), 'sem_clp');
    assert.ok(store.idadeAtualSeg() >= 30);
});

test('idadeAtualSeg: nula antes da primeira leitura', () => {
    reiniciar();
    assert.equal(store.idadeAtualSeg(), null);
});

test('amostras: só leituras reais e novas entram no buffer', () => {
    reiniciar();
    store.setStatus(tel({ telemetria: { timestamp: 'T1' } }));
    store.setStatus(tel({ telemetria: { timestamp: 'T1' } }));                       // repetida: ignora
    store.setStatus(tel({ telemetria: { timestamp: 'T2' } }));
    store.setStatus(tel({ telemetria: { timestamp: 'T3', plcOnline: false, idadeSegundos: 9 } })); // aviso de CLP offline: ignora
    assert.equal(store.state.amostras.length, 2);
    assert.deepEqual(Object.keys(store.state.amostras[0]).sort(), ['cv', 'modo', 'pv', 'sp', 't']);
});

test('amostras: sem leitura nenhum ponto é inventado', () => {
    reiniciar();
    store.setStatus(tel({ telemetria: { timestamp: null, plcOnline: false, idadeSegundos: null } }));
    assert.equal(store.state.amostras.length, 0);
});

test('amostras: a hora do ponto desconta a idade informada', () => {
    reiniciar();
    const antes = Date.now();
    store.setStatus(tel({ telemetria: { timestamp: 'T9', idadeSegundos: 2 } }));
    assert.ok(store.state.amostras[0].t <= antes - 1900);
});

test('alarmes: nível alto liga uma vez, não repete e normaliza', () => {
    reiniciar();
    store.setStatus(tel({ telemetria: { nivel: 95, timestamp: 'A1', leds: { low: false, normal: false, high: true } } }));
    store.setStatus(tel({ telemetria: { nivel: 96, timestamp: 'A2', leds: { low: false, normal: false, high: true } } }));
    assert.equal(store.state.alarmes.alto.ativo, true);
    assert.equal(store.state.alarmes.alto.ack, false);
    assert.equal(store.state.eventos.filter((e) => e.texto.includes('Nível alto')).length, 1);

    assert.equal(store.reconhecerAlarmes(), true);
    assert.equal(store.state.alarmes.alto.ack, true);
    assert.equal(store.reconhecerAlarmes(), false);

    store.setStatus(tel({ telemetria: { nivel: 80, timestamp: 'A3' } }));
    assert.equal(store.state.alarmes.alto.ativo, false);
    assert.ok(store.state.eventos.some((e) => e.texto === 'Nível alto normalizado'));
});

test('alarmes: sem comunicação válida os LEDs de nível não disparam', () => {
    reiniciar();
    store.setStatus(tel({ telemetria: { nivel: 95, timestamp: 'B1', leds: { low: false, normal: false, high: true } } }));
    store.state.recebidoEm = Date.now() - 20_000;           // telemetria envelheceu
    store.avaliar();
    assert.equal(store.state.alarmes.alto.ativo, false);    // o nível "alto" é de uma leitura velha
    assert.equal(store.state.alarmes.com.ativo, true);
});

test('alarmes: comunicação perdida e restabelecida geram eventos', () => {
    reiniciar();
    store.setStatus(tel({ telemetria: { timestamp: 'C1' } }));
    store.state.recebidoEm = Date.now() - 20_000;
    store.avaliar();
    assert.equal(store.state.alarmes.com.ativo, true);
    store.setStatus(tel({ telemetria: { timestamp: 'C2' } }));
    assert.equal(store.state.alarmes.com.ativo, false);
    assert.ok(store.state.eventos.some((e) => e.texto === 'Comunicação restabelecida'));
});

test('setStatus ignora corpo malformado', () => {
    reiniciar();
    const antes = store.state.status;
    store.setStatus(null);
    store.setStatus({ telemetria: {} });
    assert.equal(store.state.status, antes);
});

test('atualizarComando: insere, atualiza no lugar e ordena do mais novo', () => {
    store.state.comandos.length = 0;
    store.atualizarComando({ id: 'a', estado: 'solicitado', solicitadoEm: '2026-01-01T12:00:00Z', campos: { setpoint: 1 } });
    store.atualizarComando({ id: 'b', estado: 'solicitado', solicitadoEm: '2026-01-01T12:00:05Z', campos: { kp: 2 } });
    store.atualizarComando({ id: 'a', estado: 'confirmado', solicitadoEm: '2026-01-01T12:00:00Z', campos: { setpoint: 1 } });
    assert.deepEqual(store.state.comandos.map((c) => c.id), ['b', 'a']);
    assert.equal(store.state.comandos[1].estado, 'confirmado');
    store.atualizarComando({});                              // sem id: ignora
    assert.equal(store.state.comandos.length, 2);
});

test('eventos e comandos notificam os ouvintes e permitem cancelar a inscrição', () => {
    reiniciar();
    let chamadas = 0;
    const off = store.on('status', () => { chamadas++; });
    store.setStatus(tel());
    off();
    store.setStatus(tel({ telemetria: { timestamp: 'Z9' } }));
    assert.equal(chamadas, 1);
});

// ---------------- glossário ----------------
test('glossário: termos obrigatórios existem e têm explicação curta', () => {
    for (const k of ['clp', 'processo', 'pv', 'sp', 'mv', 'malha', 'pid', 'kp', 'ti', 'td', 'manual', 'automatico', 'sobreelevacao', 'estabilizacao']) {
        assert.ok(glos.TERMOS[k], `falta o termo ${k}`);
        assert.ok(glos.TERMOS[k][1].length < 260, `explicação longa demais: ${k}`);
    }
});

test('glossário: termo() devolve botão focável e termo desconhecido vira texto', () => {
    assert.match(glos.termo('pv'), /<button type="button" class="term" data-termo="pv">PV<\/button>/);
    assert.equal(glos.termo('inexistente', 'x'), 'x');
});

test('glossário: não promete comportamento específico do PID', () => {
    const t = (glos.TERMOS.kp[1] + glos.TERMOS.ti[1] + glos.TERMOS.td[1]).toLowerCase();
    assert.match(t, /em geral|pode/);
});
