// Testes ponta a ponta do LOGIN, em Chrome real (headless, CDP). Sem dependências npm.
//
// Pré-requisitos (ver docs/testes.md):
//   - backend rodando com login ligado e banco de TESTE: Auth__Enabled=true, ConnectionStrings__SmartPlc, Auth__AdminEmail e Auth__AdminPassword
//     (o administrador inicial); a URL em BASE_URL (padrão http://localhost:5100);
//   - variáveis de ambiente E2E_EMAIL e E2E_SENHA com as credenciais desse administrador.
// Uso:  E2E_EMAIL=... E2E_SENHA=... node --test tests/ui/auth.e2e.test.mjs
// O teste cria usuários com e-mail único (auth-e2e-<hora>@teste.local) e os desativa no fim. Use SOMENTE contra um banco de teste.

import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const BASE = process.env.BASE_URL || 'http://localhost:5100';
const EMAIL = process.env.E2E_EMAIL;
const SENHA = process.env.E2E_SENHA;
const CHROME = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find((p) => fs.existsSync(p));
const dormir = (ms) => new Promise((r) => setTimeout(r, ms));

const sufixo = Date.now().toString(36);
const VIS = { nome: 'Visualizador E2E', email: `auth-e2e-${sufixo}@teste.local`, senha: `Prov-${sufixo}-xK9#vq`, nova: `Nova-${sufixo}-mP4!zt` };

let chrome, perfil, ws, nextId = 0;
const pend = new Map();
const erros = [];

async function iniciar() {
    perfil = fs.mkdtempSync(path.join(os.tmpdir(), 'smartplc-auth-'));
    chrome = spawn(CHROME, ['--headless=new', '--disable-gpu', '--hide-scrollbars', '--remote-debugging-port=9445', `--user-data-dir=${perfil}`, 'about:blank'], { stdio: 'ignore' });
    let url;
    for (let i = 0; i < 80 && !url; i++) {
        try { url = (await (await fetch('http://127.0.0.1:9445/json/list')).json()).find((t) => t.type === 'page')?.webSocketDebuggerUrl; } catch { /* subindo */ }
        if (!url) await dormir(250);
    }
    assert.ok(url, 'Chrome não respondeu ao protocolo de depuração');
    ws = new WebSocket(url);
    await new Promise((ok, ko) => { ws.onopen = ok; ws.onerror = ko; });
    ws.onmessage = (m) => {
        const d = JSON.parse(m.data);
        if (d.id && pend.has(d.id)) { const { ok, ko } = pend.get(d.id); pend.delete(d.id); d.error ? ko(new Error(d.error.message)) : ok(d.result); }
        else if (d.method === 'Runtime.exceptionThrown') erros.push(`exceção: ${d.params.exceptionDetails.exception?.description ?? d.params.exceptionDetails.text}`);
    };
    await cdp('Page.enable'); await cdp('Runtime.enable');
}

const cdp = (method, params = {}) => new Promise((ok, ko) => { const id = ++nextId; pend.set(id, { ok, ko }); ws.send(JSON.stringify({ id, method, params })); });
async function avaliar(expressao) {
    const r = await cdp('Runtime.evaluate', { expression: expressao, awaitPromise: true, returnByValue: true });
    if (r.exceptionDetails) throw new Error(`${r.exceptionDetails.exception?.description ?? r.exceptionDetails.text}\n${expressao}`);
    return r.result.value;
}
async function esperar(expressao, ms = 10000, msg = expressao) {
    const limite = Date.now() + ms;
    let ultimo;
    while (Date.now() < limite) {
        try { ultimo = await avaliar(expressao); if (ultimo) return ultimo; } catch (e) { ultimo = e.message; }
        await dormir(120);
    }
    assert.fail(`Tempo esgotado esperando: ${msg} (último valor: ${JSON.stringify(ultimo)})`);
}
async function abrir(rota = '', { largura = 1440, altura = 900 } = {}) {
    await cdp('Emulation.setDeviceMetricsOverride', { width: largura, height: altura, deviceScaleFactor: 1, mobile: false });
    await cdp('Page.navigate', { url: 'about:blank' });
    await cdp('Page.navigate', { url: `${BASE}/?tema=dark#/${rota}` });
    await esperar(`document.readyState === 'complete'`, 15000, 'página carregada');
}
const clicar = (sel) => avaliar(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); if (!e) throw new Error('não achei ${sel}'); e.click(); return true; })()`);
const digitar = (sel, v) => avaliar(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); if (!e) throw new Error('não achei ${sel}'); e.value = ${JSON.stringify(v)}; e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
const texto = (sel) => avaliar(`document.querySelector(${JSON.stringify(sel)})?.textContent ?? ''`);
const visivel = (sel) => avaliar(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); return !!e && !e.hidden && e.getClientRects().length > 0; })()`);
const confirmarDialogo = async () => { await esperar(`document.getElementById('dlg-confirmar')?.open`, 5000, 'diálogo aberto'); await clicar('#dlg-confirmar [data-ok]'); };

async function entrarNaTela(email, senha) {
    await digitar('#login-email', email);
    await digitar('#login-senha', senha);
    await avaliar(`document.getElementById('f-login').requestSubmit()`);
}

// Cliente da API fora do navegador (para o administrador agir "por fora", como outra pessoa).
async function adminApi() {
    const r = await fetch(`${BASE}/api/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: EMAIL, senha: SENHA }) });
    assert.equal(r.status, 200, 'login do administrador de teste falhou: confira E2E_EMAIL/E2E_SENHA e o servidor');
    const cookie = r.headers.getSetCookie().find((c) => c.startsWith('smartplc.sid=')).split(';')[0];
    return (caminho, opcoes = {}) => fetch(`${BASE}${caminho}`, { ...opcoes, headers: { 'Content-Type': 'application/json', Cookie: cookie, ...(opcoes.headers ?? {}) } });
}

before(async () => {
    assert.ok(CHROME, 'Chrome/Edge não encontrado');
    assert.ok(EMAIL && SENHA, 'Defina E2E_EMAIL e E2E_SENHA (administrador do servidor de teste)');
    const me = await (await fetch(`${BASE}/api/auth/me`).catch(() => ({ json: async () => ({}) }))).json();
    assert.equal(me.autenticacaoAtiva, true, `O servidor em ${BASE} precisa estar com Auth__Enabled=true`);
    await iniciar();
});

after(async () => {
    try {
        const api = await adminApi();
        const lista = (await (await api('/api/usuarios')).json()).usuarios ?? [];
        const alvo = lista.find((u) => u.email === VIS.email);
        if (alvo) await api(`/api/usuarios/${alvo.id}`, { method: 'PATCH', body: JSON.stringify({ ativo: false }) });
    } catch { /* limpeza de cortesia */ }
    try { ws?.close(); } catch { /* ok */ }
    chrome?.kill();
    await dormir(400);
    if (perfil) fs.rmSync(perfil, { recursive: true, force: true });
});

test('sem sessão: só a tela de login aparece e nada do painel é carregado', async () => {
    await abrir();
    await esperar(`!document.getElementById('tela-login').hidden`, 10000, 'tela de login');
    assert.equal(await visivel('.app'), false);
    assert.match(await texto('#login-titulo'), /Entrar/);
    assert.equal(await avaliar(`document.title`), 'Entrar · SmartPLC');
    assert.equal(await avaliar(`document.getElementById('login-senha').type`), 'password');
    assert.equal(await avaliar(`document.getElementById('login-senha').autocomplete`), 'current-password');
    const status = await avaliar(`fetch('/api/status').then((r) => r.status)`);
    assert.equal(status, 401);
});

test('login com senha errada mostra o erro sem revelar nada e limpa a senha', async () => {
    await abrir();
    await esperar(`!document.getElementById('tela-login').hidden`);
    await entrarNaTela(EMAIL, 'senha-errada-qualquer-1');
    await esperar(`document.getElementById('login-erro').textContent.length > 0`, 8000, 'mensagem de erro');
    assert.match(await texto('#login-erro'), /inválidos/);
    assert.equal(await avaliar(`document.getElementById('login-senha').value`), '');
    assert.equal(await visivel('.app'), false);
    const outro = await (async () => { await entrarNaTela('quem-nao-existe@teste.local', 'qualquer-senha-123'); await esperar(`document.getElementById('login-erro').textContent.length > 0`); return texto('#login-erro'); })();
    assert.equal(outro, await texto('#login-erro'));
});

test('login correto abre o painel, mostra quem entrou e não deixa senha nem token no navegador', async () => {
    await abrir();
    await esperar(`!document.getElementById('tela-login').hidden`);
    await entrarNaTela(EMAIL, SENHA);
    await esperar(`document.getElementById('tela-login').hidden && !document.querySelector('.app').hidden`, 10000, 'painel visível');
    await esperar(`document.getElementById('rail-conta') && !document.getElementById('rail-conta').hidden`, 5000, 'bloco do usuário');
    assert.match(await texto('#conta-papel'), /Administrador/i);
    assert.ok((await texto('#conta-nome')).length > 0);

    // o JavaScript não enxerga o cookie de sessão (HttpOnly) e nada sensível vai para o armazenamento local
    assert.equal(await avaliar(`document.cookie.includes('smartplc.sid')`), false);
    const armazenado = await avaliar(`JSON.stringify([...Object.entries(localStorage), ...Object.entries(sessionStorage)])`);
    assert.ok(!armazenado.includes(SENHA), 'a senha não pode ser guardada');
    assert.ok(!/token|senha|chave/i.test(armazenado), `armazenamento suspeito: ${armazenado}`);
    assert.equal(await avaliar(`fetch('/api/status').then((r) => r.status)`), 200);
});

test('administrador cria um visualizador pela tela e ele aparece na lista', async () => {
    await abrir('integracoes');
    await esperar(`!document.querySelector('.app').hidden && !!document.getElementById('f-usr')`, 15000, 'seção de usuários (administrador)');
    await digitar('#nu-nome', VIS.nome);
    await digitar('#nu-email', VIS.email);
    await digitar('#nu-papel', 'visualizador');
    await digitar('#nu-senha', 'fraca');
    await avaliar(`document.getElementById('f-usr').requestSubmit()`);
    await esperar(`document.getElementById('e-usr').textContent.length > 0`, 8000, 'erro de senha fraca');
    assert.match(await texto('#e-usr'), /pelo menos 10|senha/i);

    await digitar('#nu-senha', VIS.senha);
    await avaliar(`document.getElementById('f-usr').requestSubmit()`);
    await esperar(`[...document.querySelectorAll('#tb-usr tr')].some((t) => t.textContent.includes(${JSON.stringify(VIS.email)}))`, 10000, 'novo usuário na tabela');
    assert.equal(await avaliar(`document.getElementById('nu-senha').value`), '', 'a senha provisória some do campo');
    assert.ok(!(await texto('#tb-usr')).includes(VIS.senha), 'a senha nunca aparece na lista');

    // e-mail repetido
    await digitar('#nu-nome', 'Outro'); await digitar('#nu-email', VIS.email); await digitar('#nu-senha', VIS.senha);
    await avaliar(`document.getElementById('f-usr').requestSubmit()`);
    await esperar(`/já existe/i.test(document.getElementById('e-usr').textContent)`, 8000, 'e-mail repetido recusado');
});

test('visualizador entra, só consulta: Controle e Laboratório ficam travados e não há gestão de usuários', async () => {
    await clicar('#btn-sair');
    await esperar(`!document.getElementById('tela-login').hidden`, 10000, 'volta ao login depois de sair');
    assert.equal(await avaliar(`fetch('/api/status').then((r) => r.status)`), 401, 'depois de sair, a API volta a negar');

    await entrarNaTela(VIS.email, VIS.senha);
    await esperar(`document.getElementById('tela-login').hidden && !document.querySelector('.app').hidden`, 10000, 'painel do visualizador');
    assert.match(await texto('#conta-papel'), /Visualizador/i);

    await abrir('controle');
    await esperar(`document.getElementById('ctl-banner')?.textContent.includes('somente de consulta')`, 10000, 'aviso de consulta no Controle');
    assert.equal(await avaliar(`document.getElementById('in-sp').disabled`), true);

    await abrir('laboratorio');
    await esperar(`!!document.getElementById('b-iniciar')`, 10000, 'laboratório');
    assert.equal(await avaliar(`document.getElementById('b-iniciar').disabled`), true);

    await abrir('integracoes');
    await esperar(`!!document.getElementById('tb-con')`, 10000, 'integrações');
    assert.equal(await visivel('#sec-usuarios'), false);
    assert.equal(await avaliar(`fetch('/api/usuarios').then((r) => r.status)`), 403);
    assert.equal(await avaliar(`fetch('/api/experimentos', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ tipo: 'respostaSetpoint', fonte: 'simulacao' }) }).then((r) => r.status)`), 403);
});

test('troca da própria senha: valida, aplica e a nova senha passa a valer', async () => {
    await abrir('integracoes');
    await esperar(`!!document.getElementById('b-conta-senha')`, 10000, 'seção Minha conta');
    await clicar('#b-conta-senha');
    await esperar(`document.getElementById('dlg-senha').open`, 5000, 'diálogo de senha');

    await digitar('#sn-atual', VIS.senha); await digitar('#sn-nova', 'curta'); await digitar('#sn-conf', 'curta');
    await avaliar(`document.getElementById('f-senha').requestSubmit()`);
    assert.match(await texto('#sn-erro'), /pelo menos 10/);

    await digitar('#sn-nova', VIS.nova); await digitar('#sn-conf', VIS.nova + 'x');
    await avaliar(`document.getElementById('f-senha').requestSubmit()`);
    assert.match(await texto('#sn-erro'), /não confere/);

    await digitar('#sn-atual', 'senha-atual-errada-1'); await digitar('#sn-conf', VIS.nova);
    await avaliar(`document.getElementById('f-senha').requestSubmit()`);
    await esperar(`/não confere/.test(document.getElementById('sn-erro').textContent) && document.getElementById('dlg-senha').open`, 8000, 'senha atual errada recusada');

    await digitar('#sn-atual', VIS.senha);
    await avaliar(`document.getElementById('f-senha').requestSubmit()`);
    await esperar(`!document.getElementById('dlg-senha').open`, 10000, 'diálogo fechado após salvar');
    assert.equal(await avaliar(`fetch('/api/status').then((r) => r.status)`), 200, 'quem trocou continua logado');

    await clicar('#b-conta-sair');
    await esperar(`!document.getElementById('tela-login').hidden`, 10000, 'login');
    await entrarNaTela(VIS.email, VIS.senha);
    await esperar(`document.getElementById('login-erro').textContent.length > 0`, 8000, 'senha antiga recusada');
    await entrarNaTela(VIS.email, VIS.nova);
    await esperar(`document.getElementById('tela-login').hidden`, 10000, 'senha nova aceita');
});

test('quando o administrador desativa a conta, a sessão aberta cai e a tela pede o login de novo', async () => {
    await abrir('visao-geral');
    await esperar(`document.getElementById('tela-login').hidden && !document.querySelector('.app').hidden`, 15000, 'visualizador logado');

    const api = await adminApi();
    const lista = (await (await api('/api/usuarios')).json()).usuarios;
    const alvo = lista.find((u) => u.email === VIS.email);
    assert.ok(alvo);
    assert.equal((await api(`/api/usuarios/${alvo.id}`, { method: 'PATCH', body: JSON.stringify({ ativo: false }) })).status, 200);

    // A próxima chamada do painel recebe 401 e a tela de login volta, avisando.
    await avaliar(`location.hash = '#/historico'`);
    await esperar(`!document.getElementById('tela-login').hidden`, 20000, 'tela de login depois da desativação');
    assert.match(await texto('#login-aviso'), /sessão terminou/i);
});

test('nenhuma exceção de JavaScript durante a bateria de login', () => {
    assert.deepEqual(erros, []);
});
