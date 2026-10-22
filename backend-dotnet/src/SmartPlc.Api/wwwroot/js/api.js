// Chamadas HTTP ao backend. Nunca lança: devolve { ok, status, data, erro }.
// Nenhuma credencial é guardada aqui: a sessão é um cookie HttpOnly. Uma resposta 401 fora do login avisa que a sessão acabou.
let aoExpirar = null;
export const definirAoExpirar = (fn) => { aoExpirar = fn; };

async function chamar(metodo, caminho, corpo, tempoMs = 8000) {
    const controle = new AbortController();
    const timer = setTimeout(() => controle.abort(), tempoMs);
    try {
        const resp = await fetch(caminho, {
            method: metodo,
            headers: corpo === undefined ? undefined : { 'Content-Type': 'application/json' },
            body: corpo === undefined ? undefined : JSON.stringify(corpo),
            signal: controle.signal
        });
        let data = null;
        const texto = await resp.text();
        if (texto) { try { data = JSON.parse(texto); } catch { data = null; } }
        if (resp.status === 401 && !caminho.startsWith('/api/auth/login') && !caminho.startsWith('/api/auth/me')) aoExpirar?.();
        return { ok: resp.ok, status: resp.status, data, erro: resp.ok ? null : mensagemDe(resp.status, data) };
    } catch (e) {
        const msg = e.name === 'AbortError' ? 'o servidor não respondeu a tempo' : 'falha de rede ao contactar o servidor';
        return { ok: false, status: 0, data: null, erro: msg };
    } finally {
        clearTimeout(timer);
    }
}

function mensagemDe(status, data) {
    if (data?.erros?.length) return data.erros.map((e) => `${e.campo === '$' ? '' : e.campo + ': '}${e.mensagem}`).join('; ');
    if (data?.mensagem) return data.mensagem;
    return `erro ${status}`;
}

export const get = (caminho, tempoMs) => chamar('GET', caminho, undefined, tempoMs);
export const post = (caminho, corpo, tempoMs) => chamar('POST', caminho, corpo ?? {}, tempoMs);
export const patch = (caminho, corpo, tempoMs) => chamar('PATCH', caminho, corpo ?? {}, tempoMs);

export function qs(params) {
    const p = new URLSearchParams();
    for (const [k, v] of Object.entries(params)) if (v !== null && v !== undefined && v !== '') p.set(k, v);
    const s = p.toString();
    return s ? `?${s}` : '';
}
