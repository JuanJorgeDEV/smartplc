// Sessão do usuário (login). O navegador NUNCA guarda senha, token ou chave: a sessão é um cookie HttpOnly que o servidor define,
// e que o JavaScript não consegue ler. Aqui só se guarda quem está logado (nome e papel), para ajustar a interface.
import * as api from './api.js';

export const sessao = {
    carregada: false,
    autenticacaoAtiva: false,
    autenticado: false,
    usuario: null          // { id, email, nome, papel, papelRotulo }
};

export async function carregar() {
    const r = await api.get('/api/auth/me', 6000);
    if (r.ok) {
        sessao.autenticacaoAtiva = !!r.data.autenticacaoAtiva;
        sessao.autenticado = !!r.data.autenticado;
        sessao.usuario = r.data.usuario ?? null;
        sessao.carregada = true;
    }
    return r.ok;
}

/** Papéis: administrador e operador comandam e usam o Laboratório; visualizador só consulta. */
export const podeOperar = () => !sessao.autenticacaoAtiva || ['admin', 'operador'].includes(sessao.usuario?.papel);
export const ehAdmin = () => !sessao.autenticacaoAtiva || sessao.usuario?.papel === 'admin';

export async function entrar(email, senha) {
    const r = await api.post('/api/auth/login', { email, senha }, 15000);
    if (r.ok) { sessao.autenticado = true; sessao.usuario = r.data.usuario; }
    return r;
}

export async function sair() {
    await api.post('/api/auth/logout', {}, 8000);
    sessao.autenticado = false;
    sessao.usuario = null;
}

export const trocarSenha = (senhaAtual, novaSenha) => api.post('/api/auth/senha', { senhaAtual, novaSenha }, 15000);

let avisouExpiracao = false;
/** Chamado quando uma chamada autenticada recebe 401: a sessão acabou (prazo, troca de senha ou desativação). */
export function sessaoExpirou() {
    if (!sessao.autenticacaoAtiva || avisouExpiracao) return;
    avisouExpiracao = true;
    window.dispatchEvent(new CustomEvent('smartplc:sessao-expirada'));
}
