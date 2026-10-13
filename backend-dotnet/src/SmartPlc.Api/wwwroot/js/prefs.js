// Preferências de APRESENTAÇÃO (nomes amigáveis, casas decimais, o que aparece em destaque).
// Ficam no localStorage deste navegador: não vão ao servidor, não são compartilhadas entre pessoas ou aparelhos e
// NÃO alteram endereços do CLP nem a política de gravação do histórico.

const CHAVE = 'smartplc.prefs.v1';

export const PADRAO = Object.freeze({
    variaveis: {
        nivel: { nome: '', casas: 1, destaque: true },
        valvula: { nome: '', casas: 1, destaque: true },
        setpoint: { nome: '', casas: 1, destaque: true },
        modoManual: { nome: '', casas: 0, destaque: true },
        kp: { nome: '', casas: 2, destaque: false },
        ti: { nome: '', casas: 1, destaque: false },
        td: { nome: '', casas: 1, destaque: false },
        valvulaManual: { nome: '', casas: 1, destaque: false }
    },
    indicadores: { tendencia: true, alarmes: true, resumo: true }
});

const copia = (o) => JSON.parse(JSON.stringify(o));
let atual = null;
const ouvintes = new Set();

function mesclar(base, salvo) {
    const r = copia(base);
    if (!salvo || typeof salvo !== 'object') return r;
    for (const campo of Object.keys(r.variaveis)) {
        const s = salvo.variaveis?.[campo];
        if (!s) continue;
        if (typeof s.nome === 'string') r.variaveis[campo].nome = s.nome.slice(0, 40);
        if (Number.isInteger(s.casas) && s.casas >= 0 && s.casas <= 3) r.variaveis[campo].casas = s.casas;
        if (typeof s.destaque === 'boolean') r.variaveis[campo].destaque = s.destaque;
    }
    for (const k of Object.keys(r.indicadores)) if (typeof salvo.indicadores?.[k] === 'boolean') r.indicadores[k] = salvo.indicadores[k];
    return r;
}

export function prefs() {
    if (atual) return atual;
    let salvo = null;
    try { salvo = JSON.parse(localStorage.getItem(CHAVE) ?? 'null'); } catch { /* sem armazenamento: usa o padrão */ }
    atual = mesclar(PADRAO, salvo);
    return atual;
}

export function salvar(novo) {
    atual = mesclar(PADRAO, novo);
    try { localStorage.setItem(CHAVE, JSON.stringify(atual)); } catch { /* modo privado: vale só nesta sessão */ }
    ouvintes.forEach((f) => f(atual));
}

export function restaurar() {
    atual = copia(PADRAO);
    try { localStorage.removeItem(CHAVE); } catch { /* ignora */ }
    ouvintes.forEach((f) => f(atual));
}

export function aoMudar(fn) { ouvintes.add(fn); return () => ouvintes.delete(fn); }

export const nomeDe = (campo, padrao) => prefs().variaveis[campo]?.nome?.trim() || padrao;
export const casasDe = (campo) => prefs().variaveis[campo]?.casas ?? 1;
