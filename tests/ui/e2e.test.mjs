// Testes ponta a ponta da interface, em Chrome REAL (headless, protocolo CDP). Sem dependências npm.
//
// Pré-requisitos (ver docs/testes.md):
//   - backend .NET rodando em http://localhost:5000 com Control__Enabled=true e histórico ligado (PostgreSQL local/de teste);
//   - alguém enviando telemetria e atendendo /api/comando-clp (o Node-RED real OU tools/simulador-nodered.js).
// Uso:  node --test tests/ui/e2e.test.mjs      (BASE_URL=http://localhost:5000 por padrão)
// ATENÇÃO: o teste cria um experimento e envia comandos de setpoint ao que estiver respondendo como "CLP".
// Com o simulador isso é inofensivo; NÃO aponte para um CLP/PLCSIM real sem querer.

import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const BASE = process.env.BASE_URL || 'http://localhost:5000';
const CHROME = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find((p) => fs.existsSync(p));
const dormir = (ms) => new Promise((r) => setTimeout(r, ms));

let chrome, perfil, ws, nextId = 0;
const pend = new Map();
const erros = [];     // exceções e console.error da página

async function iniciar() {
    perfil = fs.mkdtempSync(path.join(os.tmpdir(), 'smartplc-e2e-'));
    chrome = spawn(CHROME, ['--headless=new', '--disable-gpu', '--hide-scrollbars', '--remote-debugging-port=9444', `--user-data-dir=${perfil}`, 'about:blank'], { stdio: 'ignore' });
    let url;
    for (let i = 0; i < 80 && !url; i++) {
        try { url = (await (await fetch('http://127.0.0.1:9444/json/list')).json()).find((t) => t.type === 'page')?.webSocketDebuggerUrl; } catch { /* subindo */ }
        if (!url) await dormir(250);
    }
    assert.ok(url, 'Chrome não respondeu ao protocolo de depuração');
    ws = new WebSocket(url);
    await new Promise((ok, ko) => { ws.onopen = ok; ws.onerror = ko; });
    ws.onmessage = (m) => {
        const d = JSON.parse(m.data);
        if (d.id && pend.has(d.id)) { const { ok, ko } = pend.get(d.id); pend.delete(d.id); d.error ? ko(new Error(d.error.message)) : ok(d.result); }
        else if (d.method === 'Runtime.exceptionThrown') erros.push(`exceção: ${d.params.exceptionDetails.exception?.description ?? d.params.exceptionDetails.text}`);
        else if (d.method === 'Runtime.consoleAPICalled' && d.params.type === 'error') erros.push(`console.error: ${d.params.args.map((a) => a.value ?? a.description).join(' ')}`);
        else if (d.method === 'Log.entryAdded' && d.params.entry.level === 'error' && !/favicon|net::ERR_(BLOCKED|INTERNET)/.test(d.params.entry.text + d.params.entry.url)) erros.push(`log: ${d.params.entry.text} ${d.params.entry.url ?? ''}`);
    };
    for (const m of ['Page.enable', 'Runtime.enable', 'Log.enable']) await cdp(m);
}

const cdp = (method, params = {}) => new Promise((ok, ko) => { const id = ++nextId; pend.set(id, { ok, ko }); ws.send(JSON.stringify({ id, method, params })); });

async function avaliar(expressao) {
    const r = await cdp('Runtime.evaluate', { expression: expressao, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) throw new Error(`avaliar: ${r.exceptionDetails.exception?.description ?? r.exceptionDetails.text}`);
    return r.result.value;
}

async function esperar(expressao, ms = 15000, msg = expressao) {
    const limite = Date.now() + ms;
    let ultimo;
    while (Date.now() < limite) {
        try { ultimo = await avaliar(expressao); if (ultimo) return ultimo; } catch { /* página trocando */ }
        await dormir(200);
    }
    assert.fail(`Tempo esgotado esperando: ${msg} (último valor: ${JSON.stringify(ultimo)})`);
}

async function abrir(rota, { largura = 1440, altura = 900, tema = 'dark', movel = false } = {}) {
    await cdp('Emulation.setDeviceMetricsOverride', { width: largura, height: altura, deviceScaleFactor: 1, mobile: movel });
    await cdp('Page.navigate', { url: 'about:blank' });
    await cdp('Page.navigate', { url: `${BASE}/?tema=${tema}#/${rota}` });
    await esperar(`document.querySelector('#view-root')?.children.length > 0 && document.readyState === 'complete'`, 15000, `rota ${rota} montada`);
}

const clicar = (sel) => avaliar(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); if (!e) throw new Error('não achei ${sel}'); e.click(); return true; })()`);
const texto = (sel) => avaliar(`document.querySelector(${JSON.stringify(sel)})?.textContent ?? ''`);
function definir(sel, valor) {
    return avaliar(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); if (!e) throw new Error('não achei ${sel}'); e.value = ${JSON.stringify(String(valor))}; e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
}
const enviarForm = (sel) => avaliar(`(() => { document.querySelector(${JSON.stringify(sel)}).requestSubmit(); return true; })()`);
const confirmarDialogo = async () => { await esperar(`document.getElementById('dlg-confirmar')?.open`, 5000, 'diálogo de confirmação aberto'); await clicar('#dlg-confirmar [data-ok]'); };
const api = (caminho, opcoes) => avaliar(`fetch(${JSON.stringify(caminho)}, ${JSON.stringify(opcoes ?? {})}).then(async (r) => ({ status: r.status, corpo: await r.json().catch(() => null) }))`);

const ROTAS = [['visao-geral', 'Visão Geral'], ['controle', 'Controle'], ['historico', 'Histórico'], ['laboratorio', 'Laboratório'], ['integracoes', 'Integrações']];

before(async () => {
    assert.ok(CHROME, 'Chrome/Edge não encontrado');
    const r = await fetch(`${BASE}/api/config`).catch(() => null);
    assert.ok(r?.ok, `Backend não responde em ${BASE}`);
    const cfg = await r.json();
    assert.ok(cfg.controle.habilitado && !cfg.controle.exigeChaveDeOperador, 'Suba o backend com Control__Enabled=true (sem chave de operador) para este teste');
    assert.ok(cfg.historico.habilitado && cfg.laboratorio.habilitado, 'Suba o backend com ConnectionStrings__SmartPlc (histórico e laboratório ligados)');
    const st = await (await fetch(`${BASE}/api/status`)).json();
    assert.ok(st.telemetria.plcOnline, 'Sem telemetria chegando: inicie o Node-RED ou tools/simulador-nodered.js');
    const ativa = await (await fetch(`${BASE}/api/experimentos?status=ativa`)).json();
    assert.equal(ativa.sessoes.length, 0, 'Há um experimento ativo; encerre-o antes de rodar o teste');
    await iniciar();
});

after(async () => {
    try { ws?.close(); } catch { /* ok */ }
    chrome?.kill();
    await dormir(400);
    if (perfil) fs.rmSync(perfil, { recursive: true, force: true });
});

// ======================================================================
test('navegação: as 5 seções abrem, marcam a aba atual e não geram erros de console', async () => {
    erros.length = 0;
    for (const [rota, titulo] of ROTAS) {
        await abrir(rota);
        await esperar(`document.getElementById('titulo-pagina').textContent === ${JSON.stringify(titulo)}`, 5000, `título de ${rota}`);
        assert.equal(await avaliar(`document.querySelector('.nav a[aria-current="page"]')?.dataset.view`), rota);
        assert.equal(await avaliar(`document.querySelectorAll('h1').length`), 1, `${rota}: deve haver um único h1`);
    }
    // navegação por clique, sem recarregar a página
    await clicar('.nav a[data-view="historico"]');
    await esperar(`document.getElementById('titulo-pagina').textContent === 'Histórico'`);
    assert.deepEqual(erros, []);
});

test('visão geral: o tanque, os indicadores e o status refletem a telemetria real', async () => {
    await abrir('visao-geral');
    await esperar(`document.getElementById('chip-conexao').textContent.includes('Recebendo')`, 15000, 'chip "Recebendo do CLP"');
    await esperar(`/\\d/.test(document.getElementById('t-pv').textContent)`);
    const dados = await avaliar(`(async () => {
        const s = await (await fetch('/api/status')).json();
        const pvTxt = document.getElementById('t-pv').textContent;
        const escala = Number(document.getElementById('t-liquido').style.transform.match(/scaleY\\(([^)]+)\\)/)[1]);
        return { nivel: s.telemetria.nivel, sp: s.controle.setpoint, pvTxt, escala, estado: document.getElementById('stage').dataset.estado, alarmes: document.querySelectorAll('#alm-lista li').length, spTxt: document.getElementById('t-sp-txt').textContent };
    })()`);
    const pvMostrado = Number(dados.pvTxt.replace(/[^\d,]/g, '').replace(',', '.'));
    assert.ok(Math.abs(pvMostrado - dados.nivel) < 4, `PV exibida ${pvMostrado} vs API ${dados.nivel}`);
    assert.ok(Math.abs(dados.escala - pvMostrado / 100) < 0.05, `nível do líquido (${dados.escala}) deve acompanhar a PV (${pvMostrado})`);
    assert.equal(dados.estado, 'ok');
    assert.equal(dados.alarmes, 4);
    assert.match(dados.spTxt, /SP\s*\d/);
});

test('visão geral: a tendência recebe leituras reais e o gráfico existe', async () => {
    await abrir('visao-geral');
    await esperar(`document.getElementById('tend-nota').textContent.match(/(\\d+) leituras/)?.[1] >= 3`, 15000, '≥ 3 leituras no gráfico');
    assert.equal(await avaliar(`!!document.getElementById('cv-tendencia').getContext`), true);
    assert.equal(await avaliar(`document.getElementById('cv-tendencia').getAttribute('role')`), 'img');
});

test('tema: alterna claro/escuro e lembra a escolha', async () => {
    await abrir('visao-geral', { tema: 'dark' });
    assert.equal(await avaliar(`document.documentElement.dataset.theme`), 'dark');
    await clicar('#btn-tema');
    assert.equal(await avaliar(`document.documentElement.dataset.theme`), 'light');
    assert.equal(await avaliar(`localStorage.getItem('smartplc.tema')`), 'light');
    await clicar('#btn-tema');
    assert.equal(await avaliar(`document.documentElement.dataset.theme`), 'dark');
});

test('controle: valida a faixa, pede confirmação, mostra as etapas e só confirma quando o CLP reporta', async () => {
    await abrir('controle');
    await esperar(`document.getElementById('ctl-banner')?.textContent.includes('Controle habilitado')`, 8000, 'banner de controle habilitado');
    await esperar(`document.getElementById('v-sp').textContent !== '—'`);

    // valor fora da faixa: erro visível e nenhum diálogo
    await definir('#in-sp', 150);
    await enviarForm('#f-sp');
    assert.match(await texto('#e-sp'), /entre 0 e 100/);
    assert.equal(await avaliar(`document.getElementById('dlg-confirmar').open`), false);

    // valor válido: diálogo com o texto de cautela
    const antes = await avaliar(`Number(document.getElementById('v-sp').textContent.replace(',', '.'))`);
    const novo = antes === 63 ? 57 : 63;
    await definir('#in-sp', novo);
    await enviarForm('#f-sp');
    await esperar(`document.getElementById('dlg-confirmar').open`, 5000, 'diálogo');
    assert.match(await texto('#dlg-confirmar [data-c]'), /só será exibido como aplicado depois que o CLP o reportar/);
    await clicar('#dlg-confirmar [data-ok]');

    // aparece na lista; termina "Confirmado pelo CLP" depois da releitura (telemetria), nunca antes de existir
    await esperar(`document.querySelectorAll('.cmd').length >= 1`, 8000, 'comando listado');
    await esperar(`document.querySelector('.cmd .small')?.textContent.includes('Confirmado pelo CLP')`, 15000, 'comando confirmado pela releitura');
    await esperar(`document.getElementById('v-sp').textContent.replace(',', '.').startsWith(${JSON.stringify(String(novo))})`, 8000, 'valor no CLP atualizado só depois da releitura');
    assert.equal(await avaliar(`document.querySelectorAll('.cmd .steps li').length`) >= 3, true);
});

test('histórico: consulta o banco, mostra amostras e eventos e valida o intervalo personalizado', async () => {
    await abrir('historico');
    await esperar(`document.getElementById('info')?.textContent.includes('Amostras retornadas') && !document.getElementById('info').textContent.includes('carregando')`, 20000, 'consulta concluída');
    const amostras = Number(await avaliar(`document.querySelector('#info div:nth-child(2) dd').textContent.replace(/[^\\d]/g, '')`));
    assert.ok(amostras > 0, 'deve haver amostras persistidas');
    assert.equal(await avaliar(`document.getElementById('b-csv-banco').disabled`), false);
    assert.equal(await avaliar(`document.getElementById('cv-hist').getAttribute('aria-label').includes('amostras')`), true);

    await clicar('#seg-per [data-p="6h"]');
    await esperar(`document.querySelector('#seg-per [data-p="6h"]').getAttribute('aria-checked') === 'true'`);
    await clicar('#seg-per [data-p="custom"]');
    assert.equal(await avaliar(`document.getElementById('f-custom').hidden`), false);
    await definir('#in-de', '2026-12-31T12:00');
    await definir('#in-ate', '2026-12-30T12:00');
    await enviarForm('#f-custom');
    assert.match(await texto('#e-custom'), /anterior/);
    await definir('#in-de', '2020-01-01T00:00');
    await definir('#in-ate', '2020-01-10T00:00');
    await enviarForm('#f-custom');
    assert.match(await texto('#e-custom'), /máximo/);
});

test('laboratório: inicia, observa, encerra e revisa uma sessão com valores reais (ponta a ponta)', async () => {
    await abrir('laboratorio');
    await esperar(`!!document.getElementById('f-iniciar')`, 10000, 'tela de preparação');
    assert.match(await texto('.lab-titulo'), /Laboratório SmartPLC/);
    await esperar(`!document.getElementById('b-iniciar').disabled`, 10000, 'botão de iniciar habilitado (telemetria válida)');
    await definir('#in-nome', 'E2E degrau');
    await enviarForm('#f-iniciar');
    await esperar(`!!document.getElementById('b-encerrar')`, 10000, 'sessão ativa');
    assert.match(await texto('#lab-vivo'), /Tempo decorrido/);

    // a mudança de setpoint é feita pelo caminho de comando existente; o laboratório só observa
    const atual = await avaliar(`fetch('/api/status').then(r => r.json()).then(s => s.controle.setpoint)`);
    const alvo = atual > 50 ? atual - 12 : atual + 12;
    const r = await api('/api/controle', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ setpoint: alvo }) });
    assert.equal(r.status, 200);
    await esperar(`document.getElementById('deteccao')?.textContent.includes('Detectado')`, 20000, 'mudança de setpoint detectada');
    await dormir(4000);

    await clicar('#b-encerrar');
    await confirmarDialogo();
    await esperar(`document.querySelector('.lab-cab2 .tag')?.textContent.includes('Encerrada')`, 15000, 'sessão encerrada');
    await esperar(`document.body.textContent.includes('Resposta ao degrau')`, 15000, 'bloco de resposta ao degrau');
    const resumo = await texto('#palco');
    assert.match(resumo, /Duração/);
    assert.match(resumo, /Nível inicial → final/);
    assert.match(resumo, /Amostras persistidas/);
    // ou há métricas, ou o motivo de não calcular: nunca um bloco vazio
    assert.ok(/Tempo até 63 %/.test(resumo) || /Métricas não calculadas/.test(resumo));
    assert.equal(await avaliar(`document.querySelectorAll('#sessoes .sessao').length >= 1`), true);
    assert.equal(await avaliar(`!!document.getElementById('cv-lab')`), true);
});

test('laboratório em simulação: inicia sem CLP, aceita alterações, registra o degrau e fecha o resumo', async () => {
    await abrir('laboratorio');
    await esperar(`!!document.getElementById('f-iniciar')`, 10000, 'formulário de preparação');
    await clicar('input[name="fonte"][value="simulacao"]');
    await esperar(`document.getElementById('sim-params').hidden === false && document.getElementById('lab-ro').hidden === true`, 3000, 'parâmetros da simulação visíveis');
    await definir('#sp-nivel', 25);
    await enviarForm('#f-iniciar');
    await esperar(`!!document.querySelector('.lab-cab2 .tag.sim') && !!document.getElementById('f-sim')`, 10000, 'palco da simulação');

    // O processo real (simulador de Node-RED) segue independente: a simulação não mexe no estado do CLP.
    const antes = await api('/api/status');
    await definir('#sm-vel', 10);
    await esperar(`document.getElementById('lab-vivo')?.textContent.includes('10×')`, 8000, 'velocidade 10× aplicada');
    await definir('#sm-sp', 60);
    await clicar('[data-ap="sp"]');
    await esperar(`document.getElementById('deteccao').textContent.includes('Registrado')`, 8000, 'mudança de setpoint registrada');
    const pv = () => avaliar(`Number(document.querySelectorAll('#lab-vivo > div')[1]?.querySelector('dd')?.textContent.replace(/[^\\d,]/g, '').replace(',', '.'))`);
    await esperar(`Number(document.querySelectorAll('#lab-vivo > div')[1]?.querySelector('dd')?.textContent.replace(/[^\\d,]/g, '').replace(',', '.')) > 45`, 60000, 'PV subindo em direção ao novo SP');
    assert.ok((await pv()) > 45);

    // modo manual: a abertura passa a ser a da pessoa
    await clicar('#sm-modo');
    await esperar(`document.getElementById('sm-modo').textContent.includes('automático')`, 8000, 'modo manual aplicado');
    assert.equal(await avaliar(`document.getElementById('sm-vm').disabled`), false);
    // valores inválidos são recusados pelo servidor e explicados
    await definir('#sm-sp', 150);
    await clicar('[data-ap="sp"]');
    await esperar(`document.getElementById('e-sim').textContent.length > 0`, 5000, 'erro de validação exibido');

    await clicar('#b-encerrar');
    await confirmarDialogo();
    await esperar(`document.querySelector('.lab-cab2 .tag.sim') && /Dados simulados/.test(document.body.innerText) && !!document.getElementById('b-nova')`, 15000, 'revisão da sessão simulada');
    const resumo = await texto('.lab-palco');
    assert.match(resumo, /Duração/);
    assert.match(resumo, /Amostras/);
    assert.ok(/Tempo até 63 %/.test(resumo) || /Métricas não calculadas/.test(resumo));
    assert.equal(await avaliar(`!!document.getElementById('cv-lab')`), true);
    assert.equal(await avaliar(`[...document.querySelectorAll('#sessoes .sessao')].some((e) => e.textContent.includes('Simulação'))`), true);

    const depois = await api('/api/status');
    assert.equal(depois.corpo.telemetria.plcOnline, true);
    assert.equal(depois.corpo.controle.setpoint, antes.corpo.controle.setpoint, 'a simulação não pode alterar o setpoint do CLP');
});

test('integrações: 8 variáveis, endereços do fluxo e nenhuma personalização', async () => {
    await abrir('integracoes');
    await esperar(`document.querySelectorAll('#tb-var tr').length === 8`, 8000, '8 variáveis');
    const enderecos = await avaliar(`[...document.querySelectorAll('#tb-var tr td:nth-child(2)')].map((e) => e.textContent.trim())`);
    assert.deepEqual(enderecos, ['DB3,REAL0', 'DB4,REAL0', 'DB5,REAL0', 'DB5,X8.0', 'DB5,REAL10', 'DB5,REAL14', 'DB5,REAL18', 'DB5,REAL22']);
    assert.ok(await avaliar(`[...document.querySelectorAll('#tb-var [data-valor]')].every((e) => /\\d|—|manual|automático/.test(e.textContent))`));
    assert.equal(await avaliar(`document.querySelectorAll('#tb-var input, #tb-var select').length`), 0, 'a tabela é somente leitura');
    assert.equal(await avaliar(`!!document.getElementById('b-restaurar') || /Apresentação|armazenamento local/.test(document.body.innerText)`), false);
});

test('visão geral: não repete a "última leitura válida" (a faixa de estado já mostra)', async () => {
    await abrir('visao-geral');
    await esperar(`document.getElementById('ro-main')?.textContent.includes('Nível')`, 8000, 'indicadores');
    assert.equal(await avaliar(`/Última leitura válida/i.test(document.getElementById('ro-list').textContent)`), false);
});

test('visão geral: PV, SP e CV não ficam colados (SP e CV afastados, PV em linha própria)', async () => {
    await abrir('visao-geral', { largura: 1440, altura: 900 });
    await esperar(`document.querySelector('.ro-setpoint') && document.querySelector('.ro-valvula')`, 8000, 'indicadores');
    const g = await avaliar(`(() => { const r = (s) => document.querySelector(s).getBoundingClientRect(); const pv = r('.ro-nivel'), sp = r('.ro-setpoint'), cv = r('.ro-valvula'); return { pvBaixo: pv.bottom, spTopo: sp.top, spDir: sp.right, cvEsq: cv.left }; })()`);
    assert.ok(g.spTopo >= g.pvBaixo - 1, 'SP abaixo do PV');
    assert.ok(g.cvEsq >= g.spDir - 1, 'CV à direita do SP');
});

test('responsivo: nenhuma tela tem rolagem horizontal em 360, 390, 768 e 1440 px', async () => {
    for (const largura of [360, 390, 768, 1440]) {
        for (const [rota] of ROTAS) {
            await abrir(rota, { largura, altura: 800, movel: largura < 600 });
            await dormir(1200);
            const m = await avaliar(`({ iw: innerWidth, sw: document.documentElement.scrollWidth })`);
            assert.ok(m.sw <= m.iw + 1, `${rota} @${largura}px: scrollWidth ${m.sw} > ${m.iw}`);
        }
    }
});

test('responsivo: em telas estreitas a navegação vai para a base e as 5 abas continuam clicáveis', async () => {
    await abrir('visao-geral', { largura: 390, altura: 800, movel: true });
    const pos = await avaliar(`(() => { const r = document.querySelector('.rail').getBoundingClientRect(); return { bottom: Math.round(r.bottom), ih: innerHeight, itens: [...document.querySelectorAll('.nav a')].map((a) => a.getBoundingClientRect().width > 40 && a.getBoundingClientRect().height >= 40) }; })()`);
    assert.equal(pos.bottom, pos.ih);
    assert.deepEqual(pos.itens, [true, true, true, true, true]);
});

test('acessibilidade básica: campos e botões com nome, foco visível e link para pular', async () => {
    for (const [rota] of ROTAS) {
        await abrir(rota);
        await dormir(1500);
        const semNome = await avaliar(`[...document.querySelectorAll('button, input, select, textarea')].filter((e) => {
            if (e.type === 'hidden') return false;
            const nome = e.getAttribute('aria-label') || e.labels?.length || e.textContent.trim() || e.getAttribute('aria-labelledby') || e.title;
            return !nome;
        }).map((e) => e.outerHTML.slice(0, 90))`);
        assert.deepEqual(semNome, [], `${rota}: controles sem nome acessível`);
    }
    await abrir('controle');
    assert.equal(await avaliar(`document.querySelector('.skip').getAttribute('href')`), '#conteudo');
    assert.ok(await avaliar(`getComputedStyle(document.documentElement).getPropertyValue('--focus-ring').trim().length > 0`));
});

test('explicações: termos abrem a explicação curta e fecham com Escape', async () => {
    await abrir('controle');
    await clicar('.term[data-termo="kp"]');
    assert.equal(await avaliar(`document.getElementById('termo-pop').hidden`), false);
    assert.match(await texto('#termo-pop'), /Ganho proporcional/);
    await avaliar(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))`);
    assert.equal(await avaliar(`document.getElementById('termo-pop').hidden`), true);
});

test('nenhum erro de console ou exceção durante toda a bateria', () => {
    assert.deepEqual(erros, []);
});
