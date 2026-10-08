// ==========================================================
// Supervisório TQ-101 — lógica do front-end
// Mantém o mesmo contrato de API do back-end original:
//   GET  /api/status
//   POST /api/controle   { setpoint, modoManual, valvulaManual, kp, ti, td }
//   evento 'telemetria_update' -> { telemetria, controle }
// Versão ASP.NET Core: o tempo real usa SignalR (hub /hubs/telemetria) em vez de Socket.IO.
// ==========================================================

const byId = (id) => document.getElementById(id);
const RETENCAO_MS = 60 * 60 * 1000; // guarda até 1h de histórico em memória

// ---------- Estado local ----------
let estado = {
    telemetria: { nivel: 0, valvula: 0, leds: { low: false, normal: true, high: false }, timestamp: null },
    controle: { setpoint: 0, modoManual: false, valvulaManual: 0, kp: 1, ti: 20, td: 0 }
};

let hub = null;
let hubConectado = false;
let ultimoRecebimento = null;
let historico = [];
let pausado = false;
let janelaSegundos = 300;

let alarmes = {
    alto: { ativo: false, ack: true },
    baixo: { ativo: false, ack: true },
    com: { ativo: false, ack: true }
};
let eventos = [];
let proximoIdEvento = 1;

let graficoNivel = null;
let graficoSaida = null;

// Mapa de cada campo editável -> ids no DOM e como ler o valor de referência do servidor
const camposConfig = {
    sp: { input: 'inSp', campo: 'campoSp', hint: 'hintSp', erro: 'erroSp', botao: 'btnSp' },
    cv: { input: 'inCv', campo: 'campoCv', hint: 'hintCv', erro: 'erroCv', botao: 'btnCv', espelho: 'rgCv' },
    kp: { input: 'inKp', campo: 'campoKp', hint: 'hintKp', erro: 'erroKp' },
    ti: { input: 'inTi', campo: 'campoTi', hint: 'hintTi', erro: 'erroTi' },
    td: { input: 'inTd', campo: 'campoTd', hint: 'hintTd', erro: 'erroTd' }
};

const specSp = { chave: 'sp', valor: () => estado.controle.setpoint, casas: 1, unidade: '%', min: 0, max: 100, rotulo: 'Setpoint' };
const specCv = { chave: 'cv', valor: () => estado.controle.valvulaManual, casas: 1, unidade: '%', min: 0, max: 100, rotulo: 'Válvula manual' };
const specKp = { chave: 'kp', valor: () => estado.controle.kp, casas: 2, unidade: '', min: 0, max: 1000, rotulo: 'Kp' };
const specTi = { chave: 'ti', valor: () => estado.controle.ti, casas: 1, unidade: 's', min: 0, max: 100000, rotulo: 'Ti' };
const specTd = { chave: 'td', valor: () => estado.controle.td, casas: 1, unidade: 's', min: 0, max: 100000, rotulo: 'Td' };

// ---------- Utilitários ----------
function clamp(v, min, max) { return Math.min(max, Math.max(min, v)); }

function nivelParaY(v) { return 400 - (clamp(v, 0, 100) / 100) * 300; }

function formatarHora(t) {
    const dt = t instanceof Date ? t : new Date(t);
    return dt.toLocaleTimeString('pt-BR', { hour12: false });
}

function escapeHtml(s) {
    return String(s).replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
}

function validar(valor, min, max) {
    if (valor === '' || valor === null || valor === undefined) return 'Informe um valor.';
    const n = Number(valor);
    if (Number.isNaN(n)) return 'Valor inválido.';
    if (n < min || n > max) return `Informe um valor entre ${min} e ${max}.`;
    return null;
}

function travar(ids) {
    ids.forEach((id) => {
        const el = byId(id);
        if (!el) return;
        el.disabled = true;
        if (el.tagName === 'BUTTON') el.setAttribute('aria-busy', 'true');
    });
}

function destravar(ids) {
    ids.forEach((id) => {
        const el = byId(id);
        if (!el) return;
        el.disabled = false;
        if (el.tagName === 'BUTTON') el.removeAttribute('aria-busy');
    });
}

function aviso(tipo, texto) {
    const container = byId('avisos');
    const el = document.createElement('div');
    el.className = `aviso ${tipo}`;
    el.textContent = texto;
    container.appendChild(el);
    setTimeout(() => el.remove(), 5000);
    while (container.children.length > 4) container.removeChild(container.firstChild);
}

// ---------- Comunicação com o back-end ----------
async function enviarControle(payload, { sucesso, falha } = {}) {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), 4000);
    try {
        const resp = await fetch('/api/controle', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload),
            signal: controller.signal
        });
        clearTimeout(timeoutId);
        if (resp.ok) {
            if (sucesso) sucesso();
            return true;
        }
        if (falha) falha('o servidor recusou o comando.');
        return false;
    } catch (erro) {
        clearTimeout(timeoutId);
        const msg = erro.name === 'AbortError'
            ? 'sem resposta do Node-RED (tempo esgotado).'
            : 'falha de rede ao contactar o servidor.';
        if (falha) falha(msg);
        return false;
    }
}

async function carregarEstadoInicial() {
    try {
        const resp = await fetch('/api/status');
        if (resp.ok) {
            const dados = await resp.json();
            registrarRecebimento(dados);
            aoTelemetria(dados);
        }
    } catch (erro) {
        // Sem problema: o hub tentará ligar-se a seguir.
    }
}

function marcarConectado() {
    hubConectado = true;
    byId('statusConexao').textContent = 'Conectado';
    byId('statusConexao').className = 'conexao online';
    definirAlarme('com', false, { desligar: 'Comunicação com o servidor restabelecida' });
}

function marcarDesconectado() {
    hubConectado = false;
    byId('statusConexao').textContent = 'Desconectado';
    byId('statusConexao').className = 'conexao offline';
    definirAlarme('com', true, { ligar: 'Comunicação com o servidor perdida (SignalR desconectado)' });
}

// O SignalR desiste de reconectar por padrão; aqui a tentativa é contínua, como no Socket.IO.
async function iniciarHub() {
    while (true) {
        try {
            await hub.start();
            marcarConectado();
            carregarEstadoInicial(); // recupera o que passou enquanto estava desconectado
            return;
        } catch (erro) {
            await new Promise((r) => setTimeout(r, 2000));
        }
    }
}

function conectarSocket() {
    hub = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/telemetria')
        .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(1000 * 2 ** ctx.previousRetryCount, 10000) })
        .build();

    hub.on('telemetria_update', (dados) => {
        registrarRecebimento(dados);
        aoTelemetria(dados);
    });
    hub.onreconnecting(marcarDesconectado);
    hub.onreconnected(() => { marcarConectado(); carregarEstadoInicial(); });
    hub.onclose(() => { marcarDesconectado(); iniciarHub(); });

    iniciarHub();
}

// Usa a idade informada pelo servidor (sem depender do relógio do navegador). Sem ela, cai no horário local.
// idadeSegundos === null significa que o CLP nunca enviou nada desde que o servidor subiu.
function registrarRecebimento(dados) {
    const tel = dados && dados.telemetria;
    if (tel && 'idadeSegundos' in tel) {
        ultimoRecebimento = (typeof tel.idadeSegundos === 'number') ? Date.now() - tel.idadeSegundos * 1000 : null;
    } else {
        ultimoRecebimento = Date.now();
    }
}

function verificarFrescor() {
    const el = byId('frescor');
    if (!ultimoRecebimento) {
        el.textContent = '—';
        document.body.dataset.dados = 'obsoleto';
        atualizarFluxoVisivel();
        if (hubConectado) {
            definirAlarme('com', true, {
                ligar: 'Aguardando a primeira leitura do CLP',
                desligar: 'Recebendo leituras do CLP normalmente'
            });
        }
        return;
    }

    const seg = Math.round((Date.now() - ultimoRecebimento) / 1000);
    el.textContent = seg < 1 ? 'agora' : `há ${seg} s`;

    const obsoleto = seg > 5;
    el.classList.toggle('atrasado', obsoleto);
    document.body.dataset.dados = obsoleto ? 'obsoleto' : 'atualizado';
    atualizarFluxoVisivel();

    if (hubConectado) {
        definirAlarme('com', obsoleto, {
            ligar: 'Sem novas leituras do CLP há mais de 5 s',
            desligar: 'Recebendo leituras do CLP normalmente'
        });
    }
}

// ---------- Recebimento de telemetria ----------
function aoTelemetria(dados) {
    if (!dados) return;
    estado = {
        telemetria: dados.telemetria || estado.telemetria,
        controle: dados.controle || estado.controle
    };
    document.body.dataset.dados = 'atualizado';

    atualizarSinotico();
    atualizarLeituras();
    atualizarModoUI();
    sincronizarCampos();
    avaliarAlarmesNivel();
    // Aviso de "CLP offline" repete o último valor; não é uma leitura nova e não entra no gráfico/CSV.
    if (!dados.telemetria || dados.telemetria.plcOnline !== false) registrarHistorico();
}

function avaliarAlarmesNivel() {
    const leds = estado.telemetria.leds || {};
    definirAlarme('alto', !!leds.high, {
        ligar: 'Nível alto: acima de 90 %',
        desligar: 'Nível alto normalizado'
    });
    definirAlarme('baixo', !!leds.low, {
        ligar: 'Nível baixo: abaixo de 10 %',
        desligar: 'Nível baixo normalizado'
    });
}

// ---------- Sinótico (SVG) ----------
function gerarEscala() {
    const g = byId('escala');
    const ns = 'http://www.w3.org/2000/svg';
    for (let v = 0; v <= 100; v += 10) {
        const y = nivelParaY(v);
        const tick = document.createElementNS(ns, 'line');
        tick.setAttribute('class', 'esc-traco');
        tick.setAttribute('x1', '400'); tick.setAttribute('x2', '408');
        tick.setAttribute('y1', y); tick.setAttribute('y2', y);
        g.appendChild(tick);

        if (v % 20 === 0) {
            const txt = document.createElementNS(ns, 'text');
            txt.setAttribute('class', 'esc-num');
            txt.setAttribute('x', '434'); txt.setAttribute('y', y + 4);
            txt.setAttribute('text-anchor', 'end');
            txt.textContent = String(v);
            g.appendChild(txt);
        }
    }
}

function atualizarFluxoVisivel() {
    const tel = estado.telemetria;
    const dadosOk = document.body.dataset.dados !== 'obsoleto';
    byId('fluxoEntrada').style.opacity = (dadosOk && tel.valvula > 1) ? '1' : '0';
    byId('fluxoSaida').style.opacity = (dadosOk && tel.nivel > 0.5) ? '1' : '0';
}

function atualizarSinotico() {
    const tel = estado.telemetria;
    const ctrl = estado.controle;

    const frac = clamp(tel.nivel, 0, 100) / 100;
    byId('liquido').style.transform = `scaleY(${frac})`;
    byId('superficie').style.transform = `translateY(${-(frac * 300)}px)`;
    byId('pvTexto').textContent = `${tel.nivel.toFixed(1)} %`;

    const spY = nivelParaY(ctrl.setpoint);
    byId('spMarcador').style.transform = `translateY(${spY}px)`;
    byId('spTexto').textContent = `SP ${ctrl.setpoint.toFixed(1)}`;

    byId('valvulaTexto').textContent = `${tel.valvula.toFixed(1)} %`;
    byId('valvulaCorpo').style.fillOpacity = (0.15 + 0.85 * (clamp(tel.valvula, 0, 100) / 100)).toFixed(2);

    atualizarFluxoVisivel();

    const contorno = byId('tanqueContorno');
    contorno.classList.toggle('baixo', !!tel.leds.low);
    contorno.classList.toggle('alto', !!tel.leds.high);

    byId('ledLow').classList.toggle('on', !!tel.leds.low);
    byId('ledNormal').classList.toggle('on', !!tel.leds.normal);
    byId('ledHigh').classList.toggle('on', !!tel.leds.high);
}

function atualizarLeituras() {
    const tel = estado.telemetria;
    const ctrl = estado.controle;
    byId('roPv').textContent = tel.nivel.toFixed(1);
    byId('roSp').textContent = ctrl.setpoint.toFixed(1);
    byId('roCv').textContent = tel.valvula.toFixed(1);
}

function atualizarModoUI() {
    const manual = estado.controle.modoManual;
    document.querySelectorAll('.js-modo-chip').forEach((el) => {
        el.textContent = manual ? 'Manual' : 'Automático';
        el.classList.toggle('manual', manual);
    });
    byId('btnAuto').setAttribute('aria-checked', String(!manual));
    byId('btnManual').setAttribute('aria-checked', String(manual));
    sincronizarCvHabilitado();
}

function sincronizarCvHabilitado() {
    const manual = estado.controle.modoManual;
    const campo = byId('campoCv');
    campo.classList.toggle('inativo', !manual);
    byId('inCv').disabled = !manual;
    byId('rgCv').disabled = !manual;
    byId('btnCv').disabled = !manual;
}

// ---------- Campos editáveis (setpoint, válvula manual, PID) ----------
function sincronizarCampo(spec) {
    const c = camposConfig[spec.chave];
    const input = byId(c.input);
    const campo = byId(c.campo);
    const hint = byId(c.hint);
    const valorServidor = spec.valor();

    hint.textContent = `No CLP: ${valorServidor.toFixed(spec.casas)}${spec.unidade ? ' ' + spec.unidade : ''}`;

    const alvosFoco = [input, c.espelho ? byId(c.espelho) : null].filter(Boolean);
    const focado = alvosFoco.includes(document.activeElement);

    if (!focado && !campo.classList.contains('alterado')) {
        input.value = valorServidor.toFixed(spec.casas);
        if (c.espelho) byId(c.espelho).value = String(valorServidor);
    }
}

function sincronizarCampos() {
    [specSp, specCv, specKp, specTi, specTd].forEach(sincronizarCampo);
}

function marcarDirty(spec) {
    const c = camposConfig[spec.chave];
    const input = byId(c.input);
    const campo = byId(c.campo);
    const erro = byId(c.erro);
    erro.textContent = '';
    campo.classList.remove('invalido');
    const n = Number(input.value);
    const dirty = input.value !== '' && !Number.isNaN(n) && Math.abs(n - spec.valor()) > 0.05;
    campo.classList.toggle('alterado', dirty);
}

function ligarCampo(spec) {
    const c = camposConfig[spec.chave];
    const input = byId(c.input);
    input.addEventListener('input', () => {
        if (c.espelho && document.activeElement === input) byId(c.espelho).value = input.value;
        marcarDirty(spec);
    });
    input.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && c.botao) byId(c.botao).click();
    });
    if (c.espelho) {
        const esp = byId(c.espelho);
        esp.addEventListener('input', () => {
            input.value = esp.value;
            marcarDirty(spec);
        });
    }
}

function validarCampo(spec) {
    const c = camposConfig[spec.chave];
    const input = byId(c.input);
    const campo = byId(c.campo);
    const erro = byId(c.erro);
    const msg = validar(input.value, spec.min, spec.max);
    erro.textContent = msg || '';
    campo.classList.toggle('invalido', !!msg);
    return msg ? null : Number(input.value);
}

function resultadoHandlers(rotulo, valorTexto, camposLimpar) {
    const lista = !camposLimpar ? [] : (Array.isArray(camposLimpar) ? camposLimpar : [camposLimpar]);
    return {
        sucesso: () => {
            lista.forEach((id) => byId(id).classList.remove('alterado'));
            aviso('ok', `${rotulo} enviado: ${valorTexto}.`);
            registrarEvento({ tipo: 'Comando', descricao: `${rotulo} → ${valorTexto}`, sev: 'ok', estado: 'Enviado' });
        },
        falha: (msg) => {
            aviso('erro', `${rotulo} não confirmado: ${msg}`);
            registrarEvento({ tipo: 'Comando', descricao: `${rotulo} → ${valorTexto}`, sev: 'com', estado: 'Falhou — ' + msg });
        }
    };
}

async function aplicarSp() {
    const val = validarCampo(specSp);
    if (val === null) return;
    travar([camposConfig.sp.input, camposConfig.sp.botao]);
    await enviarControle({ setpoint: val }, resultadoHandlers('Setpoint', `${val.toFixed(1)} %`, 'campoSp'));
    destravar([camposConfig.sp.input, camposConfig.sp.botao]);
}

async function aplicarCv() {
    const val = validarCampo(specCv);
    if (val === null) return;
    travar([camposConfig.cv.input, camposConfig.cv.botao, camposConfig.cv.espelho]);
    await enviarControle({ valvulaManual: val }, resultadoHandlers('Válvula manual', `${val.toFixed(1)} %`, 'campoCv'));
    destravar([camposConfig.cv.input, camposConfig.cv.botao, camposConfig.cv.espelho]);
    sincronizarCvHabilitado();
}

async function aplicarPid() {
    const vKp = validarCampo(specKp);
    const vTi = validarCampo(specTi);
    const vTd = validarCampo(specTd);
    if (vKp === null || vTi === null || vTd === null) return;

    const ids = [camposConfig.kp.input, camposConfig.ti.input, camposConfig.td.input, 'btnPid'];
    travar(ids);
    await enviarControle(
        { kp: vKp, ti: vTi, td: vTd },
        resultadoHandlers('Sintonia PID', `Kp ${vKp} · Ti ${vTi} s · Td ${vTd} s`, ['campoKp', 'campoTi', 'campoTd'])
    );
    destravar(ids);
}

async function definirModo(manual) {
    if (manual === estado.controle.modoManual) return;
    travar(['btnAuto', 'btnManual']);
    const payload = { modoManual: manual };
    if (manual && byId('chkSemImpacto').checked) {
        payload.valvulaManual = estado.telemetria.valvula; // transferência sem impacto (bumpless)
    }
    const rotulo = manual ? 'Modo manual' : 'Modo automático';
    await enviarControle(payload, {
        sucesso: () => {
            aviso('ok', `${rotulo} ativado.`);
            registrarEvento({ tipo: 'Comando', descricao: `${rotulo} ativado`, sev: 'ok', estado: 'Enviado' });
        },
        falha: (msg) => {
            aviso('erro', `${rotulo}: comando não confirmado — ${msg}`);
            registrarEvento({ tipo: 'Comando', descricao: `Tentativa de ativar ${rotulo.toLowerCase()}`, sev: 'com', estado: 'Falhou — ' + msg });
        }
    });
    destravar(['btnAuto', 'btnManual']);
}

// ---------- Alarmes e eventos ----------
function definirAlarme(chave, ativo, textos) {
    const a = alarmes[chave];
    if (ativo === a.ativo) return;
    a.ativo = ativo;
    if (ativo) {
        a.ack = false;
        registrarEvento({ tipo: 'Alarme', descricao: textos.ligar, sev: chave, estado: 'Ativo — não reconhecido', pendente: true });
    } else {
        a.ack = true;
        if (textos.desligar) {
            registrarEvento({ tipo: 'Alarme', descricao: textos.desligar, sev: 'ok', estado: 'Normalizado', pendente: false });
        }
    }
    atualizarChipEBanner();
}

function atualizarChipEBanner() {
    const PRIORIDADE = ['alto', 'com', 'baixo'];
    const nomes = { alto: 'Nível alto', baixo: 'Nível baixo', com: 'Falha de comunicação' };
    const mensagens = {
        alto: 'Nível acima de 90 %. Verifique a válvula e o setpoint.',
        baixo: 'Nível abaixo de 10 %. Verifique a alimentação.',
        com: 'Sem comunicação confiável com o CLP.'
    };

    const chip = byId('chipAlarme');
    const ativos = PRIORIDADE.filter((k) => alarmes[k].ativo);
    if (ativos.length === 0) {
        chip.textContent = 'Sem alarmes';
        chip.className = 'chip-alarme ok';
    } else {
        const principal = ativos[0];
        chip.textContent = ativos.length > 1 ? `${nomes[principal]} +${ativos.length - 1}` : nomes[principal];
        chip.className = 'chip-alarme ' + principal;
    }

    const faixa = byId('faixaAlarme');
    const faixaTexto = byId('faixaTexto');
    const pendentes = PRIORIDADE.filter((k) => alarmes[k].ativo && !alarmes[k].ack);
    if (pendentes.length === 0) {
        faixa.hidden = true;
        faixa.className = 'faixa-alarme';
    } else {
        const principal = pendentes[0];
        const prefixo = pendentes.length > 1 ? `(${pendentes.length} alarmes) ` : '';
        faixaTexto.textContent = prefixo + mensagens[principal];
        faixa.hidden = false;
        faixa.className = 'faixa-alarme pendente ' + principal;
    }
}

function reconhecerAlarmes() {
    let algum = false;
    Object.keys(alarmes).forEach((k) => {
        if (alarmes[k].ativo && !alarmes[k].ack) { alarmes[k].ack = true; algum = true; }
    });
    if (algum) {
        eventos.filter((e) => e.pendente).forEach((e) => { e.pendente = false; e.estado = 'Reconhecido'; });
        renderEventos();
        atualizarChipEBanner();
        aviso('info', 'Alarmes reconhecidos.');
    }
}

function registrarEvento(ev) {
    eventos.unshift({
        id: proximoIdEvento++,
        hora: new Date(),
        tipo: ev.tipo,
        descricao: ev.descricao,
        estado: ev.estado,
        sev: ev.sev || 'ok',
        pendente: !!ev.pendente
    });
    if (eventos.length > 200) eventos.length = 200;
    renderEventos();
}

function renderEventos() {
    const corpo = byId('corpoEventos');
    const vazio = byId('eventosVazio');
    vazio.hidden = eventos.length > 0;
    corpo.innerHTML = eventos.map((e) => `
        <tr class="sev-${e.sev}${e.pendente ? ' pendente' : ''}">
            <td>${formatarHora(e.hora)}</td>
            <td>${escapeHtml(e.tipo)}</td>
            <td>${escapeHtml(e.descricao)}</td>
            <td>${escapeHtml(e.estado)}</td>
        </tr>`).join('');
}

function limparEventos() {
    eventos = [];
    renderEventos();
}

// ---------- Histórico e gráficos ----------
function registrarHistorico() {
    const agora = Date.now();
    historico.push({ t: agora, nivel: estado.telemetria.nivel, sp: estado.controle.setpoint, cv: estado.telemetria.valvula });
    const limite = agora - RETENCAO_MS;
    while (historico.length && historico[0].t < limite) historico.shift();
    if (!pausado) redesenharGraficos();
}

function initGraficos() {
    const estilo = getComputedStyle(document.documentElement);
    const corPv = estilo.getPropertyValue('--pv').trim();
    const corSp = estilo.getPropertyValue('--sp').trim();
    const corCv = estilo.getPropertyValue('--cv').trim();
    const corGrade = estilo.getPropertyValue('--linha').trim();
    const corTexto = estilo.getPropertyValue('--suave').trim();

    Chart.defaults.font.family = "'IBM Plex Mono', monospace";
    Chart.defaults.font.size = 11;
    Chart.defaults.color = corTexto;

    const eixoX = { grid: { color: corGrade, drawTicks: false }, ticks: { maxTicksLimit: 8, autoSkip: true } };
    const tooltipPct = { callbacks: { label: (ctx) => `${ctx.dataset.label}: ${ctx.parsed.y.toFixed(1)} %` } };

    graficoNivel = new Chart(byId('gNivel').getContext('2d'), {
        type: 'line',
        data: {
            labels: [],
            datasets: [
                { label: 'Nível', data: [], borderColor: corPv, backgroundColor: corPv + '22', borderWidth: 2, pointRadius: 0, tension: 0.2, fill: false },
                { label: 'Setpoint', data: [], borderColor: corSp, borderDash: [6, 4], borderWidth: 1.5, pointRadius: 0, tension: 0, fill: false }
            ]
        },
        options: {
            responsive: true, maintainAspectRatio: false, animation: false,
            interaction: { mode: 'index', intersect: false },
            plugins: { legend: { display: false }, tooltip: tooltipPct },
            scales: { x: eixoX, y: { min: 0, max: 100, grid: { color: corGrade }, ticks: { callback: (v) => v + '%' } } }
        }
    });

    graficoSaida = new Chart(byId('gSaida').getContext('2d'), {
        type: 'line',
        data: {
            labels: [],
            datasets: [
                { label: 'Saída da válvula', data: [], borderColor: corCv, backgroundColor: corCv + '22', borderWidth: 2, pointRadius: 0, tension: 0.2, fill: true }
            ]
        },
        options: {
            responsive: true, maintainAspectRatio: false, animation: false,
            interaction: { mode: 'index', intersect: false },
            plugins: { legend: { display: false }, tooltip: tooltipPct },
            scales: { x: eixoX, y: { min: 0, max: 100, grid: { color: corGrade }, ticks: { callback: (v) => v + '%' } } }
        }
    });
}

function redesenharGraficos() {
    if (!graficoNivel || !graficoSaida) return;
    const agora = Date.now();
    const desde = agora - janelaSegundos * 1000;
    const pontos = historico.filter((p) => p.t >= desde);
    const labels = pontos.map((p) => formatarHora(p.t));

    graficoNivel.data.labels = labels;
    graficoNivel.data.datasets[0].data = pontos.map((p) => p.nivel);
    graficoNivel.data.datasets[1].data = pontos.map((p) => p.sp);
    graficoNivel.update('none');

    graficoSaida.data.labels = labels;
    graficoSaida.data.datasets[0].data = pontos.map((p) => p.cv);
    graficoSaida.update('none');
}

function alternarPausa() {
    pausado = !pausado;
    const b = byId('btnPausa');
    b.textContent = pausado ? 'Retomar' : 'Pausar';
    b.setAttribute('aria-pressed', String(pausado));
    if (!pausado) redesenharGraficos();
}

function exportarCsv() {
    if (historico.length === 0) {
        aviso('info', 'Ainda não há histórico para exportar.');
        return;
    }
    const linhas = ['hora_iso,nivel_pct,setpoint_pct,saida_valvula_pct'];
    historico.forEach((p) => linhas.push(`${new Date(p.t).toISOString()},${p.nivel},${p.sp},${p.cv}`));

    const blob = new Blob([linhas.join('\n')], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    const nome = `tq101_historico_${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')}.csv`;
    a.href = url; a.download = nome;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    aviso('ok', 'Histórico exportado.');
}

// ---------- Relógio ----------
function atualizarRelogio() {
    byId('relogio').textContent = new Date().toLocaleTimeString('pt-BR', { hour12: false });
}

// ---------- Inicialização ----------
function iniciar() {
    gerarEscala();

    [specSp, specCv, specKp, specTi, specTd].forEach(ligarCampo);
    byId('btnSp').addEventListener('click', aplicarSp);
    byId('btnCv').addEventListener('click', aplicarCv);
    byId('btnPid').addEventListener('click', aplicarPid);

    byId('btnAuto').addEventListener('click', () => definirModo(false));
    byId('btnManual').addEventListener('click', () => definirModo(true));

    byId('btnReconhecer').addEventListener('click', reconhecerAlarmes);
    byId('btnReconhecerFaixa').addEventListener('click', reconhecerAlarmes);
    byId('btnLimpar').addEventListener('click', limparEventos);

    byId('btnPausa').addEventListener('click', alternarPausa);
    byId('btnCsv').addEventListener('click', exportarCsv);
    byId('selJanela').addEventListener('change', (e) => {
        janelaSegundos = Number(e.target.value);
        redesenharGraficos();
    });

    initGraficos();
    conectarSocket();
    carregarEstadoInicial();

    atualizarRelogio();
    setInterval(atualizarRelogio, 1000);
    setInterval(verificarFrescor, 1000);
}

document.addEventListener('DOMContentLoaded', iniciar);