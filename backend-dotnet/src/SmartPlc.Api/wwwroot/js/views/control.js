// Controle: separado da supervisão. "No CLP" é SEMPRE o que a telemetria reportou; nada é exibido como aplicado antes da releitura.
import { state, on, comunicacao, atualizarComando, carregarComandos } from '../store.js';
import { nomeDe, casasDe } from '../prefs.js';
import { num, hora, esc } from '../format.js';
import { termo } from '../glossary.js';
import { aviso, confirmar } from '../ui.js';
import * as api from '../api.js';
import { sessao, podeOperar } from '../sessao.js';

export const titulo = 'Controle';

const ROTULO_CAMPO = { setpoint: 'Setpoint', modoManual: 'Modo', valvulaManual: 'Abertura manual', kp: 'Kp', ti: 'Ti', td: 'Td' };
const ESTADOS = {
    solicitado: ['Solicitado', 'Validado pelo servidor e sendo enviado ao Node-RED.'],
    recebido: ['Recebido pelo Node-RED', 'O Node-RED aceitou o pedido. Ainda não é prova de que o CLP aplicou.'],
    confirmado: ['Confirmado pelo CLP', 'A telemetria, lida do CLP, trouxe o valor pedido.'],
    falhou: ['Falhou', 'O Node-RED recusou, não respondeu ou estava inacessível.'],
    expirado: ['Expirado', 'O CLP não reportou o valor pedido dentro do prazo.'],
    substituido: ['Substituído', 'Um pedido mais recente cobriu os mesmos campos.']
};

function resumoCampos(campos) {
    return Object.entries(campos).map(([k, v]) => {
        const valor = k === 'modoManual' ? (v ? 'manual' : 'automático') : num(v, k === 'kp' ? 2 : 1);
        return `${ROTULO_CAMPO[k] ?? k} → ${valor}`;
    }).join(' · ');
}

function passos(estado) {
    const final = { confirmado: 'Confirmado', falhou: 'Falhou', expirado: 'Expirado', substituido: 'Substituído' }[estado];
    const idx = { solicitado: 0, recebido: 1, confirmado: 2, falhou: 2, expirado: 2, substituido: 2 }[estado] ?? 0;
    const rotulos = ['Solicitado', 'Recebido', final ?? 'Confirmado'];
    const tom = estado === 'confirmado' ? 'ok' : (estado === 'falhou' || estado === 'expirado') ? 'alarm' : estado === 'substituido' ? 'off' : 'ativo';
    return `<ol class="steps tom-${tom}" aria-label="Etapas do comando">${rotulos.map((r, i) =>
        `<li class="${i < idx ? 'feito' : i === idx ? 'atual' : ''}${i === 2 && !final ? ' pendente' : ''}"><span class="sq" aria-hidden="true"></span>${r}</li>`).join('')}</ol>`;
}

export async function montar(raiz) {
    raiz.innerHTML = `
<div class="ctl">
  <div class="ctl-main">
    <div id="ctl-banner" class="banner" role="status"></div>

    <section class="ctl-sec" aria-labelledby="c-sp">
      <div class="sec-head"><h2 id="c-sp" class="eyebrow">A · Setpoint (${termo('sp', 'SP')})</h2><span class="tag" id="st-setpoint"></span></div>
      <div class="ctl-grid">
        <div class="ctl-atual"><span class="eyebrow">Valor no CLP</span><span class="val"><span class="num big" id="v-sp">—</span><span class="muted">%</span></span></div>
        <form class="ctl-form" id="f-sp" novalidate>
          <div class="field"><label for="in-sp">Novo setpoint (%)</label>
            <input class="input" id="in-sp" type="number" inputmode="decimal" step="0.1" aria-describedby="h-sp e-sp">
            <p class="hint" id="h-sp"></p><p class="error" id="e-sp" role="alert"></p></div>
          <button type="submit" class="btn primary" id="b-sp">Solicitar</button>
        </form>
      </div>
    </section>

    <section class="ctl-sec" aria-labelledby="c-modo">
      <div class="sec-head"><h2 id="c-modo" class="eyebrow">B · Modo de operação</h2><span class="tag" id="st-modo"></span></div>
      <div class="ctl-grid">
        <div class="ctl-atual"><span class="eyebrow">Modo no CLP</span><span class="val"><span class="num big big-t" id="v-modo">—</span></span></div>
        <div class="ctl-form">
          <div class="seg" role="radiogroup" aria-label="Modo desejado">
            <button type="button" role="radio" id="m-auto" aria-checked="false">Automático</button>
            <button type="button" role="radio" id="m-man" aria-checked="false">Manual</button>
          </div>
          <label class="check"><input type="checkbox" id="chk-bump" checked> Manter a saída atual ao passar para manual</label>
          <button type="button" class="btn primary" id="b-modo">Solicitar modo</button>
        </div>
      </div>

      <div class="saidas" aria-label="Diferença entre saída automática e manual">
        <div><span class="eyebrow">${termo('automatico', 'Automático')} · saída do PID (${termo('cv', 'CV')})</span><span class="num big-s" id="v-cv">—</span><span class="muted"> %</span>
          <p class="note">O PID calcula a abertura para levar a PV ao SP.</p></div>
        <div><span class="eyebrow">${termo('manual', 'Manual')} · abertura definida</span><span class="num big-s" id="v-cvman">—</span><span class="muted"> %</span>
          <p class="note">Valor guardado no CLP. Só governa a válvula no modo manual.</p></div>
      </div>

      <form class="ctl-form man-form" id="f-cv" novalidate>
        <div class="field"><label for="in-cv">Abertura manual da válvula (%)</label>
          <input id="rg-cv" type="range" min="0" max="100" step="0.5" aria-label="Abertura manual, controle deslizante">
          <input class="input" id="in-cv" type="number" inputmode="decimal" step="0.1" aria-describedby="h-cv e-cv">
          <p class="hint" id="h-cv"></p><p class="error" id="e-cv" role="alert"></p></div>
        <button type="submit" class="btn primary" id="b-cv">Solicitar abertura</button>
      </form>
    </section>

    <section class="ctl-sec" aria-labelledby="c-pid">
      <div class="sec-head"><h2 id="c-pid" class="eyebrow">C · Sintonia ${termo('pid', 'PID')}</h2><span class="tag" id="st-pid"></span></div>
      <form class="pid-grid" id="f-pid" novalidate>
        <div class="field"><label for="in-kp">${termo('kp', 'Kp')} · ganho proporcional</label><input class="input" id="in-kp" type="number" step="0.1" inputmode="decimal" aria-describedby="h-kp e-kp"><p class="hint" id="h-kp"></p><p class="error" id="e-kp" role="alert"></p></div>
        <div class="field"><label for="in-ti">${termo('ti', 'Ti')} · tempo integral (s)</label><input class="input" id="in-ti" type="number" step="0.1" inputmode="decimal" aria-describedby="h-ti e-ti"><p class="hint" id="h-ti"></p><p class="error" id="e-ti" role="alert"></p></div>
        <div class="field"><label for="in-td">${termo('td', 'Td')} · tempo derivativo (s)</label><input class="input" id="in-td" type="number" step="0.1" inputmode="decimal" aria-describedby="h-td e-td"><p class="hint" id="h-td"></p><p class="error" id="e-td" role="alert"></p></div>
        <div class="pid-acao"><button type="submit" class="btn primary" id="b-pid">Solicitar sintonia</button></div>
      </form>
      <p class="note">Kp: intensidade da reação ao erro. Ti: ação que considera o erro acumulado ao longo do tempo. Td: ação que considera a velocidade de variação do erro.
        São explicações conceituais: o efeito real depende de como o PID foi implementado no CLP.</p>
    </section>
  </div>

  <aside class="ctl-cmds" aria-labelledby="c-cmd">
    <div class="sec-head"><h2 id="c-cmd" class="eyebrow">Comandos recentes</h2><span class="badge">tempo real</span></div>
    <ul class="cmd-lista" id="cmd-lista"></ul>
    <p class="note" id="cmd-vazio">Nenhum comando enviado desde que o servidor iniciou.</p>
    <p class="note">Um comando só é <strong>confirmado</strong> quando a telemetria, lida do CLP, traz o valor pedido
      (prazo: <span id="prazo">10</span> s). Receber um "ok" do Node-RED não prova que o CLP executou.</p>
  </aside>
</div>`;

    const $ = (id) => raiz.querySelector(`#${id}`);
    const cfg = () => state.config?.controle;
    const lim = (campo) => cfg()?.limites?.[campo] ?? [0, 100];
    const sujo = new Set();                 // campos editados pelo usuário e ainda não enviados
    let desejado = null;                    // modo escolhido na tela (true = manual)
    let ocupado = false;

    const habilitado = () => !!cfg()?.habilitado && !cfg()?.exigeChaveDeOperador && podeOperar();
    const temTelemetria = () => state.status.telemetria.timestamp !== null;

    // ---------- Banner de modo ----------
    function banner() {
        const c = cfg();
        const b = $('ctl-banner');
        let tom = 'info', msg;
        if (!c) { tom = 'warn'; msg = 'Não foi possível ler a configuração do servidor.'; }
        else if (!c.habilitado) msg = '<strong>Modo somente leitura.</strong> Os comandos estão desabilitados neste ambiente. Os campos abaixo mostram o que o CLP reporta, mas nada é enviado.';
        else if (!podeOperar()) { tom = 'warn'; msg = '<strong>Seu perfil é somente de consulta.</strong> Os campos mostram o que o CLP reporta, mas só operadores e administradores podem enviar comandos.'; }
        else if (c.exigeChaveDeOperador) { tom = 'warn'; msg = '<strong>Comandos exigem uma chave de operador.</strong> Este painel nunca guarda chaves no navegador; ative o login para operar por aqui. Os campos abaixo ficam apenas para consulta.'; }
        else if (comunicacao() !== 'ok') { tom = 'warn'; msg = '<strong>Sem telemetria válida do CLP agora.</strong> Os comandos podem ser recebidos, mas só serão confirmados quando o CLP voltar a reportar.'; }
        else msg = `<strong>Controle habilitado${sessao.autenticacaoAtiva ? '' : ' (ambiente local)'}.</strong> Cada solicitação pede confirmação e só é dada como aplicada depois que o CLP a reporta.${c.entrega === 'fila' ? ' Os comandos seguem por uma fila: o Node-RED os recebe na resposta da próxima telemetria (cerca de 1 s) e, sem ele enviando dados, expiram.' : ''}`;
        b.className = `banner ${tom}`;
        b.innerHTML = msg;
        $('prazo').textContent = String(c?.confirmacaoSegundos ?? 10);
    }

    // ---------- Valores reportados pelo CLP ----------
    function pintarAtuais() {
        const t = state.status.telemetria, c = state.status.controle, tem = temTelemetria();
        $('v-sp').textContent = tem ? num(c.setpoint, casasDe('setpoint')) : '—';
        $('v-modo').textContent = tem ? (c.modoManual ? 'Manual' : 'Automático') : '—';
        $('v-cv').textContent = tem ? num(t.valvula, casasDe('valvula')) : '—';
        $('v-cvman').textContent = tem ? num(c.valvulaManual, casasDe('valvulaManual')) : '—';
        $('h-sp').textContent = `Valor no CLP: ${tem ? num(c.setpoint, 1) : '—'} %. Faixa: ${lim('setpoint')[0]} a ${lim('setpoint')[1]} %.`;
        $('h-kp').textContent = `No CLP: ${tem ? num(c.kp, 2) : '—'} · faixa ${lim('kp')[0]} a ${lim('kp')[1]}`;
        $('h-ti').textContent = `No CLP: ${tem ? num(c.ti, 1) : '—'} s · faixa ${lim('ti')[0]} a ${lim('ti')[1]}`;
        $('h-td').textContent = `No CLP: ${tem ? num(c.td, 1) : '—'} s · faixa ${lim('td')[0]} a ${lim('td')[1]}`;
        if (desejado === null && tem) desejado = c.modoManual;
        pintarModo();
    }

    // Preenche os campos com o valor do CLP enquanto o usuário não os editou.
    function sincronizarCampos() {
        if (!temTelemetria()) return;
        const c = state.status.controle;
        const mapa = { sp: ['in-sp', c.setpoint, 1], cv: ['in-cv', c.valvulaManual, 1], kp: ['in-kp', c.kp, 2], ti: ['in-ti', c.ti, 1], td: ['in-td', c.td, 1] };
        for (const [k, [id, v, casas]] of Object.entries(mapa)) {
            const el = $(id);
            if (sujo.has(k) || document.activeElement === el) continue;
            el.value = Number(v).toFixed(casas);
            if (k === 'cv') $('rg-cv').value = String(v);
        }
    }

    function pintarModo() {
        const manualNoClp = temTelemetria() && state.status.controle.modoManual;
        $('m-auto').setAttribute('aria-checked', String(desejado === false));
        $('m-man').setAttribute('aria-checked', String(desejado === true));
        const pode = habilitado() && !ocupado;
        $('m-auto').disabled = !habilitado(); $('m-man').disabled = !habilitado();
        $('b-modo').disabled = !pode || desejado === null || !temTelemetria() || desejado === state.status.controle.modoManual;
        $('chk-bump').disabled = !habilitado() || desejado !== true;
        // abertura manual: só quando o CLP está em manual (como no painel original)
        const manualOk = habilitado() && manualNoClp;
        $('in-cv').disabled = !manualOk; $('rg-cv').disabled = !manualOk; $('b-cv').disabled = !manualOk || ocupado;
        const tem = temTelemetria(), c = state.status.controle;
        $('h-cv').textContent = `Valor no CLP: ${tem ? num(c.valvulaManual, 1) : '—'} %. Faixa: ${lim('valvulaManual')[0]} a ${lim('valvulaManual')[1]} %.`
            + (habilitado() && !manualOk ? ' Disponível quando o CLP estiver em modo manual.' : '');
    }

    function pintarHabilitacao() {
        const pode = habilitado() && !ocupado;
        ['in-sp', 'in-kp', 'in-ti', 'in-td'].forEach((id) => { $(id).disabled = !habilitado(); });
        ['b-sp', 'b-pid'].forEach((id) => { $(id).disabled = !pode; });
        pintarModo();
    }

    // ---------- Estado do último comando por seção ----------
    function ultimoCom(...campos) { return state.comandos.find((c) => campos.some((k) => k in c.campos)); }
    function pintarEstadoSecoes() {
        const defs = { 'st-setpoint': ['setpoint'], 'st-modo': ['modoManual'], 'st-pid': ['kp', 'ti', 'td'] };
        for (const [id, campos] of Object.entries(defs)) {
            const c = ultimoCom(...campos);
            const el = $(id);
            if (!c) { el.className = 'tag off'; el.textContent = 'Sem solicitações'; continue; }
            const [rot] = ESTADOS[c.estado] ?? [c.estado];
            el.className = `tag ${c.estado === 'confirmado' ? 'ok' : (c.estado === 'falhou' || c.estado === 'expirado') ? 'alarm' : c.estado === 'substituido' ? 'off' : 'warn'}`;
            el.textContent = `Última: ${rot}`;
        }
    }

    // ---------- Lista de comandos ----------
    function pintarComandos() {
        const lista = state.comandos.slice(0, 10);
        $('cmd-vazio').hidden = lista.length > 0;
        $('cmd-lista').innerHTML = lista.map((c) => {
            const [rot, ajuda] = ESTADOS[c.estado] ?? [c.estado, ''];
            return `<li class="cmd"><div class="cmd-topo"><span class="num">${hora(c.solicitadoEm)}</span><strong>${esc(resumoCampos(c.campos))}</strong></div>
              ${passos(c.estado)}
              <p class="small"><strong>${esc(rot)}.</strong> ${esc(c.detalhe || ajuda)}</p></li>`;
        }).join('');
        pintarEstadoSecoes();
    }

    // ---------- Validação e envio ----------
    function validar(id, erroId, campo, rotulo) {
        const el = $(id), erro = $(erroId);
        const bruto = el.value.trim().replace(',', '.');
        const [min, max] = lim(campo);
        let msg = '';
        if (bruto === '') msg = 'Informe um valor.';
        else if (Number.isNaN(Number(bruto))) msg = 'Valor inválido.';
        else if (Number(bruto) < min || Number(bruto) > max) msg = `${rotulo}: informe um valor entre ${min} e ${max}.`;
        erro.textContent = msg;
        el.setAttribute('aria-invalid', msg ? 'true' : 'false');
        return msg ? null : Number(bruto);
    }

    function mensagemFalha(r) {
        if (r.status === 401) return 'sua sessão terminou ou o servidor exige uma chave de operador; entre novamente.';
        if (r.status === 403) return r.data?.mensagem ? `${r.erro}.` : 'seu perfil não permite enviar comandos.';
        if (r.status === 0) return `${r.erro}.`;
        return `${r.erro}.`;
    }

    async function enviar(payload, rotulo, corpoHtml, camposLimpar) {
        if (!habilitado() || ocupado) return;
        const ok = await confirmar({ titulo: `Confirmar: ${rotulo}`, corpo: `${corpoHtml}<br><span class="muted">O valor só será exibido como aplicado depois que o CLP o reportar.</span>`, confirmarTexto: 'Enviar solicitação' });
        if (!ok) return;
        ocupado = true; pintarHabilitacao();
        const r = await api.post('/api/controle', payload, 6000);
        ocupado = false;
        if (r.ok) {
            camposLimpar.forEach((k) => sujo.delete(k));
            if (r.data?.comando) atualizarComando(r.data.comando);
            aviso(r.data?.estadoComando === 'confirmado' ? 'ok' : 'info',
                r.data?.estadoComando === 'confirmado'
                    ? `${rotulo}: confirmado pelo CLP.`
                    : `${rotulo}: recebido pelo Node-RED. Aguardando o CLP reportar o valor…`);
        } else {
            aviso('erro', `${rotulo}: ${mensagemFalha(r)}`);
            carregarComandos();
        }
        pintarHabilitacao();
        sincronizarCampos();
    }

    const atual = () => state.status.controle;

    $('f-sp').addEventListener('submit', (e) => {
        e.preventDefault();
        const v = validar('in-sp', 'e-sp', 'setpoint', 'Setpoint');
        if (v === null) return;
        enviar({ setpoint: v }, 'Setpoint', `Alterar o setpoint de <strong>${num(atual().setpoint, 1)} %</strong> (valor no CLP) para <strong>${num(v, 1)} %</strong>?`, ['sp']);
    });

    $('f-cv').addEventListener('submit', (e) => {
        e.preventDefault();
        const v = validar('in-cv', 'e-cv', 'valvulaManual', 'Abertura');
        if (v === null) return;
        enviar({ valvulaManual: v }, 'Abertura manual', `Alterar a abertura manual de <strong>${num(atual().valvulaManual, 1)} %</strong> para <strong>${num(v, 1)} %</strong>?`, ['cv']);
    });

    $('f-pid').addEventListener('submit', (e) => {
        e.preventDefault();
        const kp = validar('in-kp', 'e-kp', 'kp', 'Kp'), ti = validar('in-ti', 'e-ti', 'ti', 'Ti'), td = validar('in-td', 'e-td', 'td', 'Td');
        if (kp === null || ti === null || td === null) return;
        enviar({ kp, ti, td }, 'Sintonia PID',
            `Alterar a sintonia?<br>Kp ${num(atual().kp, 2)} → <strong>${num(kp, 2)}</strong> · Ti ${num(atual().ti, 1)} s → <strong>${num(ti, 1)} s</strong> · Td ${num(atual().td, 1)} s → <strong>${num(td, 1)} s</strong>`, ['kp', 'ti', 'td']);
    });

    $('m-auto').addEventListener('click', () => { desejado = false; pintarModo(); });
    $('m-man').addEventListener('click', () => { desejado = true; pintarModo(); });
    $('b-modo').addEventListener('click', () => {
        const manual = desejado === true;
        const payload = { modoManual: manual };
        let extra = '';
        if (manual && $('chk-bump').checked) { payload.valvulaManual = state.status.telemetria.valvula; extra = `<br>A abertura manual começa em <strong>${num(payload.valvulaManual, 1)} %</strong> (a saída atual), para evitar um salto.`; }
        enviar(payload, 'Modo de operação', `Passar o CLP para o modo <strong>${manual ? 'manual' : 'automático'}</strong>?${extra}`, []);
    });

    // edição manual marca o campo como "sujo" (não é sobrescrito pela telemetria)
    for (const [id, k] of [['in-sp', 'sp'], ['in-cv', 'cv'], ['in-kp', 'kp'], ['in-ti', 'ti'], ['in-td', 'td']]) {
        $(id).addEventListener('input', () => { sujo.add(k); if (k === 'cv') $('rg-cv').value = $(id).value; });
    }
    $('rg-cv').addEventListener('input', () => { $('in-cv').value = $('rg-cv').value; sujo.add('cv'); });

    function tudo() { banner(); pintarAtuais(); sincronizarCampos(); pintarHabilitacao(); pintarComandos(); }
    const offs = [on('status', () => { pintarAtuais(); sincronizarCampos(); pintarHabilitacao(); banner(); }), on('config', tudo), on('comandos', pintarComandos), on('comunicacao', banner)];
    tudo();
    carregarComandos();
    return () => offs.forEach((f) => f());
}
