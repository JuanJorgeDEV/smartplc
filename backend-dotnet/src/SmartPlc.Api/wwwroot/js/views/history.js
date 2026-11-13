// Histórico: consome /api/historico e /api/eventos (dados persistidos no PostgreSQL). Nunca usa a memória da aba como se fosse histórico.
import { state, on } from '../store.js';
import { num, dataHora, hora, duracao, esc, paraInputLocal, baixarArquivo, carimboArquivo, csvNum } from '../format.js';
import { criarTendencia, atualizar, aplicarTema, destruir } from '../charts.js';
import { rotuloEvento } from './overview.js';
import { termo } from '../glossary.js';
import * as api from '../api.js';

export const titulo = 'Histórico';

const PRESETS = { '1h': 1, '6h': 6, '24h': 24 };
const MOTIVO = { Initial: 'Ponto de partida', Periodic: 'Periódica', Change: 'Variação', Event: 'Evento' };
const GRUPOS = {
    todos: () => true,
    alarmes: (t) => /Nivel(Alto|Baixo)/.test(t),
    comunicacao: (t) => /Comunicacao/.test(t),
    parametros: (t) => /(Setpoint|Modo|Pid)Alterado/.test(t)
};

export async function montar(raiz) {
    const cfg = state.config?.historico;
    if (!cfg?.habilitado) {
        raiz.innerHTML = `<div class="vazio"><h2 class="title">Histórico indisponível</h2>
          <p class="lead">O servidor está sem banco de dados configurado, então nada é gravado nem consultado.</p>
          <p class="note">Para ativar: defina <span class="mono">ConnectionStrings:SmartPlc</span> (PostgreSQL) e aplique o esquema — passo a passo em <span class="mono">docs/deploy.md</span>. Enquanto isso, a aba Visão Geral mostra os últimos 60 minutos <em>desta aba do navegador</em>, que não são histórico persistido.</p></div>`;
        return () => {};
    }

    const maxH = cfg.periodoMaximoHoras ?? 72;
    const limite = cfg.limiteMaximo ?? 5000;

    raiz.innerHTML = `
<div class="hist">
  <div class="sec-head"><h2 class="eyebrow">Consulta · dados persistidos no banco</h2></div>
  <div class="hist-tools">
    <div class="seg" role="radiogroup" aria-label="Período" id="seg-per">
      <button type="button" role="radio" data-p="1h" aria-checked="true">Última hora</button>
      <button type="button" role="radio" data-p="6h" aria-checked="false">6 horas</button>
      <button type="button" role="radio" data-p="24h" aria-checked="false">24 horas</button>
      <button type="button" role="radio" data-p="custom" aria-checked="false">Personalizado</button>
    </div>
    <form class="hist-custom" id="f-custom" hidden novalidate>
      <div class="field"><label for="in-de">De (hora local)</label><input class="input" id="in-de" type="datetime-local"></div>
      <div class="field"><label for="in-ate">Até (hora local)</label><input class="input" id="in-ate" type="datetime-local"></div>
      <button class="btn primary" type="submit">Consultar</button>
      <p class="error" id="e-custom" role="alert"></p>
    </form>
    <button type="button" class="btn small" id="b-atualizar">Atualizar</button>
  </div>

  <dl class="hist-info" id="info" aria-live="polite"></dl>

  <div class="sec-head">
    <h2 class="eyebrow">${termo('pv', 'PV')} e ${termo('sp', 'SP')} no período</h2>
    <div class="sec-tools">
      <ul class="legenda" aria-label="Legenda"><li><i class="sw pv"></i>PV nível</li><li><i class="sw sp"></i>SP setpoint</li><li><label class="check"><input type="checkbox" id="chk-cv"> <i class="sw cv"></i>MV válvula</label></li><li><label class="check"><input type="checkbox" id="chk-ev" checked> Marcar eventos</label></li></ul>
    </div>
  </div>
  <div class="chart-box"><canvas id="cv-hist"></canvas></div>
  <p class="note" id="msg-vazio" hidden></p>
  <div class="hist-acoes">
    <button type="button" class="btn" id="b-mais" hidden>Carregar mais amostras</button>
    <button type="button" class="btn" id="b-csv-banco">Exportar período carregado (banco)</button>
    <button type="button" class="btn" id="b-csv-aba">Exportar sessão atual (memória da aba)</button>
  </div>
  <p class="note">"Período carregado (banco)" exporta apenas as amostras persistidas já carregadas acima. "Sessão atual" exporta as leituras recebidas por esta aba (até 60 min), que não são histórico permanente.</p>

  <div class="sec-head"><h2 class="eyebrow">Eventos</h2>
    <div class="sec-tools"><label class="sr-only" for="sel-ev">Filtrar eventos</label>
      <select class="input sel-curto" id="sel-ev"><option value="todos">Todos</option><option value="alarmes">Alarmes</option><option value="comunicacao">Comunicação</option><option value="parametros">Setpoint, modo e PID</option></select></div></div>
  <ol class="linha-tempo" id="eventos"></ol>
  <p class="note" id="ev-vazio" hidden></p>
  <button type="button" class="btn small" id="b-mais-ev" hidden>Carregar mais eventos</button>
</div>`;

    const $ = (id) => raiz.querySelector(`#${id}`);
    let chart = criarTendencia($('cv-hist'), { cv: true, ariaLabel: 'Gráfico histórico de PV e SP do período consultado' });
    if (chart) chart.data.datasets[2].hidden = true;

    const est = { preset: '1h', de: null, ate: null, amostras: [], eventos: [], proxA: null, proxE: null, carregando: false, erro: null, filtro: 'todos', marcar: true, seq: 0 };

    // ---------- Consulta ----------
    async function consultar() {
        const minha = ++est.seq;
        est.carregando = true; est.erro = null; est.amostras = []; est.eventos = []; est.proxA = null; est.proxE = null;
        pintar();
        const q = { de: new Date(est.de).toISOString(), ate: new Date(est.ate).toISOString(), limite };
        const [a, e] = await Promise.all([api.get(`/api/historico${api.qs(q)}`, 20000), api.get(`/api/eventos${api.qs(q)}`, 20000)]);
        if (minha !== est.seq) return;
        est.carregando = false;
        if (!a.ok) { est.erro = a.status === 503 ? 'O banco de dados está indisponível agora. Tente novamente em instantes.' : `Não foi possível consultar o histórico: ${a.erro}.`; pintar(); return; }
        est.amostras = a.data.amostras;
        est.proxA = a.data.proximo;
        if (e.ok) { est.eventos = e.data.eventos; est.proxE = e.data.proximo; } else est.erro = `Eventos indisponíveis: ${e.erro}.`;
        pintar();
    }

    async function maisAmostras() {
        if (!est.proxA || est.carregando) return;
        est.carregando = true; pintar();
        const q = { de: new Date(est.de).toISOString(), ate: new Date(est.ate).toISOString(), limite, proximo: est.proxA };
        const r = await api.get(`/api/historico${api.qs(q)}`, 20000);
        est.carregando = false;
        if (r.ok) { est.amostras = est.amostras.concat(r.data.amostras); est.proxA = r.data.proximo; } else est.erro = `Falha ao carregar mais: ${r.erro}.`;
        pintar();
    }

    async function maisEventos() {
        if (!est.proxE) return;
        const q = { de: new Date(est.de).toISOString(), ate: new Date(est.ate).toISOString(), limite, proximo: est.proxE };
        const r = await api.get(`/api/eventos${api.qs(q)}`, 20000);
        if (r.ok) { est.eventos = est.eventos.concat(r.data.eventos); est.proxE = r.data.proximo; pintar(); }
    }

    function definirPeriodo(p) {
        est.preset = p;
        raiz.querySelectorAll('#seg-per button').forEach((b) => b.setAttribute('aria-checked', String(b.dataset.p === p)));
        $('f-custom').hidden = p !== 'custom';
        if (p === 'custom') {
            if (!$('in-de').value) { $('in-de').value = paraInputLocal(Date.now() - 3600_000); $('in-ate').value = paraInputLocal(Date.now()); }
            $('in-de').focus();
            return;
        }
        est.ate = Date.now();
        est.de = est.ate - PRESETS[p] * 3600_000;
        consultar();
    }

    // ---------- Apresentação ----------
    function pintar() {
        const n = est.amostras.length;
        const ts = est.amostras.map((s) => new Date(s.t).getTime());
        const motivos = est.amostras.reduce((m, s) => { m[s.motivo] = (m[s.motivo] ?? 0) + 1; return m; }, {});
        $('info').innerHTML = est.de === null ? '' : `
          <div><dt class="eyebrow">Período consultado</dt><dd class="num">${dataHora(est.de)} → ${dataHora(est.ate)}<br><span class="muted">${duracao((est.ate - est.de) / 1000)} · máx. ${maxH} h por consulta</span></dd></div>
          <div><dt class="eyebrow">Amostras retornadas</dt><dd class="num">${est.carregando && !n ? 'carregando…' : num(n, 0)}${est.proxA ? ' <span class="tag warn">há mais</span>' : ''}</dd></div>
          <div><dt class="eyebrow">Disponibilidade</dt><dd class="num">${n ? `${hora(Math.min(...ts))} → ${hora(Math.max(...ts))}` : 'sem registros'}<br><span class="muted">${Object.entries(motivos).map(([k, v]) => `${MOTIVO[k] ?? k}: ${v}`).join('<br>') || '—'}</span></dd></div>
          <div><dt class="eyebrow">Eventos</dt><dd class="num">${num(est.eventos.length, 0)}${est.proxE ? ' <span class="tag warn">há mais</span>' : ''}</dd></div>`;

        const vazio = $('msg-vazio');
        if (est.erro) { vazio.hidden = false; vazio.className = 'note alarm'; vazio.textContent = est.erro; }
        else if (!est.carregando && est.de !== null && n === 0) { vazio.hidden = false; vazio.className = 'note'; vazio.textContent = 'Nenhum registro neste período. O histórico grava amostras selecionadas (periódicas, por variação e por evento); com o CLP desconectado, ou com o banco recém-configurado, o período pode estar vazio.'; }
        else vazio.hidden = true;

        $('b-mais').hidden = !est.proxA;
        $('b-mais').disabled = est.carregando;
        $('b-mais').textContent = est.carregando ? 'Carregando…' : `Carregar mais amostras (próximas ${num(limite, 0)})`;
        $('b-csv-banco').disabled = n === 0;
        $('b-mais-ev').hidden = !est.proxE;

        desenhar();
        pintarEventos();
    }

    function desenhar() {
        if (!chart || est.de === null) return;
        const a = est.amostras;
        const marcas = est.marcar ? est.eventos.filter((e) => GRUPOS[est.filtro](e.tipo)).map((e, i, todos) => ({
            x: new Date(e.t).getTime(),
            rotulo: todos.length <= 8 ? rotuloEvento(e.tipo).split(' (')[0] : '',
            cor: /Alto|Baixo|Perdida/.test(e.tipo) && !/Normalizado/.test(e.tipo) ? 'alarme' : /Perdida|Restab/.test(e.tipo) ? 'aviso' : 'texto',
            linha: i % 3
        })) : [];
        atualizar(chart, {
            pv: a.map((s) => ({ x: new Date(s.t).getTime(), y: s.nivel })),
            sp: a.map((s) => ({ x: new Date(s.t).getTime(), y: s.setpoint })).filter((p) => p.y !== null),
            cv: a.map((s) => ({ x: new Date(s.t).getTime(), y: s.valvula })),
            xmin: est.de, xmax: est.ate, marcadores: marcas
        });
        const canvas = $('cv-hist');
        canvas.setAttribute('aria-label', `Gráfico histórico de PV e SP: ${a.length} amostras de ${dataHora(est.de)} a ${dataHora(est.ate)}.`);
    }

    function pintarEventos() {
        const lista = est.eventos.filter((e) => GRUPOS[est.filtro](e.tipo));
        $('eventos').innerHTML = lista.map((e) => {
            const tom = /Normalizado|Restabelecida/.test(e.tipo) ? 'ok' : /Alto|Baixo|Perdida/.test(e.tipo) ? 'alarm' : 'info';
            const valores = e.valorAnterior !== null && e.valorNovo !== null ? ` <span class="num muted">(${num(e.valorAnterior, 2)} → ${num(e.valorNovo, 2)})</span>` : '';
            return `<li class="lt ${tom}"><span class="num lt-h">${dataHora(e.t)}</span><span class="lt-t"><strong>${esc(rotuloEvento(e.tipo))}</strong>${valores}${e.detalhe ? `<br><span class="muted">${esc(e.detalhe)}</span>` : ''}</span></li>`;
        }).join('');
        const vazio = $('ev-vazio');
        vazio.hidden = lista.length > 0 || est.carregando || est.de === null;
        vazio.textContent = est.eventos.length ? 'Nenhum evento deste tipo no período.' : 'Nenhum evento neste período.';
        $('ev-vazio').className = 'note';
    }

    // ---------- Exportações ----------
    function exportarBanco() {
        const linhas = ['hora_iso,hora_aquisicao_iso,nivel_pct,setpoint_pct,saida_valvula_pct,modo_manual,motivo', ...est.amostras.map((s) =>
            `${s.t},${s.adquiridoEm ?? ''},${csvNum(s.nivel)},${csvNum(s.setpoint)},${csvNum(s.valvula)},${s.modoManual === null ? '' : s.modoManual},${s.motivo}`)];
        baixarArquivo(`tq101_historico_banco_${carimboArquivo()}.csv`, linhas.join('\n'));
    }
    function exportarSessao() {
        if (!state.amostras.length) return;
        const linhas = ['hora_iso,nivel_pct,setpoint_pct,saida_valvula_pct', ...state.amostras.map((s) => `${new Date(s.t).toISOString()},${csvNum(s.pv)},${csvNum(s.sp)},${csvNum(s.cv)}`)];
        baixarArquivo(`tq101_sessao_aba_${carimboArquivo()}.csv`, linhas.join('\n'));
    }

    // ---------- Ligações ----------
    raiz.querySelectorAll('#seg-per button').forEach((b) => b.addEventListener('click', () => definirPeriodo(b.dataset.p)));
    $('f-custom').addEventListener('submit', (e) => {
        e.preventDefault();
        const de = new Date($('in-de').value).getTime(), ate = new Date($('in-ate').value).getTime();
        let msg = '';
        if (!Number.isFinite(de) || !Number.isFinite(ate)) msg = 'Informe as duas datas.';
        else if (de >= ate) msg = 'A data inicial deve ser anterior à final.';
        else if ((ate - de) / 3600_000 > maxH) msg = `O período máximo por consulta é ${maxH} h.`;
        $('e-custom').textContent = msg;
        if (msg) return;
        est.de = de; est.ate = ate;
        consultar();
    });
    $('b-atualizar').addEventListener('click', () => {
        if (est.preset !== 'custom') { est.ate = Date.now(); est.de = est.ate - PRESETS[est.preset] * 3600_000; }
        if (est.de !== null) consultar();
    });
    $('b-mais').addEventListener('click', maisAmostras);
    $('b-mais-ev').addEventListener('click', maisEventos);
    $('b-csv-banco').addEventListener('click', exportarBanco);
    $('b-csv-aba').addEventListener('click', exportarSessao);
    $('chk-cv').addEventListener('change', (e) => { if (chart) { chart.data.datasets[2].hidden = !e.target.checked; chart.update('none'); } });
    $('chk-ev').addEventListener('change', (e) => { est.marcar = e.target.checked; desenhar(); });
    $('sel-ev').addEventListener('change', (e) => { est.filtro = e.target.value; desenhar(); pintarEventos(); });

    const aoTema = () => aplicarTema(chart);
    window.addEventListener('smartplc:tema', aoTema);
    const off = on('config', () => {});

    definirPeriodo('1h');

    return () => {
        est.seq++; // descarta respostas em andamento
        window.removeEventListener('smartplc:tema', aoTema);
        off();
        destruir(chart);
        chart = null;
    };
}
