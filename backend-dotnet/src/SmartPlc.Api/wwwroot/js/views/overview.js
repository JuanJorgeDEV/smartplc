// Visão Geral: o tanque TQ-101 é o protagonista. Todos os números vêm da telemetria; sem leitura, mostra "—".
import { state, on, comunicacao, idadeAtualSeg, limiteObsoleto, reconhecerAlarmes } from '../store.js';
import { prefs, nomeDe, casasDe, aoMudar } from '../prefs.js';
import { clamp, num, hora, idade, esc, baixarArquivo, carimboArquivo, csvNum } from '../format.js';
import { termo } from '../glossary.js';
import { criarTendencia, atualizar, aplicarTema, destruir } from '../charts.js';
import * as api from '../api.js';

// Geometria do sinótico (viewBox 0 0 560 470)
const TANQUE = { x: 200, y: 70, w: 170, h: 320 };
const yDe = (pct) => TANQUE.y + TANQUE.h - (clamp(pct, 0, 100) / 100) * TANQUE.h;

function escala() {
    let s = '';
    for (let v = 0; v <= 100; v += 10) {
        const y = yDe(v);
        s += `<line class="t-tick" x1="${TANQUE.x - 8}" x2="${TANQUE.x}" y1="${y}" y2="${y}"/>`;
        if (v % 20 === 0) s += `<text class="t-esc" x="${TANQUE.x - 14}" y="${y + 4}" text-anchor="end">${v}</text>`;
    }
    return s;
}

const SINOTICO = () => `
<svg id="svg-tanque" viewBox="0 0 560 470" role="img" aria-labelledby="svg-t">
  <title id="svg-t">Sinótico do tanque TQ-101: nível (PV), setpoint (SP) e abertura da válvula de entrada FV-101</title>
  <defs><clipPath id="recorte"><rect x="${TANQUE.x}" y="${TANQUE.y}" width="${TANQUE.w}" height="${TANQUE.h}"/></clipPath></defs>

  <!-- tubulação de entrada (FV-101) -->
  <path class="t-tubo" d="M24 44 H285 V${TANQUE.y}"/>
  <path id="t-fluxo-in" class="t-fluxo" d="M24 44 H285 V${TANQUE.y}"/>
  <g transform="translate(128 44)">
    <rect class="t-atuador" x="-14" y="-36" width="28" height="14" />
    <line class="t-haste" x1="0" y1="-22" x2="0" y2="-10"/>
    <path id="t-valvula" class="t-valvula" d="M-18 -10 L0 0 L-18 10 Z M18 -10 L0 0 L18 10 Z"/>
    <text class="t-tag" x="22" y="-24">FV-101</text>
    <text id="t-cv" class="t-valor" x="0" y="34" text-anchor="middle">—</text>
  </g>

  <!-- tubulação de saída (sem instrumentação no projeto) -->
  <path class="t-tubo" d="M285 ${TANQUE.y + TANQUE.h} V430 H536"/>
  <text class="t-tag" x="536" y="452" text-anchor="end">SAÍDA</text>

  <!-- tanque -->
  <rect class="t-fundo" x="${TANQUE.x}" y="${TANQUE.y}" width="${TANQUE.w}" height="${TANQUE.h}"/>
  <g clip-path="url(#recorte)">
    <rect id="t-liquido" class="t-liquido" x="${TANQUE.x}" y="${TANQUE.y}" width="${TANQUE.w}" height="${TANQUE.h}"/>
  </g>
  <line class="t-zona" x1="${TANQUE.x}" x2="${TANQUE.x + TANQUE.w}" y1="${yDe(10)}" y2="${yDe(10)}"/>
  <line class="t-zona" x1="${TANQUE.x}" x2="${TANQUE.x + TANQUE.w}" y1="${yDe(90)}" y2="${yDe(90)}"/>
  <text id="t-zl" class="t-tag" x="${TANQUE.x + 6}" y="${yDe(10) - 5}">L 10</text>
  <text id="t-zh" class="t-tag" x="${TANQUE.x + 6}" y="${yDe(90) - 5}">H 90</text>
  <line id="t-menisco" class="t-menisco" x1="${TANQUE.x}" x2="${TANQUE.x + TANQUE.w}" y1="${TANQUE.y + TANQUE.h}" y2="${TANQUE.y + TANQUE.h}"/>
  <rect id="t-contorno" class="t-contorno" x="${TANQUE.x}" y="${TANQUE.y}" width="${TANQUE.w}" height="${TANQUE.h}"/>
  <g>${escala()}</g>

  <text class="t-tag" x="150" y="214" text-anchor="end">LT-101 · PV</text>
  <text id="t-pv" class="t-pv" x="150" y="252" text-anchor="end">—</text>

  <!-- setpoint -->
  <g id="t-sp" class="t-sp">
    <line x1="${TANQUE.x}" x2="${TANQUE.x + TANQUE.w + 12}" y1="0" y2="0"/>
    <path d="M${TANQUE.x + TANQUE.w + 12} 0 l12 -7 v14 z"/>
    <text id="t-sp-txt" x="${TANQUE.x + TANQUE.w + 30}" y="4">SP —</text>
  </g>
</svg>`;

const TIPOS_EVENTO = {
    NivelAlto: 'Nível alto', NivelAltoNormalizado: 'Nível alto normalizado', NivelBaixo: 'Nível baixo', NivelBaixoNormalizado: 'Nível baixo normalizado',
    ComunicacaoPerdida: 'Comunicação perdida', ComunicacaoRestabelecida: 'Comunicação restabelecida',
    SetpointAlterado: 'Setpoint alterado (lido do CLP)', ModoAlterado: 'Modo alterado (lido do CLP)', PidAlterado: 'PID alterado (lido do CLP)'
};
export const rotuloEvento = (t) => TIPOS_EVENTO[t] ?? t;
const sevEvento = (t) => (/Normalizado|Restabelecida/.test(t) ? 'ok' : /Alto|Baixo|Perdida/.test(t) ? 'alarm' : 'info');

export const titulo = 'Visão Geral';

export async function montar(raiz) {
    const lim = state.config?.processo ?? { nivelBaixo: 10, nivelAlto: 90 };
    raiz.innerHTML = `
<div class="ov">
  <section class="ov-stage" aria-labelledby="t-tanque">
    <h2 id="t-tanque" class="sr-only">Sinótico do processo</h2>
    <div class="stage" id="stage" data-estado="servidor_off">
      <p class="stage-msg" id="stage-msg" role="status"></p>
      ${SINOTICO()}
    </div>
    <p class="note stage-nota">${termo('pv')} é o nível medido pelo transmissor LT-101; ${termo('sp')}, o nível desejado; ${termo('cv')}, a abertura da válvula FV-101. As animações só acompanham valores recebidos.</p>
  </section>

  <section class="ov-ind" aria-labelledby="t-ind">
    <div class="sec-head"><h2 id="t-ind" class="eyebrow">Indicadores operacionais</h2><span class="tag" id="ind-tag"></span></div>
    <dl class="ro-main" id="ro-main"></dl>
    <dl class="ro-list" id="ro-list"></dl>
    <section id="sec-alarmes" class="ov-alarmes" aria-labelledby="t-alm">
      <div class="sec-head"><h2 id="t-alm" class="eyebrow">Alarmes</h2><button type="button" class="btn small" id="btn-ack">Reconhecer</button></div>
      <ul class="alm-lista" id="alm-lista"></ul>
    </section>
  </section>
</div>

<section class="ov-trend" id="sec-tendencia" aria-labelledby="t-tend">
  <div class="sec-head">
    <h2 id="t-tend" class="eyebrow">Tendência · PV e SP</h2>
    <div class="sec-tools">
      <ul class="legenda" aria-label="Legenda"><li><i class="sw pv"></i>PV nível</li><li><i class="sw sp"></i>SP setpoint</li><li><label class="check"><input type="checkbox" id="chk-cv"> <i class="sw cv"></i>CV válvula</label></li></ul>
      <label class="sr-only" for="sel-janela">Janela de tempo</label>
      <select class="input sel-curto" id="sel-janela"><option value="1">1 min</option><option value="5" selected>5 min</option><option value="15">15 min</option><option value="60">60 min</option></select>
      <button type="button" class="btn small" id="btn-pausa" aria-pressed="false">Pausar</button>
      <button type="button" class="btn small" id="btn-csv">Exportar sessão (CSV)</button>
    </div>
  </div>
  <div class="chart-box"><canvas id="cv-tendencia"></canvas></div>
  <p class="note" id="tend-nota"></p>
</section>

<div class="ov-bottom">
  <section id="sec-resumo" aria-labelledby="t-res">
    <div class="sec-head"><h2 id="t-res" class="eyebrow">Resumo de operação</h2><span class="badge" id="res-fonte"></span></div>
    <ul class="ev-lista" id="ev-lista"></ul>
    <p class="note" id="ev-vazio" hidden></p>
  </section>
</div>`;

    const $ = (id) => raiz.querySelector(`#${id}`);
    const stage = $('stage');
    let pausado = false;
    let janelaMin = 5;
    let mostrarCv = false;
    let chart = criarTendencia($('cv-tendencia'), { cv: true, ariaLabel: 'Gráfico de tendência de PV e SP nesta aba' });
    if (chart) chart.data.datasets[2].hidden = true;

    // ---------- Sinótico ----------
    function pintarTanque() {
        const com = comunicacao();
        const t = state.status.telemetria;
        const c = state.status.controle;
        const tem = t.timestamp !== null;
        stage.dataset.estado = com;

        const frac = tem ? clamp(t.nivel, 0, 100) / 100 : 0;
        $('t-liquido').style.transform = `scaleY(${frac})`;
        $('t-menisco').style.transform = `translateY(${-frac * TANQUE.h}px)`;
        $('t-menisco').style.opacity = tem ? '1' : '0';
        $('t-pv').innerHTML = tem ? `${num(t.nivel, casasDe('nivel'))}<tspan class="t-un"> %</tspan>` : '—';

        const spY = TANQUE.y + TANQUE.h - clamp(c.setpoint, 0, 100) / 100 * TANQUE.h;
        $('t-sp').style.transform = `translateY(${spY}px)`;
        $('t-sp').style.opacity = tem ? '1' : '0';
        $('t-sp-txt').textContent = `SP ${num(c.setpoint, casasDe('setpoint'))}`;

        $('t-cv').textContent = tem ? `${num(t.valvula, casasDe('valvula'))} %` : '—';
        $('t-valvula').style.fillOpacity = tem ? (0.12 + 0.88 * clamp(t.valvula, 0, 100) / 100).toFixed(2) : '0';
        $('t-fluxo-in').style.opacity = com === 'ok' ? (clamp(t.valvula, 0, 100) / 100).toFixed(2) : '0';

        $('t-contorno').classList.toggle('baixo', com === 'ok' && !!t.leds.low);
        $('t-contorno').classList.toggle('alto', com === 'ok' && !!t.leds.high);

        const msg = $('stage-msg');
        if (com === 'ok') msg.textContent = '';
        else if (com === 'aguardando') msg.textContent = 'AGUARDANDO TELEMETRIA · nenhuma leitura recebida do CLP ainda';
        else if (com === 'sem_clp') msg.textContent = `SEM COMUNICAÇÃO COM O CLP · último valor ${idade(idadeAtualSeg())}`;
        else msg.textContent = 'SERVIDOR DESCONECTADO · valores exibidos podem estar desatualizados';
    }

    // ---------- Indicadores ----------
    const catalogo = () => state.variaveis.length ? Object.fromEntries(state.variaveis.map((v) => [v.campo, v])) : {};
    function valorBruto(campo) {
        const t = state.status.telemetria, c = state.status.controle;
        return ({ nivel: t.nivel, valvula: t.valvula, setpoint: c.setpoint, modoManual: c.modoManual, kp: c.kp, ti: c.ti, td: c.td, valvulaManual: c.valvulaManual })[campo];
    }
    function textoValor(campo) {
        if (state.status.telemetria.timestamp === null) return '—';
        const v = valorBruto(campo);
        if (campo === 'modoManual') return v ? 'Manual' : 'Automático';
        return num(v, casasDe(campo));
    }
    const unidade = (campo) => catalogo()[campo]?.unidade ?? (['nivel', 'valvula', 'setpoint', 'valvulaManual'].includes(campo) ? '%' : campo === 'ti' || campo === 'td' ? 's' : '');
    const rotuloVar = (campo, padrao) => nomeDe(campo, catalogo()[campo]?.nome ?? padrao);

    function pintarIndicadores() {
        const p = prefs();
        const com = comunicacao();
        const velho = com !== 'ok';
        $('ind-tag').className = `tag ${com === 'ok' ? 'live' : com === 'aguardando' ? 'warn' : 'alarm'}`;
        $('ind-tag').textContent = com === 'ok' ? 'Ao vivo' : com === 'aguardando' ? 'Sem leitura' : velho && state.status.telemetria.timestamp !== null ? 'Valor antigo' : 'Sem comunicação';

        const grandes = [['nivel', 'Nível (PV)'], ['setpoint', 'Setpoint (SP)'], ['valvula', 'Saída (CV)']].filter(([c]) => p.variaveis[c].destaque);
        $('ro-main').className = `ro-main${velho ? ' is-stale' : ''}`;
        $('ro-main').innerHTML = grandes.map(([c, padrao]) =>
            `<div class="ro ro-${c}"><dt class="eyebrow">${esc(rotuloVar(c, padrao))}</dt><dd><span class="num ro-v" data-ro="${c}">${textoValor(c)}</span><span class="ro-un">${unidade(c)}</span></dd></div>`).join('');

        const extra = [['modoManual', 'Modo de operação'], ['kp', 'Kp'], ['ti', 'Ti'], ['td', 'Td'], ['valvulaManual', 'Abertura manual']].filter(([c]) => p.variaveis[c].destaque);
        const ult = idadeAtualSeg();
        $('ro-list').className = `ro-list${velho ? ' is-stale' : ''}`;
        $('ro-list').innerHTML = extra.map(([c, padrao]) =>
            `<div><dt class="eyebrow">${esc(rotuloVar(c, padrao))}</dt><dd class="num">${textoValor(c)}${c === 'modoManual' ? '' : ` <span class="muted">${unidade(c)}</span>`}</dd></div>`).join('')
            + `<div><dt class="eyebrow">Comunicação</dt><dd>${{ ok: '<span class="tag live">Recebendo</span>', aguardando: '<span class="tag warn">Aguardando telemetria</span>', sem_clp: '<span class="tag alarm">Sem comunicação com o CLP</span>', servidor_off: '<span class="tag alarm">Servidor desconectado</span>' }[com]}</dd></div>`
            + `<div><dt class="eyebrow">Última leitura válida</dt><dd class="num">${ult === null ? '—' : `${hora(Date.now() - ult * 1000)} <span class="muted">(${idade(ult)})</span>`}</dd></div>`;
    }

    // ---------- Tendência (memória desta aba) ----------
    function desenhar() {
        if (!chart || pausado) return;
        const agora = Date.now();
        const desde = agora - janelaMin * 60_000;
        const a = state.amostras.filter((s) => s.t >= desde);
        atualizar(chart, {
            pv: a.map((s) => ({ x: s.t, y: s.pv })),
            sp: a.map((s) => ({ x: s.t, y: s.sp })),
            cv: a.map((s) => ({ x: s.t, y: s.cv })),
            xmin: desde, xmax: agora
        });
        const n = a.length;
        $('tend-nota').textContent = `Últimos ${janelaMin} min · ${n} leituras reais recebidas nesta aba (memória do navegador, até 60 min). O histórico persistido no banco está na aba Histórico.`
            + (n === 0 ? ' Nenhuma leitura neste período.' : '');
    }

    // ---------- Alarmes ----------
    function pintarAlarmes() {
        const com = comunicacao();
        const t = state.status.telemetria;
        const ok = com === 'ok';
        const linhas = [
            ['baixo', `Nível baixo (abaixo de ${num(lim.nivelBaixo, 0)} %)`, ok && t.leds.low],
            ['normal', `Nível normal (${num(lim.nivelBaixo, 0)}–${num(lim.nivelAlto, 0)} %)`, ok && t.leds.normal],
            ['alto', `Nível alto (acima de ${num(lim.nivelAlto, 0)} %)`, ok && t.leds.high],
            ['com', 'Comunicação com o CLP', com === 'sem_clp' || com === 'servidor_off']
        ];
        $('alm-lista').innerHTML = linhas.map(([k, rot, ativo]) => {
            let estado;
            if (k === 'normal') estado = ok ? (ativo ? '<span class="tag ok">Faixa atual</span>' : '<span class="tag off">Fora da faixa</span>') : '<span class="tag off">Sem leitura</span>';
            else if (k === 'com') estado = { ok: '<span class="tag live">OK</span>', aguardando: '<span class="tag warn">Aguardando</span>', sem_clp: '<span class="tag alarm">Interrompida</span>', servidor_off: '<span class="tag alarm">Servidor off</span>' }[com];
            else estado = !ok ? '<span class="tag off">Sem leitura</span>' : ativo ? '<span class="tag alarm">ATIVO</span>' : '<span class="tag off">Inativo</span>';
            return `<li><span>${esc(rot)}</span>${estado}</li>`;
        }).join('');
        const pend = Object.values(state.alarmes).some((a) => a.ativo && !a.ack);
        $('btn-ack').disabled = !pend;
    }

    // ---------- Resumo de operação ----------
    let eventosServidor = null;
    async function carregarEventos() {
        if (!state.config?.historico?.habilitado) { eventosServidor = null; pintarEventos(); return; }
        const de = new Date(Date.now() - 6 * 3600_000).toISOString();
        const r = await api.get(`/api/eventos?de=${encodeURIComponent(de)}&limite=200`, 6000);
        eventosServidor = r.ok ? r.data.eventos : undefined;
        pintarEventos();
    }
    function pintarEventos() {
        const lista = $('ev-lista'), vazio = $('ev-vazio');
        let itens;
        if (Array.isArray(eventosServidor)) {
            $('res-fonte').textContent = 'Banco · últimas 6 h';
            itens = eventosServidor.slice(-8).reverse().map((e) => ({ t: new Date(e.t).getTime(), titulo: rotuloEvento(e.tipo), detalhe: e.detalhe, sev: sevEvento(e.tipo) }));
        } else {
            $('res-fonte').textContent = 'Desta aba';
            itens = state.eventos.slice(0, 8).map((e) => ({ t: e.t, titulo: e.texto, detalhe: e.estado, sev: e.sev === 'ok' ? 'ok' : 'alarm' }));
        }
        lista.innerHTML = itens.map((e) => `<li class="ev ${e.sev}"><span class="num ev-h">${hora(e.t)}</span><span><strong class="ev-t">${esc(e.titulo)}</strong>${e.detalhe ? `<br><span class="muted">${esc(e.detalhe)}</span>` : ''}</span></li>`).join('');
        vazio.hidden = itens.length > 0;
        vazio.textContent = eventosServidor === undefined
            ? 'Não foi possível consultar os eventos do banco agora.'
            : 'Nenhum evento recente. Alarmes, perda e retorno de comunicação e alterações lidas do CLP aparecem aqui.';
    }

    // ---------- Visibilidade por preferência ----------
    function aplicarPrefs() {
        const ind = prefs().indicadores;
        $('sec-tendencia').hidden = !ind.tendencia;
        $('sec-alarmes').hidden = !ind.alarmes;
        $('sec-resumo').hidden = !ind.resumo;
        raiz.querySelector('.ov-bottom').hidden = !ind.resumo;
        pintarIndicadores();
        pintarTanque();
    }

    // ---------- Ligações ----------
    $('sel-janela').addEventListener('change', (e) => { janelaMin = Number(e.target.value); desenhar(); });
    $('chk-cv').addEventListener('change', (e) => { mostrarCv = e.target.checked; if (chart) { chart.data.datasets[2].hidden = !mostrarCv; chart.update('none'); } });
    $('btn-pausa').addEventListener('click', (e) => { pausado = !pausado; e.currentTarget.textContent = pausado ? 'Retomar' : 'Pausar'; e.currentTarget.setAttribute('aria-pressed', String(pausado)); if (!pausado) desenhar(); });
    $('btn-ack').addEventListener('click', () => reconhecerAlarmes());
    $('btn-csv').addEventListener('click', () => {
        if (!state.amostras.length) return;
        const linhas = ['hora_iso,nivel_pct,setpoint_pct,saida_valvula_pct', ...state.amostras.map((s) => `${new Date(s.t).toISOString()},${csvNum(s.pv)},${csvNum(s.sp)},${csvNum(s.cv)}`)];
        baixarArquivo(`tq101_sessao_${carimboArquivo()}.csv`, linhas.join('\n'));
    });

    const offs = [
        on('status', () => { pintarTanque(); pintarIndicadores(); pintarAlarmes(); desenhar(); }),
        on('conexao', () => { pintarTanque(); pintarIndicadores(); pintarAlarmes(); }),
        on('tick', () => { pintarTanque(); pintarIndicadores(); pintarAlarmes(); desenhar(); }),
        on('alarmes', () => { pintarAlarmes(); if (!Array.isArray(eventosServidor)) pintarEventos(); else carregarEventos(); }),
        on('eventos', () => { if (!Array.isArray(eventosServidor)) pintarEventos(); }),
        on('config', () => { pintarTanque(); carregarEventos(); }),
        aoMudar(aplicarPrefs)
    ];
    const aoTema = () => aplicarTema(chart);
    window.addEventListener('smartplc:tema', aoTema);
    const timerEventos = setInterval(carregarEventos, 60_000);

    aplicarPrefs();
    pintarAlarmes();
    pintarEventos();
    desenhar();
    carregarEventos();

    return () => {
        offs.forEach((f) => f());
        window.removeEventListener('smartplc:tema', aoTema);
        clearInterval(timerEventos);
        destruir(chart);
        chart = null;
    };
}
