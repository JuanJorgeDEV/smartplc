// Estado central do aplicativo. Fonte da verdade do que o CLP reportou: SEMPRE o backend (telemetria), nunca otimismo local.
import * as api from './api.js';

const ouvintes = new Map();
export function on(topico, fn) {
    if (!ouvintes.has(topico)) ouvintes.set(topico, new Set());
    ouvintes.get(topico).add(fn);
    return () => ouvintes.get(topico)?.delete(fn);
}
export function emitir(topico, dado) {
    ouvintes.get(topico)?.forEach((fn) => { try { fn(dado); } catch (e) { console.error(`ouvinte de ${topico}`, e); } });
}

const RETENCAO_AMOSTRAS_MS = 60 * 60 * 1000;

export const state = {
    config: null,
    variaveis: [],
    hub: 'conectando',                 // conectando | conectado | reconectando | desconectado
    status: {
        telemetria: { nivel: 0, valvula: 0, leds: { low: false, normal: false, high: false }, timestamp: null, plcOnline: false, idadeSegundos: null },
        controle: { setpoint: 0, modoManual: false, valvulaManual: 0, kp: 1, ti: 20, td: 0 }
    },
    recebidoEm: null,                  // Date.now() local quando o status chegou
    amostras: [],                      // leituras reais recebidas nesta aba: { t, pv, sp, cv }
    eventos: [],                       // eventos locais desta aba
    comandos: [],                      // mais novos primeiro
    alarmes: { baixo: { ativo: false, ack: true }, alto: { ativo: false, ack: true }, com: { ativo: false, ack: true } }
};

export const limiteObsoleto = () => state.config?.processo?.obsoletoAposSegundos ?? 5;

/** Segundos desde a última leitura do CLP (a idade informada pelo servidor + o tempo local decorrido). */
export function idadeAtualSeg() {
    const t = state.status.telemetria;
    if (t.timestamp === null || state.recebidoEm === null) return null;
    return (t.idadeSegundos ?? 0) + (Date.now() - state.recebidoEm) / 1000;
}

/** servidor_off | aguardando | ok | sem_clp */
export function comunicacao() {
    if (state.hub !== 'conectado') return 'servidor_off';
    if (state.status.telemetria.timestamp === null) return 'aguardando';
    return idadeAtualSeg() > limiteObsoleto() ? 'sem_clp' : 'ok';
}

export const online = () => comunicacao() === 'ok';

export function setHub(estado) {
    if (state.hub === estado) return;
    state.hub = estado;
    emitir('conexao');
    avaliar();
}

let ultimoCarimbo = null;

export function setStatus(dados) {
    if (!dados?.telemetria || !dados?.controle) return;
    state.status = dados;
    state.recebidoEm = Date.now();

    const t = dados.telemetria;
    const novaLeitura = t.timestamp !== null && t.timestamp !== ultimoCarimbo && t.plcOnline && (t.idadeSegundos ?? 0) <= limiteObsoleto();
    if (novaLeitura) {
        ultimoCarimbo = t.timestamp;
        const agora = Date.now();
        state.amostras.push({ t: agora - (t.idadeSegundos ?? 0) * 1000, pv: t.nivel, sp: dados.controle.setpoint, cv: t.valvula, modo: dados.controle.modoManual });
        const corte = agora - RETENCAO_AMOSTRAS_MS;
        while (state.amostras.length && state.amostras[0].t < corte) state.amostras.shift();
    }
    emitir('status', { novaLeitura });
    avaliar();
}

// ---------- Alarmes e eventos locais desta aba ----------

let proximoIdEvento = 1;
export function registrarEvento(ev) {
    state.eventos.unshift({ id: proximoIdEvento++, t: Date.now(), sev: 'info', ...ev });
    if (state.eventos.length > 200) state.eventos.length = 200;
    emitir('eventos');
}

function definirAlarme(chave, ativo, textos) {
    const a = state.alarmes[chave];
    if (a.ativo === ativo) return;
    a.ativo = ativo;
    if (ativo) {
        a.ack = false;
        registrarEvento({ tipo: 'Alarme', texto: textos.ligar, sev: chave === 'com' ? 'com' : chave, estado: 'Ativo' });
    } else {
        a.ack = true;
        registrarEvento({ tipo: 'Alarme', texto: textos.desligar, sev: 'ok', estado: 'Normalizado' });
    }
    emitir('alarmes');
}

export function reconhecerAlarmes() {
    let algum = false;
    for (const a of Object.values(state.alarmes)) if (a.ativo && !a.ack) { a.ack = true; algum = true; }
    if (algum) emitir('alarmes');
    return algum;
}

/** Recalcula alarmes a partir do estado atual. Chamado a cada status e a cada segundo. */
export function avaliar() {
    const com = comunicacao();
    const leds = state.status.telemetria.leds;
    const valido = com === 'ok';
    definirAlarme('baixo', valido && !!leds.low, { ligar: 'Nível baixo (abaixo do limite)', desligar: 'Nível baixo normalizado' });
    definirAlarme('alto', valido && !!leds.high, { ligar: 'Nível alto (acima do limite)', desligar: 'Nível alto normalizado' });
    definirAlarme('com', com === 'servidor_off' || com === 'sem_clp', {
        ligar: com === 'servidor_off' ? 'Conexão com o servidor perdida' : 'Sem comunicação com o CLP (telemetria parou)',
        desligar: 'Comunicação restabelecida'
    });
    emitir('comunicacao', com);
}

// ---------- Comandos ----------

export function atualizarComando(dto) {
    if (!dto?.id) return;
    const i = state.comandos.findIndex((c) => c.id === dto.id);
    if (i >= 0) state.comandos[i] = dto; else state.comandos.unshift(dto);
    state.comandos.sort((a, b) => new Date(b.solicitadoEm) - new Date(a.solicitadoEm));
    if (state.comandos.length > 50) state.comandos.length = 50;
    emitir('comandos', dto);
}

// ---------- Carga inicial / ressincronização ----------

export async function carregarStatus() {
    const r = await api.get('/api/status', 5000);
    if (r.ok) setStatus(r.data);
    return r.ok;
}

export async function carregarComandos() {
    const r = await api.get('/api/comandos?limite=20', 5000);
    if (r.ok) { state.comandos = r.data.comandos ?? []; emitir('comandos'); }
}

export async function carregarConfig() {
    const [c, v] = await Promise.all([api.get('/api/config', 5000), api.get('/api/variaveis', 5000)]);
    if (c.ok) state.config = c.data;
    if (v.ok) { state.variaveis = v.data.variaveis ?? []; state.enderecosVisiveis = v.data.enderecosVisiveis; }
    emitir('config');
    return c.ok;
}

export async function ressincronizar() {
    await Promise.all([carregarStatus(), carregarComandos()]);
}

/** Relógio de 1 s: envelhece os dados e reavalia alarmes mesmo sem mensagens novas. */
export function iniciarRelogio() {
    return setInterval(() => { avaliar(); emitir('tick'); }, 1000);
}
