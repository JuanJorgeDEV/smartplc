// Laboratório SmartPLC: experimentos guiados sobre a telemetria REAL da malha TQ-101.
// Nada aqui simula o processo: a sessão é gravada no servidor com os valores recebidos do CLP, e o resumo vem das amostras persistidas.
import { state, on, comunicacao, idadeAtualSeg } from '../store.js';
import { num, hora, dataHora, duracao, esc } from '../format.js';
import { termo } from '../glossary.js';
import { criarTendencia, atualizar, aplicarTema, destruir } from '../charts.js';
import { aviso, confirmar } from '../ui.js';
import { rotuloEvento } from './overview.js';
import * as api from '../api.js';

export const titulo = 'Laboratório';

const TIPOS = {
    respostaSetpoint: {
        numero: '1', nome: 'Resposta ao setpoint',
        objetivo: 'Observar como o nível do tanque reage quando o setpoint é alterado.',
        intro: `O ${termo('sp', 'setpoint')} é o nível que desejamos atingir. Observe como a ${termo('pv', 'PV')} se comporta quando o objetivo muda.`,
        passos: ['Preparação', 'Iniciar', 'Alterar o setpoint', 'Observar', 'Encerrar', 'Revisar'],
        acao: 'Altere o setpoint'
    },
    automaticoManual: {
        numero: '2', nome: 'Automático versus manual',
        objetivo: 'Compreender a diferença entre o controle automático e a operação manual.',
        intro: `No modo ${termo('automatico', 'automático')}, o ${termo('pid', 'PID')} decide a abertura da válvula (${termo('cv', 'CV')}). No ${termo('manual', 'manual')}, uma pessoa a define e a ${termo('pv', 'PV')} deixa de ser regulada.`,
        passos: ['Preparação', 'Iniciar', 'Mudar o modo', 'Observar', 'Encerrar', 'Revisar'],
        acao: 'Mude o modo'
    }
};

const rotuloStatus = { ativa: ['warn', 'Em andamento'], encerrada: ['ok', 'Encerrada'], cancelada: ['off', 'Cancelada'] };

export async function montar(raiz, ctx) {
    const lab = state.config?.laboratorio;
    if (!lab?.habilitado) {
        raiz.innerHTML = `<div class="vazio"><p class="eyebrow">Laboratório SmartPLC</p><h2 class="display">Laboratório indisponível</h2>
          <p class="lead">As sessões de laboratório são gravadas no banco de dados e o resumo usa o histórico persistido. Sem banco configurado, o servidor não consegue registrar experimentos.</p>
          <p class="note">Nada é simulado para preencher o vazio. Para ativar: configure <span class="mono">ConnectionStrings:SmartPlc</span> e aplique o esquema (<span class="mono">docs/deploy.md</span>).</p></div>`;
        return () => {};
    }

    raiz.innerHTML = `
<div class="lab">
  <header class="lab-cab">
    <p class="eyebrow">Bancada digital · TQ-101</p>
    <h2 class="display lab-titulo">Laboratório SmartPLC</h2>
    <p class="lead">Experimente, observe e compreenda o comportamento da malha.</p>
  </header>
  <div class="lab-grid">
    <nav class="lab-lista" aria-label="Experimentos">
      <p class="eyebrow">Experimentos</p>
      <ul class="exp-tipos" id="exp-tipos"></ul>
      <p class="eyebrow lab-sub">Sessões anteriores</p>
      <ul class="sessoes-lista" id="sessoes"></ul>
      <p class="note" id="sessoes-vazio" hidden>Nenhuma sessão registrada ainda.</p>
    </nav>
    <section class="lab-palco" id="palco" aria-live="polite"></section>
  </div>
</div>`;

    const $ = (id) => raiz.querySelector(`#${id}`);
    const est = { tipo: 'respostaSetpoint', vista: 'carregando', sessao: null, detalhe: null, lista: [], chart: null, erro: null, ocupado: false, seq: 0 };
    let timerDetalhe = null;

    // ---------- Dados ----------
    async function carregarLista() {
        const r = await api.get('/api/experimentos?limite=20', 8000);
        if (r.ok) est.lista = r.data.sessoes; else est.erro = r.status === 503 ? 'Laboratório indisponível: banco de dados fora do ar.' : `Falha ao listar sessões: ${r.erro}.`;
        pintarLista();
        return r.ok;
    }

    async function carregarDetalhe(id) {
        const r = await api.get(`/api/experimentos/${id}`, 15000);
        if (r.ok) est.detalhe = r.data;
        return r;
    }

    // ---------- Lista lateral ----------
    function pintarLista() {
        $('exp-tipos').innerHTML = Object.entries(TIPOS).map(([k, t]) =>
            `<li><button type="button" class="exp-tipo" data-tipo="${k}" aria-pressed="${est.tipo === k}" ${est.vista === 'ativa' ? 'disabled' : ''}><span class="idx">${t.numero}</span><span><strong>${t.nome}</strong><br><span class="small">${t.objetivo}</span></span></button></li>`).join('');
        $('sessoes').innerHTML = est.lista.map((s) => {
            const dur = s.fimEm ? (new Date(s.fimEm) - new Date(s.inicioEm)) / 1000 : (Date.now() - new Date(s.inicioEm)) / 1000;
            const [tom, rot] = rotuloStatus[s.status] ?? ['off', s.status];
            return `<li class="sessao${est.sessao?.id === s.id ? ' atual' : ''}"><div class="sessao-topo"><strong>${esc(s.nome)}</strong></div>
              <div class="sessao-meta"><span class="tag ${tom}">${rot}</span><span class="small">${esc(s.tipoRotulo)}</span></div>
              <div class="sessao-meta num"><span>${dataHora(s.inicioEm)}</span><span>${duracao(dur)}</span></div>
              <button type="button" class="btn small" data-abrir="${s.id}" aria-label="Abrir a sessão ${esc(s.nome)}">Abrir</button></li>`;
        }).join('');
        $('sessoes-vazio').hidden = est.lista.length > 0;
        raiz.querySelectorAll('.exp-tipo').forEach((b) => b.addEventListener('click', () => { est.tipo = b.dataset.tipo; if (est.vista !== 'ativa') { est.vista = 'preparar'; pintarPalco(); } pintarLista(); }));
        raiz.querySelectorAll('[data-abrir]').forEach((b) => b.addEventListener('click', () => abrir(b.dataset.abrir)));
    }

    async function abrir(id) {
        const minha = ++est.seq;
        est.vista = 'carregando'; pintarPalco();
        const r = await carregarDetalhe(id);
        if (minha !== est.seq) return;
        if (!r.ok) { est.erro = r.erro; est.vista = 'preparar'; pintarPalco(); return; }
        est.sessao = r.data.sessao;
        est.tipo = r.data.sessao.tipo;
        est.vista = r.data.sessao.status === 'ativa' ? 'ativa' : 'revisar';
        pintarLista();
        pintarPalco();
    }

    // ---------- Palco ----------
    function limparGrafico() { destruir(est.chart); est.chart = null; clearInterval(timerDetalhe); timerDetalhe = null; }

    function passoAtual(mudou) { return est.vista === 'preparar' ? 0 : est.vista === 'ativa' ? (mudou ? 3 : 2) : 5; }
    function passosHtml(tipo, atual) {
        return `<ol class="lab-passos" aria-label="Etapas do experimento">${TIPOS[tipo].passos.map((p, i) =>
            `<li class="${i < atual ? 'feito' : i === atual ? 'atual' : ''}" ${i === atual ? 'aria-current="step"' : ''}><span class="idx">${i + 1}</span>${p}</li>`).join('')}</ol>`;
    }

    function desligarOuvintes() { const p = $('palco'); (p?._ouvintes ?? []).forEach((f) => f()); if (p) p._ouvintes = []; }

    function pintarPalco() {
        limparGrafico();
        desligarOuvintes();
        const palco = $('palco');
        if (est.vista === 'carregando') { palco.innerHTML = '<p class="note">Carregando…</p>'; return; }
        if (est.vista === 'preparar') return palcoPreparar(palco);
        if (est.vista === 'ativa') return palcoAtiva(palco);
        return palcoRevisar(palco);
    }

    // ----- 1) Preparar -----
    function palcoPreparar(palco) {
        const t = TIPOS[est.tipo];
        palco.innerHTML = `
          ${passosHtml(est.tipo, 0)}
          <h3 class="title">${t.numero} · ${t.nome}</h3>
          <p class="lead">${t.intro}</p>
          <dl class="lab-ro" id="lab-ro"></dl>
          <div id="lab-aviso"></div>
          <form class="lab-iniciar" id="f-iniciar" novalidate>
            <div class="field"><label for="in-nome">Nome da sessão (opcional)</label><input class="input" id="in-nome" maxlength="80" placeholder="${esc(t.nome)} — data e hora" autocomplete="off"></div>
            <button type="submit" class="btn primary" id="b-iniciar">Iniciar experimento</button>
          </form>
          ${est.erro ? `<p class="error" role="alert">${esc(est.erro)}</p>` : ''}
          <details class="saiba"><summary>O que será registrado</summary>
            <p class="note">Ao iniciar, o servidor grava o instante, o tipo e os valores de PV, SP, CV, modo e estado da comunicação <em>recebidos do CLP naquele momento</em>. A telemetria ao longo da sessão não é copiada: o resumo usa as amostras e eventos já persistidos no histórico.</p></details>`;

        const ro = $('lab-ro');
        const aviso_ = $('lab-aviso');
        const pintar = () => {
            const com = comunicacao();
            const tem = state.status.telemetria.timestamp !== null;
            const c = state.status.controle, tl = state.status.telemetria;
            ro.className = com === 'ok' ? 'lab-ro' : 'lab-ro is-stale';
            ro.innerHTML = `<div><dt class="eyebrow">${termo('pv', 'PV')} nível</dt><dd class="num">${tem ? num(tl.nivel, 1) : '—'} <span class="muted">%</span></dd></div>
              <div><dt class="eyebrow">${termo('sp', 'SP')} setpoint</dt><dd class="num">${tem ? num(c.setpoint, 1) : '—'} <span class="muted">%</span></dd></div>
              <div><dt class="eyebrow">${termo('cv', 'CV')} válvula</dt><dd class="num">${tem ? num(tl.valvula, 1) : '—'} <span class="muted">%</span></dd></div>
              <div><dt class="eyebrow">Modo</dt><dd class="num">${tem ? (c.modoManual ? 'Manual' : 'Automático') : '—'}</dd></div>
              <div><dt class="eyebrow">Comunicação</dt><dd>${{ ok: '<span class="tag live">Recebendo</span>', aguardando: '<span class="tag warn">Aguardando</span>', sem_clp: '<span class="tag alarm">Sem CLP</span>', servidor_off: '<span class="tag alarm">Servidor off</span>' }[com]}</dd></div>`;
            let msg = '';
            if (com !== 'ok') msg = `<p class="banner warn" role="status"><strong>Sem telemetria válida do CLP.</strong> Conecte o Node-RED (veja a aba Integrações) e aguarde a primeira leitura. Nenhum valor é simulado.</p>`;
            else if (!lab.escrita) msg = `<p class="banner info" role="status">Neste ambiente a criação de sessões está desabilitada (somente consulta).</p>`;
            else msg = observacaoControle();
            aviso_.innerHTML = msg;
            $('b-iniciar').disabled = com !== 'ok' || !lab.escrita || est.ocupado;
        };
        pintar();
        palco._ouvintes = [on('status', pintar), on('conexao', pintar), on('tick', pintar)];
        $('f-iniciar').addEventListener('submit', async (e) => {
            e.preventDefault();
            if (est.ocupado) return;
            est.ocupado = true; est.erro = null; pintar();
            const corpo = { tipo: est.tipo }; const nome = $('in-nome').value.trim(); if (nome) corpo.nome = nome;
            const r = await api.post('/api/experimentos', corpo, 10000);
            est.ocupado = false;
            if (!r.ok) {
                est.erro = r.status === 409 && r.data?.sessaoAtiva ? 'Já existe um experimento em andamento. Abrindo-o…' : r.erro;
                if (r.status === 409 && r.data?.sessaoAtiva) { await carregarLista(); abrir(r.data.sessaoAtiva); return; }
                pintarPalco(); return;
            }
            aviso('ok', 'Experimento iniciado. Os valores de partida foram registrados.');
            await carregarLista();
            abrir(r.data.sessao.id);
        });
    }

    function observacaoControle() {
        const c = state.config?.controle;
        if (c?.habilitado && !c.exigeChaveDeOperador)
            return `<p class="banner info" role="status">Controle habilitado: depois de iniciar, faça a alteração pela aba <a href="#/controle">Controle</a>. O Laboratório não cria outro caminho de comando.</p>`;
        return `<p class="banner info" role="status"><strong>Modo de observação.</strong> Os comandos estão desativados (ou exigem autenticação). Depois de iniciar, faça a alteração diretamente no TIA Portal ou no Node-RED: o SmartPLC a identificará quando o CLP a reportar.</p>`;
    }

    // ----- 2) Ativa -----
    function palcoAtiva(palco) {
        const s = est.sessao;
        const t = TIPOS[s.tipo];
        palco.innerHTML = `
          <div id="passos"></div>
          <div class="lab-cab2"><h3 class="title">${esc(s.nome)}</h3><span class="tag warn">Em andamento</span></div>
          <p class="note">${esc(t.numero)} · ${esc(t.nome)} · iniciada em ${dataHora(s.inicioEm)} · PV ${num(s.nivelInicial, 1)} %, SP ${num(s.setpointInicial, 1)} %, CV ${num(s.valvulaInicial, 1)} %, ${s.modoManualInicial ? 'manual' : 'automático'} no início.</p>
          ${observacaoControle()}
          <dl class="lab-ro" id="lab-vivo"></dl>
          <p class="deteccao" id="deteccao" role="status"></p>
          <div class="chart-box chart-lab"><canvas id="cv-lab"></canvas></div>
          <ul class="legenda" aria-label="Legenda"><li><i class="sw pv"></i>PV nível</li><li><i class="sw sp"></i>SP setpoint</li><li><i class="sw cv"></i>CV válvula</li></ul>
          <div class="field"><label for="in-obs">Observações (opcional, guardadas ao encerrar)</label><textarea class="input" id="in-obs" rows="2" maxlength="500"></textarea></div>
          <div class="lab-acoes">
            <button type="button" class="btn primary" id="b-encerrar">Encerrar experimento</button>
            <button type="button" class="btn danger quiet" id="b-cancelar">Cancelar sessão</button>
          </div>
          <p class="error" id="e-fim" role="alert"></p>`;

        est.chart = criarTendencia($('cv-lab'), { cv: true, ariaLabel: 'Trajetória de PV, SP e CV durante o experimento' });
        const inicio = new Date(s.inicioEm).getTime();

        async function atualizarServidor() { const r = await carregarDetalhe(s.id); if (r.ok) desenhar(); }

        function marcadores() {
            const m = [];
            const eventos = est.detalhe?.eventos ?? [];
            for (const e of eventos) {
                if (e.tipo === 'SetpointAlterado') m.push({ x: new Date(e.t).getTime(), rotulo: 'SP alterado', cor: 'texto', linha: 0 });
                else if (e.tipo === 'ModoAlterado') m.push({ x: new Date(e.t).getTime(), rotulo: 'Modo alterado', cor: 'aviso', linha: 1 });
                else if (/Perdida/.test(e.tipo)) m.push({ x: new Date(e.t).getTime(), rotulo: 'Comunicação perdida', cor: 'alarme', linha: 2 });
            }
            return m;
        }

        function mudancaLocal() {
            const live = state.amostras.filter((a) => a.t >= inicio);
            for (let i = 1; i < live.length; i++) {
                if (s.tipo === 'respostaSetpoint' && Math.abs(live[i].sp - live[i - 1].sp) >= 0.05) return { t: live[i].t, de: live[i - 1].sp, para: live[i].sp };
                if (s.tipo === 'automaticoManual' && live[i].modo !== undefined && live[i - 1].modo !== undefined && live[i].modo !== live[i - 1].modo) return { t: live[i].t, modo: live[i].modo };
            }
            return null;
        }

        function desenhar() {
            if (!est.chart) return;
            const live = state.amostras.filter((a) => a.t >= inicio);
            const primeiroLive = live[0]?.t ?? Infinity;
            const base = (est.detalhe?.serie ?? []).map((p) => ({ t: new Date(p.t).getTime(), pv: p.pv, sp: p.sp, cv: p.cv })).filter((p) => p.t < primeiroLive);
            const pontos = [...base, ...live];
            const marc = marcadores();
            const ml = mudancaLocal();
            if (ml && !marc.some((x) => Math.abs(x.x - ml.t) < 15_000)) marc.push({ x: ml.t, rotulo: ml.modo !== undefined ? 'Modo alterado' : 'SP alterado', cor: 'texto', linha: 0 });
            atualizar(est.chart, {
                pv: pontos.map((p) => ({ x: p.t, y: p.pv })),
                sp: pontos.filter((p) => p.sp !== null && p.sp !== undefined).map((p) => ({ x: p.t, y: p.sp })),
                cv: pontos.map((p) => ({ x: p.t, y: p.cv })),
                xmin: inicio, xmax: Math.max(Date.now(), inicio + 30_000), marcadores: marc
            });
            const ev = (est.detalhe?.eventos ?? []).find((e) => e.tipo === (s.tipo === 'respostaSetpoint' ? 'SetpointAlterado' : 'ModoAlterado'));
            const det = $('deteccao');
            if (ev) det.innerHTML = `<span class="tag ok">Detectado</span> ${s.tipo === 'respostaSetpoint' ? 'Mudança de setpoint' : 'Mudança de modo'} registrada às <span class="num">${hora(ev.t)}</span> (${esc(ev.detalhe ?? '')}). Este é o instante em que o <strong>CLP reportou</strong> a mudança, não o do pedido.`;
            else if (ml) det.innerHTML = `<span class="tag warn">Detectado na telemetria</span> às <span class="num">${hora(ml.t)}</span>. Aguardando o registro no banco.`;
            else det.innerHTML = `<span class="tag off">Aguardando</span> ${esc(t.acao)} (aba Controle, TIA Portal ou Node-RED). A marca vertical aparece quando o CLP reportar a mudança.`;
            $('passos').innerHTML = passosHtml(s.tipo, passoAtual(!!ev || !!ml));
        }

        function vivo() {
            const com = comunicacao();
            const tl = state.status.telemetria, c = state.status.controle;
            const live = state.amostras.filter((a) => a.t >= inicio);
            const pvs = live.map((a) => a.pv);
            const tempoDecorrido = (Date.now() - inicio) / 1000;
            $('lab-vivo').className = com === 'ok' ? 'lab-ro' : 'lab-ro is-stale';
            $('lab-vivo').innerHTML = `<div><dt class="eyebrow">Tempo decorrido</dt><dd class="num">${duracao(tempoDecorrido)}</dd></div>
              <div><dt class="eyebrow">PV atual</dt><dd class="num">${num(tl.nivel, 1)} <span class="muted">%</span></dd></div>
              <div><dt class="eyebrow">SP atual</dt><dd class="num">${num(c.setpoint, 1)} <span class="muted">%</span></dd></div>
              <div><dt class="eyebrow">CV atual</dt><dd class="num">${num(tl.valvula, 1)} <span class="muted">%</span></dd></div>
              <div><dt class="eyebrow">Modo</dt><dd class="num">${c.modoManual ? 'Manual' : 'Automático'}</dd></div>
              <div><dt class="eyebrow">PV mín · máx (desde o início)</dt><dd class="num">${pvs.length ? `${num(Math.min(...pvs), 1)} · ${num(Math.max(...pvs), 1)}` : '—'}</dd></div>
              <div><dt class="eyebrow">Comunicação</dt><dd>${{ ok: '<span class="tag live">Recebendo</span>', aguardando: '<span class="tag warn">Aguardando</span>', sem_clp: '<span class="tag alarm">Sem CLP</span>', servidor_off: '<span class="tag alarm">Servidor off</span>' }[com]}</dd></div>`;
        }

        palco._ouvintes = [on('status', () => { vivo(); desenhar(); }), on('tick', () => { vivo(); desenhar(); })];
        atualizarServidor();
        timerDetalhe = setInterval(atualizarServidor, 10_000);
        vivo(); desenhar();

        const finalizar = async (acao) => {
            if (est.ocupado) return;
            const ok = await confirmar({
                titulo: acao === 'encerrar' ? 'Encerrar o experimento?' : 'Cancelar a sessão?',
                corpo: acao === 'encerrar' ? 'O servidor registrará os valores finais recebidos do CLP e o resumo será calculado com as amostras do período.' : 'A sessão será marcada como cancelada. Os dados do histórico permanecem, mas nenhum resumo é apresentado como resultado.',
                confirmarTexto: acao === 'encerrar' ? 'Encerrar' : 'Cancelar sessão', cancelarTexto: 'Voltar'
            });
            if (!ok) return;
            est.ocupado = true;
            const obs = $('in-obs').value.trim();
            const r = await api.post(`/api/experimentos/${s.id}/${acao}`, obs ? { observacoes: obs } : {}, 10000);
            est.ocupado = false;
            if (!r.ok) { $('e-fim').textContent = `Não foi possível ${acao === 'encerrar' ? 'encerrar' : 'cancelar'}: ${r.erro}.`; return; }
            aviso('ok', acao === 'encerrar' ? 'Experimento encerrado.' : 'Sessão cancelada.');
            await carregarLista();
            abrir(s.id);
        };
        $('b-encerrar').addEventListener('click', () => finalizar('encerrar'));
        $('b-cancelar').addEventListener('click', () => finalizar('cancelar'));
    }

    // ----- 3) Revisar -----
    function palcoRevisar(palco) {
        const d = est.detalhe;
        if (!d || d.sessao.id !== est.sessao?.id) { palco.innerHTML = '<p class="note">Carregando resumo…</p>'; return; }
        const s = d.sessao, r = d.resumo, t = TIPOS[s.tipo];
        const [tom, rot] = rotuloStatus[s.status] ?? ['off', s.status];
        const cancelada = s.status === 'cancelada';
        const linha = (rotulo, valor) => `<div><dt class="eyebrow">${rotulo}</dt><dd class="num">${valor}</dd></div>`;
        const un = (v, c = 1) => (v === null || v === undefined ? '—' : `${num(v, c)} <span class="muted">%</span>`);

        let metricas = '';
        if (s.tipo === 'respostaSetpoint' && r.resposta) {
            const x = r.resposta;
            if (x.calculada) {
                metricas = `<h4 class="eyebrow met-t">Resposta ao degrau</h4>
                  <p class="note">Degrau de setpoint de <strong>${num(x.degrau.de, 1)} %</strong> para <strong>${num(x.degrau.para, 1)} %</strong> às ${hora(x.degrau.t)}; PV antes do degrau: ${num(x.pvAntesDoDegrau, 1)} %.</p>
                  <dl class="lab-ro">
                    ${linha(`${termo('t63', 'Tempo até 63 %')}`, x.segundosAteT63 === null ? '<span class="nd">não calculado</span>' : duracao(x.segundosAteT63))}
                    ${linha('Subida 10→90 %', x.segundosT10aT90 === null ? '<span class="nd">não calculado</span>' : duracao(x.segundosT10aT90))}
                    ${linha(`${termo('sobreelevacao', 'Sobre-elevação')}`, x.sobreelevacaoPercentual === null ? '<span class="nd">não calculada</span>' : `${num(x.sobreelevacaoPercentual, 1)} <span class="muted">% do degrau</span>`)}
                    ${linha(`${termo('estabilizacao', 'Estabilização')} (±2 %)`, x.segundosAcomodacao === null ? '<span class="nd">não estabilizou</span>' : duracao(x.segundosAcomodacao))}
                    ${linha('Resolução das amostras', `${num(x.resolucaoMedianaSegundos, 1)} <span class="muted">s (mediana)</span>`)}
                  </dl>
                  <ul class="leitura">${interpretar(x).map((l) => `<li>${l}</li>`).join('')}</ul>
                  ${(x.observacoes ?? []).length ? `<ul class="obs">${x.observacoes.map((o) => `<li>${esc(o)}</li>`).join('')}</ul>` : ''}`;
            } else {
                metricas = `<h4 class="eyebrow met-t">Resposta ao degrau</h4><p class="banner info" role="status"><strong>Métricas não calculadas.</strong> ${esc(x.motivo)}.</p>
                  <p class="note">O Laboratório prefere não mostrar um número enganoso quando os dados não sustentam o cálculo.</p>`;
            }
            metricas += `<details class="saiba"><summary>Como foi calculado</summary><p class="note">Sobre as amostras persistidas na janela da sessão (periódicas, por variação e por evento), com interpolação linear entre amostras vizinhas.
              Exige: degrau de setpoint ≥ 5 pontos; PV a ≥ 5 pontos do novo SP; ≥ 6 amostras após o degrau; mediana do intervalo entre amostras ≤ 10 s.
              Tempo até 63 % exige ≥ 3 amostras até o cruzamento; subida 10→90 % exige ≥ 5. Sobre-elevação só existe se a PV chegar à banda de ±2 % do degrau (mín. 0,5 ponto).
              Estabilização: após a última amostra fora da banda, com ≥ 3 amostras seguidas dentro dela. Considera apenas o primeiro degrau da sessão.</p></details>`;
        }
        if (s.tipo === 'automaticoManual') {
            const lin = (nome, m) => m ? `<tr><th scope="row">${nome}</th><td class="num">${duracao(m.segundos)}</td><td class="num">${m.pvMin === null ? '—' : num(m.pvMin, 1)} · ${m.pvMax === null ? '—' : num(m.pvMax, 1)}</td><td class="num">${m.cvMedio === null ? '—' : num(m.cvMedio, 1)} %</td></tr>` : `<tr><th scope="row">${nome}</th><td colspan="3" class="muted">sem amostras neste modo</td></tr>`;
            metricas = `<h4 class="eyebrow met-t">Por modo de operação</h4>
              <div class="table-wrap"><table><thead><tr><th scope="col">Modo</th><th scope="col" class="num">Tempo</th><th scope="col" class="num">PV mín · máx (%)</th><th scope="col" class="num">CV médio</th></tr></thead><tbody>${lin('Automático', r.automatico)}${lin('Manual', r.manual)}</tbody></table></div>
              <p class="note">${r.mudancasDeModo.length ? `Mudanças de modo reportadas pelo CLP: ${r.mudancasDeModo.map((m) => `${hora(m.t)} → ${m.paraManual ? 'manual' : m.paraManual === false ? 'automático' : '?'}`).join(' · ')}.` : 'Nenhuma mudança de modo foi reportada pelo CLP durante a sessão.'}
              O Laboratório não alterna o modo sozinho. Em manual, a CV segue a abertura definida; em automático, o PID a calcula.</p>`;
        }

        palco.innerHTML = `
          ${passosHtml(s.tipo, 5)}
          <div class="lab-cab2"><h3 class="title">${esc(s.nome)}</h3><span class="tag ${tom}">${rot}</span></div>
          <p class="note">${esc(t.numero)} · ${esc(t.nome)} · ${dataHora(s.inicioEm)} → ${s.fimEm ? dataHora(s.fimEm) : '—'}</p>
          ${cancelada ? '<p class="banner info" role="status">Sessão cancelada: os números abaixo são apenas os dados brutos do período, não um resultado de experimento.</p>' : ''}
          <div class="chart-box chart-lab"><canvas id="cv-lab"></canvas></div>
          <ul class="legenda" aria-label="Legenda"><li><i class="sw pv"></i>PV nível</li><li><i class="sw sp"></i>SP setpoint</li><li><i class="sw cv"></i>CV válvula</li></ul>
          <h4 class="eyebrow met-t">Resumo</h4>
          <dl class="lab-ro">
            ${linha('Duração', duracao(r.duracaoSegundos))}
            ${linha('Nível inicial → final', `${un(r.pv.inicial)} → ${un(r.pv.final)}`)}
            ${linha('Setpoint inicial → final', `${un(r.sp.inicial)} → ${un(r.sp.final)}`)}
            ${linha('PV mínima · máxima', r.pv.min === null ? '—' : `${num(r.pv.min, 1)} · ${num(r.pv.max, 1)} <span class="muted">%</span>`)}
            ${linha('CV mínima · máxima', r.cv.min === null ? '—' : `${num(r.cv.min, 1)} · ${num(r.cv.max, 1)} <span class="muted">%</span>`)}
            ${linha('Amostras persistidas', `${num(r.amostras, 0)}${r.truncado ? ' <span class="tag warn">truncado</span>' : ''}`)}
          </dl>
          ${r.amostras === 0 ? '<p class="banner warn" role="status">Nenhuma amostra foi persistida neste período (o CLP pode ter ficado sem comunicar). Nenhum valor foi inventado.</p>' : ''}
          ${metricas}
          ${s.observacoes ? `<h4 class="eyebrow met-t">Observações</h4><p class="note">${esc(s.observacoes)}</p>` : ''}
          <h4 class="eyebrow met-t">Eventos do período</h4>
          <ol class="linha-tempo" id="ev-lab"></ol>
          <p class="note" id="ev-lab-vazio" hidden>Nenhum evento registrado neste período.</p>
          <div class="lab-acoes"><button type="button" class="btn" id="b-nova">Preparar nova sessão</button></div>`;

        const ev = d.eventos ?? [];
        $('ev-lab').innerHTML = ev.map((e) => `<li class="lt ${/Normalizado|Restabelecida/.test(e.tipo) ? 'ok' : /Alto|Baixo|Perdida/.test(e.tipo) ? 'alarm' : 'info'}"><span class="num lt-h">${hora(e.t)}</span><span class="lt-t"><strong>${esc(rotuloEvento(e.tipo))}</strong>${e.detalhe ? `<br><span class="muted">${esc(e.detalhe)}</span>` : ''}</span></li>`).join('');
        $('ev-lab-vazio').hidden = ev.length > 0;
        $('b-nova').addEventListener('click', () => { est.vista = 'preparar'; est.sessao = null; est.erro = null; pintarLista(); pintarPalco(); });

        est.chart = criarTendencia($('cv-lab'), { cv: true, ariaLabel: `Trajetória de PV, SP e CV da sessão ${s.nome}` });
        const marc = ev.filter((e) => ['SetpointAlterado', 'ModoAlterado'].includes(e.tipo) || /Perdida|Alto$|Baixo$/.test(e.tipo)).map((e, i) => ({
            x: new Date(e.t).getTime(), rotulo: e.tipo === 'SetpointAlterado' ? 'SP alterado' : e.tipo === 'ModoAlterado' ? 'Modo alterado' : rotuloEvento(e.tipo),
            cor: /Perdida|Alto$|Baixo$/.test(e.tipo) ? 'alarme' : 'texto', linha: i % 3
        }));
        atualizar(est.chart, {
            pv: d.serie.map((p) => ({ x: new Date(p.t).getTime(), y: p.pv })),
            sp: d.serie.filter((p) => p.sp !== null).map((p) => ({ x: new Date(p.t).getTime(), y: p.sp })),
            cv: d.serie.map((p) => ({ x: new Date(p.t).getTime(), y: p.cv })),
            xmin: new Date(d.janela.de).getTime(), xmax: new Date(d.janela.ate).getTime(), marcadores: marc
        });
    }

    /** Leitura em linguagem simples, apenas a partir das métricas realmente calculadas. */
    function interpretar(x) {
        const l = [];
        if (x.sobreelevacaoPercentual !== null) l.push(x.sobreelevacaoPercentual < 0.5
            ? 'A PV se aproximou do novo SP <strong>sem ultrapassá-lo</strong> (sem sobre-elevação).'
            : `A PV <strong>ultrapassou</strong> o novo SP em ${num(x.sobreelevacaoPercentual, 1)} % do degrau antes de assentar: é a sobre-elevação.`);
        if (x.segundosAteT63 !== null) l.push(`A PV percorreu 63 % do caminho até o novo SP em ${duracao(x.segundosAteT63)}. Em um sistema simples de primeira ordem, esse tempo equivale à constante de tempo.`);
        if (x.segundosAcomodacao !== null) l.push(`A PV passou a ficar dentro de ±2 % do degrau a partir de ${duracao(x.segundosAcomodacao)} após a mudança: é a estabilização.`);
        return l;
    }

    // ---------- Ligações / ciclo de vida ----------
    const aoTema = () => aplicarTema(est.chart);
    window.addEventListener('smartplc:tema', aoTema);

    await carregarLista();
    const ativa = est.lista.find((x) => x.status === 'ativa');
    const pedida = ctx?.params?.get('sessao');
    if (pedida) await abrir(pedida);
    else if (ativa) await abrir(ativa.id);
    else { est.vista = 'preparar'; pintarPalco(); }

    return () => {
        est.seq++;
        desligarOuvintes();
        window.removeEventListener('smartplc:tema', aoTema);
        limparGrafico();
    };
}
