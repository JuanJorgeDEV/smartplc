#!/usr/bin/env node
// Captura as telas do SmartPLC V2 com o Chrome em modo headless, em TEMPO REAL (protocolo CDP), página inteira.
// Uso: node tools/capturar-telas.js [urlBase=http://localhost:5000] [filtro-de-nome | =nome-exato] [segundosDeEspera=10]
// Requer o backend rodando. Para a Visão Geral ter dados, telemetria precisa estar chegando (Node-RED ou o simulador).
// Saída: docs/evidencias/v2/*.png. As imagens NÃO são evidência de integração com o PLCSIM: dependem da fonte da telemetria.

const { spawn } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const CHROME = [
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'
].find((p) => fs.existsSync(p));
if (!CHROME) { console.error('Chrome/Edge não encontrado.'); process.exit(1); }

const base = process.argv[2] || 'http://localhost:5000';
const filtro = process.argv[3];
const espera = Number(process.argv[4] || 10) * 1000;
const saida = path.resolve('docs/evidencias/v2');
fs.mkdirSync(saida, { recursive: true });

// [arquivo, rota, largura, escala, tema]
const ALVOS = [
    ['desktop-1440-visao-geral-escuro', 'visao-geral', 1440, 1, 'dark'],
    ['desktop-1440-visao-geral-claro', 'visao-geral', 1440, 1, 'light'],
    ['desktop-1440-controle', 'controle', 1440, 1, 'dark'],
    ['desktop-1440-historico', 'historico', 1440, 1, 'dark'],
    ['desktop-1440-laboratorio', 'laboratorio', 1440, 1, 'dark'],
    ['desktop-1440-integracoes', 'integracoes', 1440, 1, 'dark'],
    ['tablet-768-visao-geral', 'visao-geral', 768, 1, 'dark'],
    ['tablet-768-laboratorio', 'laboratorio', 768, 1, 'dark'],
    ['mobile-390-visao-geral', 'visao-geral', 390, 2, 'dark'],
    ['mobile-390-controle', 'controle', 390, 2, 'dark'],
    ['mobile-390-historico', 'historico', 390, 2, 'dark'],
    ['mobile-390-laboratorio', 'laboratorio', 390, 2, 'dark'],
    ['mobile-390-integracoes', 'integracoes', 390, 2, 'dark'],
    ['mobile-360-visao-geral', 'visao-geral', 360, 2, 'light'],
    // Laboratório em simulação (rodam em sequência: preparar -> ativa -> revisão; exigem o banco ligado)
    ['desktop-1440-laboratorio-simulacao-preparar', 'laboratorio', 1440, 1, 'dark', "document.querySelector('input[name=fonte][value=simulacao]').click()"],
    ['desktop-1440-laboratorio-simulacao-ativa', 'laboratorio', 1440, 1, 'dark', "(async () => { const w = (ms) => new Promise((r) => setTimeout(r, ms)); document.querySelector('input[name=fonte][value=simulacao]').click(); document.querySelector('#f-iniciar').requestSubmit(); await w(2500); const v = document.querySelector('#sm-vel'); v.value = '10'; v.dispatchEvent(new Event('change', { bubbles: true })); await w(800); document.querySelector('#sm-sp').value = '65'; document.querySelector('[data-ap=sp]').click(); await w(9000); })()"],
    ['desktop-1440-laboratorio-simulacao-revisao', 'laboratorio', 1440, 1, 'dark', "(async () => { const w = (ms) => new Promise((r) => setTimeout(r, ms)); await w(1500); document.querySelector('#b-encerrar').click(); await w(500); document.querySelector('#dlg-confirmar [data-ok]').click(); await w(2500); })()"],
    // estados degradados (use -- filtro): 'sem-comunicacao' = telemetria parada; 'semdados-*' = servidor sem telemetria, banco e comandos
    ['desktop-1440-sem-comunicacao', 'visao-geral', 1440, 1, 'dark'],
    ['semdados-desktop-1440-visao-geral', 'visao-geral', 1440, 1, 'dark'],
    ['semdados-desktop-1440-controle', 'controle', 1440, 1, 'dark'],
    ['semdados-desktop-1440-historico', 'historico', 1440, 1, 'dark'],
    ['semdados-desktop-1440-laboratorio', 'laboratorio', 1440, 1, 'dark']
];

const dormir = (ms) => new Promise((r) => setTimeout(r, ms));

async function iniciarChrome(porta) {
    const perfil = fs.mkdtempSync(path.join(os.tmpdir(), 'smartplc-chrome-'));
    const proc = spawn(CHROME, ['--headless=new', '--disable-gpu', '--hide-scrollbars', `--remote-debugging-port=${porta}`, `--user-data-dir=${perfil}`, 'about:blank'], { stdio: 'ignore' });
    for (let i = 0; i < 60; i++) {
        try {
            const lista = await (await fetch(`http://127.0.0.1:${porta}/json/list`)).json();
            const pagina = lista.find((t) => t.type === 'page');
            if (pagina) return { proc, perfil, ws: pagina.webSocketDebuggerUrl };
        } catch { /* ainda subindo */ }
        await dormir(250);
    }
    proc.kill();
    throw new Error('Chrome não respondeu no protocolo de depuração.');
}

function conectar(url) {
    return new Promise((resolve, reject) => {
        const ws = new WebSocket(url);
        let id = 0;
        const pendentes = new Map();
        ws.onmessage = (m) => {
            const d = JSON.parse(m.data);
            if (d.id && pendentes.has(d.id)) { const { ok, ko } = pendentes.get(d.id); pendentes.delete(d.id); d.error ? ko(new Error(d.error.message)) : ok(d.result); }
        };
        ws.onerror = reject;
        ws.onopen = () => resolve({
            enviar: (method, params = {}) => new Promise((ok, ko) => { const n = ++id; pendentes.set(n, { ok, ko }); ws.send(JSON.stringify({ id: n, method, params })); }),
            fechar: () => ws.close()
        });
    });
}

(async () => {
    const chrome = await iniciarChrome(9333);
    const cdp = await conectar(chrome.ws);
    try {
        await cdp.enviar('Page.enable');
        for (const [nome, rota, largura, escala, tema, preparo] of ALVOS) {
            if (filtro && !(filtro.startsWith('=') ? nome === filtro.slice(1) : nome.includes(filtro))) continue; // '=nome' = igualdade exata
            const movel = largura < 600;
            await cdp.enviar('Emulation.setDeviceMetricsOverride', { width: largura, height: 900, deviceScaleFactor: escala, mobile: movel });
            await cdp.enviar('Page.navigate', { url: 'about:blank' });
            const rotaFinal = rota === 'laboratorio' && process.env.SESSAO_ID ? `laboratorio?sessao=${process.env.SESSAO_ID}` : rota; // abre a revisão de uma sessão
            await cdp.enviar('Page.navigate', { url: `${base}/?tema=${tema}#/${rotaFinal}` });
            await dormir(espera);
            if (preparo) await cdp.enviar('Runtime.evaluate', { expression: preparo, awaitPromise: true });
            const { result } = await cdp.enviar('Runtime.evaluate', { expression: 'Math.max(document.documentElement.scrollHeight, document.body.scrollHeight)', returnByValue: true });
            const altura = Math.min(Math.ceil(result.value), 6000);
            await cdp.enviar('Emulation.setDeviceMetricsOverride', { width: largura, height: altura, deviceScaleFactor: escala, mobile: movel });
            await dormir(600);
            const img = await cdp.enviar('Page.captureScreenshot', { format: 'png', fromSurface: true });
            const arquivo = path.join(saida, `${nome}.png`);
            fs.writeFileSync(arquivo, Buffer.from(img.data, 'base64'));
            console.log(`OK   ${nome} (${largura}x${altura} @${escala}x, ${tema})`);
        }
    } finally {
        cdp.fechar();
        chrome.proc.kill();
        await dormir(500);
        fs.rmSync(chrome.perfil, { recursive: true, force: true });
    }
})().catch((e) => { console.error('FALHA:', e.message); process.exit(1); });
