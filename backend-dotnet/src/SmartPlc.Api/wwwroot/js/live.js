// Conexão em tempo real (SignalR). Reconecta sempre; ao voltar, ressincroniza pelo /api/status.
// Se a biblioteca não carregar (CDN bloqueado), cai para consulta periódica de /api/status.
import { setHub, setStatus, atualizarComando, ressincronizar, carregarStatus } from './store.js';
import { carregar as carregarSessao, sessao, sessaoExpirou } from './sessao.js';

const dormir = (ms) => new Promise((r) => setTimeout(r, ms));

export function conectar() {
    if (typeof signalR === 'undefined') { consultaPeriodica(); return; }

    const hub = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/telemetria')
        .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(1000 * 2 ** ctx.previousRetryCount, 10000) })
        .build();

    hub.on('telemetria_update', setStatus);
    hub.on('comando_update', atualizarComando);
    hub.onreconnecting(() => setHub('reconectando'));
    hub.onreconnected(async () => { setHub('conectado'); await ressincronizar(); });
    hub.onclose(() => { setHub('desconectado'); iniciar(); });

    async function iniciar() {
        setHub('conectando');
        for (;;) {
            try {
                await hub.start();
                setHub('conectado');
                await ressincronizar();
                return;
            } catch {
                setHub('desconectado');
                // Se o servidor recusou por falta de login (sessão vencida), não adianta insistir: avisa a tela.
                if (sessao.autenticacaoAtiva) { await carregarSessao(); if (!sessao.autenticado) { sessaoExpirou(); return; } }
                await dormir(2000);
            }
        }
    }
    iniciar();
}

function consultaPeriodica() {
    const ciclo = async () => {
        const ok = await carregarStatus();
        setHub(ok ? 'conectado' : 'desconectado');
    };
    ciclo();
    setInterval(ciclo, 2000);
}
