#!/usr/bin/env node
// SIMULADOR de Node-RED + CLP para exercitar o backend SEM o ambiente real.
// NÃO é evidência de funcionamento do CLP/PLCSIM: serve para ensaiar o caminho
//   (simulador) -> POST /api/telemetria -> backend -> dashboard
//   dashboard -> POST /api/controle -> backend -> (simulador) POST /api/comando-clp
//
// Uso:  node tools/simulador-nodered.js [--backend=http://localhost:5000] [--intervalo=1000]
//                                     [--reflete=sim|nao] [--responde=ok|erro|mudo]
// Chave de ingestão (se configurada no backend): variável de ambiente INGEST_KEY.
//   --reflete=nao : telemetria NÃO traz setpoint/modo/PID (testa o caso "fluxo sem eco dos parâmetros")
//   --responde=erro|mudo : o endpoint de comando devolve 500 / não responde (testa falhas)

const http = require('node:http');

const arg = (nome, padrao) => (process.argv.find((a) => a.startsWith(`--${nome}=`)) || `=${padrao}`).split('=')[1];
const BACKEND = arg('backend', 'http://localhost:5000');
const INTERVALO = Number(arg('intervalo', 1000));
const REFLETE = arg('reflete', 'sim') === 'sim';
const RESPONDE = arg('responde', 'ok');
const PORTA = 1880;

// "CLP" simulado: tanque de primeira ordem + PI
const clp = { nivel: 30, valvula: 0, setpoint: 50, modoManual: false, valvulaManual: 0, kp: 2, ti: 20, td: 0, integral: 0 };
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);

function passo(dt) {
    const erro = clp.setpoint - clp.nivel;
    if (clp.modoManual) clp.valvula = clp.valvulaManual;
    else {
        clp.integral += clp.ti > 0 ? (erro * dt) / clp.ti : 0;
        clp.integral = Math.max(-50, Math.min(50, clp.integral));
        clp.valvula = Math.max(0, Math.min(100, 50 + clp.kp * erro + clp.kp * clp.integral));
    }
    clp.nivel = Math.max(0, Math.min(100, clp.nivel + dt * (clp.valvula * 0.04 - clp.nivel * 0.02)));
}

function enviarTelemetria() {
    passo(INTERVALO / 1000);
    const corpo = { nivel: +clp.nivel.toFixed(2), valvula: +clp.valvula.toFixed(2) };
    if (REFLETE) Object.assign(corpo, { setpoint: clp.setpoint, modoManual: clp.modoManual, valvulaManual: clp.valvulaManual, kp: clp.kp, ti: clp.ti, td: clp.td });
    const dados = JSON.stringify(corpo);
    const url = new URL('/api/telemetria', BACKEND);
    const headers = { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(dados) };
    if (process.env.INGEST_KEY) headers['X-Api-Key'] = process.env.INGEST_KEY;
    const req = http.request(url, { method: 'POST', headers }, (res) => {
        if (res.statusCode !== 200) log(`telemetria rejeitada: HTTP ${res.statusCode}`);
        res.resume();
    });
    req.on('error', (e) => log('backend inacessível:', e.message));
    req.end(dados);
}

http.createServer((req, res) => {
    if (req.method === 'POST' && req.url === '/api/comando-clp') {
        let corpo = '';
        req.on('data', (c) => (corpo += c));
        req.on('end', () => {
            log('COMANDO RECEBIDO:', corpo);
            if (RESPONDE === 'mudo') return; // nunca responde
            if (RESPONDE === 'erro') { res.writeHead(500).end('{}'); return; }
            try { Object.assign(clp, JSON.parse(corpo)); } catch { /* ignora */ }
            log('  -> aplicado no CLP simulado:', JSON.stringify({ sp: clp.setpoint, manual: clp.modoManual, cv: clp.valvulaManual, kp: clp.kp, ti: clp.ti, td: clp.td }));
            res.writeHead(200, { 'Content-Type': 'application/json' }).end('{"ok":true}');
        });
    } else res.writeHead(404).end();
}).listen(PORTA, '127.0.0.1', () => log(`SIMULADOR ativo: comandos em :${PORTA}, telemetria -> ${BACKEND} a cada ${INTERVALO} ms (reflete=${REFLETE}, responde=${RESPONDE})`));

setInterval(enviarTelemetria, INTERVALO);
