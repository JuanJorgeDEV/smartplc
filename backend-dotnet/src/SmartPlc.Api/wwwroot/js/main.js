// Inicialização: tema, navegação por hash, faixa de estado, alarmes e carga inicial.
import { state, on, comunicacao, idadeAtualSeg, reconhecerAlarmes, carregarConfig, carregarStatus, carregarComandos, iniciarRelogio, avaliar } from './store.js';
import { conectar } from './live.js';
import { iniciarTermos } from './ui.js';
import { hora, idade } from './format.js';

const ROTAS = {
    'visao-geral': { titulo: 'Visão Geral', carregar: () => import('./views/overview.js') },
    'controle': { titulo: 'Controle', carregar: () => import('./views/control.js') },
    'historico': { titulo: 'Histórico', carregar: () => import('./views/history.js') },
    'laboratorio': { titulo: 'Laboratório', carregar: () => import('./views/lab.js') },
    'integracoes': { titulo: 'Integrações', carregar: () => import('./views/integrations.js') }
};

const $ = (id) => document.getElementById(id);

// ---------- Tema ----------
function tema() { return document.documentElement.getAttribute('data-theme') === 'light' ? 'light' : 'dark'; }
function alternarTema() {
    const novo = tema() === 'dark' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-theme', novo);
    try { localStorage.setItem('smartplc.tema', novo); } catch { /* sem armazenamento */ }
    $('btn-tema').textContent = $('btn-tema-m').textContent = novo === 'dark' ? 'Tema claro' : 'Tema escuro';
    window.dispatchEvent(new CustomEvent('smartplc:tema'));
}

// ---------- Navegação ----------
let limpeza = null;
let rotaAtual = null;
let sequencia = 0;

async function navegar() {
    const chave = (location.hash.replace(/^#\/?/, '').split(/[?/]/)[0]) || 'visao-geral';
    const rota = ROTAS[chave] ?? ROTAS['visao-geral'];
    const id = ROTAS[chave] ? chave : 'visao-geral';
    if (rotaAtual === id && !location.hash.includes('?')) { return; }
    rotaAtual = id;
    const minha = ++sequencia;

    if (typeof limpeza === 'function') { try { limpeza(); } catch (e) { console.error(e); } }
    limpeza = null;

    document.querySelectorAll('.nav a').forEach((a) => {
        if (a.dataset.view === id) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current');
    });
    $('titulo-pagina').textContent = rota.titulo;
    document.title = `${rota.titulo} · SmartPLC`;

    const raiz = $('view-root');
    raiz.innerHTML = '';
    raiz.dataset.view = id;
    try {
        const modulo = await rota.carregar();
        if (minha !== sequencia) return; // o usuário já foi para outra aba
        limpeza = await modulo.montar(raiz, { params: new URLSearchParams(location.hash.split('?')[1] ?? '') });
    } catch (e) {
        console.error(e);
        raiz.innerHTML = '<p class="note" role="alert">Não foi possível carregar esta seção. Recarregue a página.</p>';
    }
}

// ---------- Faixa de estado (sempre visível) ----------
function textoComunicacao(c) {
    switch (c) {
        case 'ok': return ['live', 'Recebendo do CLP'];
        case 'aguardando': return ['warn', 'Aguardando telemetria'];
        case 'sem_clp': return ['alarm', 'Sem comunicação com o CLP'];
        default: return ['alarm', state.hub === 'reconectando' ? 'Reconectando ao servidor…' : state.hub === 'conectando' ? 'Conectando…' : 'Servidor desconectado'];
    }
}

function atualizarFaixa() {
    const c = comunicacao();
    const [classe, texto] = textoComunicacao(c);
    const chip = $('chip-conexao');
    chip.className = `tag ${classe}`;
    chip.textContent = texto;

    const idadeSeg = idadeAtualSeg();
    $('ultima-leitura').textContent = idadeSeg === null ? '—' : `${idade(idadeSeg)} · ${hora(Date.now() - idadeSeg * 1000)}`;
    $('relogio').textContent = hora(Date.now());

    const cfg = state.config;
    $('rail-modo').textContent = cfg ? (cfg.controle.habilitado ? 'Modo · controle habilitado' : 'Modo · somente leitura') : 'Modo —';
}

const MENSAGENS_ALARME = {
    alto: 'Nível acima do limite alto. Verifique a válvula e o setpoint.',
    baixo: 'Nível abaixo do limite baixo. Verifique a alimentação.',
    com: 'Sem comunicação confiável: os valores exibidos podem estar desatualizados.'
};
function atualizarBanner() {
    const pendentes = Object.entries(state.alarmes).filter(([, a]) => a.ativo && !a.ack).map(([k]) => k);
    const faixa = $('faixa-alarme');
    if (!pendentes.length) { faixa.hidden = true; return; }
    const ordem = ['alto', 'com', 'baixo'];
    const principal = ordem.find((k) => pendentes.includes(k));
    $('faixa-texto').textContent = `${pendentes.length > 1 ? `(${pendentes.length} alarmes) ` : ''}ALARME · ${MENSAGENS_ALARME[principal]}`;
    faixa.hidden = false;
}

// ---------- Boot ----------
async function iniciar() {
    $('btn-tema').textContent = $('btn-tema-m').textContent = tema() === 'dark' ? 'Tema claro' : 'Tema escuro';
    $('btn-tema').addEventListener('click', alternarTema);
    $('btn-tema-m').addEventListener('click', alternarTema);
    $('btn-reconhecer').addEventListener('click', () => reconhecerAlarmes());
    iniciarTermos();

    on('conexao', atualizarFaixa);
    on('status', atualizarFaixa);
    on('tick', atualizarFaixa);
    on('config', atualizarFaixa);
    on('alarmes', atualizarBanner);

    window.addEventListener('hashchange', navegar);
    atualizarFaixa();

    // Config primeiro: as telas dependem dos limites e dos modos (controle/histórico/laboratório)
    await carregarConfig();
    await navegar();
    conectar();
    carregarStatus();
    carregarComandos();
    iniciarRelogio();
    avaliar();
}

iniciar();
