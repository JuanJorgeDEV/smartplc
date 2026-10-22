// Tela de login, bloco "usuário" do menu e troca da própria senha.
import { sessao, entrar, sair, trocarSenha } from './sessao.js';
import { aviso } from './ui.js';

const $ = (id) => document.getElementById(id);
const MINIMO = 10;

/** Mostra a tela de login. Resolve quando o login der certo. Enquanto ela aparece, o aplicativo fica escondido. */
export function mostrarLogin({ expirada = false } = {}) {
    const tela = $('tela-login');
    const app = document.querySelector('.app');
    app.hidden = true;
    tela.hidden = false;
    document.title = 'Entrar · SmartPLC';
    $('login-aviso').textContent = expirada ? 'Sua sessão terminou. Entre novamente para continuar.' : '';
    $('login-erro').textContent = '';

    return new Promise((resolve) => {
        const form = $('f-login');
        const enviar = async (e) => {
            e.preventDefault();
            const email = $('login-email').value.trim();
            const senha = $('login-senha').value;
            if (!email || !senha) { $('login-erro').textContent = 'Informe o e-mail e a senha.'; return; }
            const botao = $('login-entrar');
            botao.disabled = true;
            $('login-erro').textContent = '';
            const r = await entrar(email, senha);
            botao.disabled = false;
            $('login-senha').value = '';
            if (!r.ok) {
                $('login-erro').textContent = r.status === 0 ? 'Não foi possível falar com o servidor. Tente de novo.' : r.erro;
                $('login-senha').focus();
                return;
            }
            form.removeEventListener('submit', enviar);
            $('login-email').value = '';
            tela.hidden = true;
            app.hidden = false;
            resolve();
        };
        form.addEventListener('submit', enviar);
        ($('login-email').value ? $('login-senha') : $('login-email')).focus();
    });
}

/** Mostra quem está logado, com "Senha" e "Sair". Some quando o login está desligado (uso local). */
export function iniciarConta() {
    const bloco = $('rail-conta');
    if (!sessao.autenticacaoAtiva || !sessao.usuario) { bloco.hidden = true; return; }
    bloco.hidden = false;
    $('conta-nome').textContent = sessao.usuario.nome;
    $('conta-papel').textContent = sessao.usuario.papelRotulo;

    $('btn-sair').addEventListener('click', sairAgora);
    $('btn-senha').addEventListener('click', abrirTrocaDeSenha);
}

export async function sairAgora() { await sair(); location.reload(); }

export function abrirTrocaDeSenha() {
    const dlg = $('dlg-senha');
    const form = $('f-senha');
    form.reset();
    $('sn-erro').textContent = '';
    dlg.showModal();
    $('sn-atual').focus();

    const fechar = () => { dlg.close(); limpar(); };
    const enviar = async (e) => {
        e.preventDefault();
        const atual = $('sn-atual').value, nova = $('sn-nova').value, conf = $('sn-conf').value;
        if (nova.length < MINIMO) { $('sn-erro').textContent = `A nova senha precisa ter pelo menos ${MINIMO} caracteres.`; return; }
        if (nova !== conf) { $('sn-erro').textContent = 'A confirmação não confere com a nova senha.'; return; }
        $('sn-salvar').disabled = true;
        const r = await trocarSenha(atual, nova);
        $('sn-salvar').disabled = false;
        if (!r.ok) { $('sn-erro').textContent = r.erro; return; }
        aviso('ok', 'Senha alterada. Suas outras sessões foram encerradas.');
        fechar();
    };
    const cancelar = (e) => { e.preventDefault(); fechar(); };
    const limpar = () => { form.removeEventListener('submit', enviar); $('sn-cancelar').removeEventListener('click', fechar); dlg.removeEventListener('cancel', cancelar); form.reset(); };
    form.addEventListener('submit', enviar);
    $('sn-cancelar').addEventListener('click', fechar);
    dlg.addEventListener('cancel', cancelar);
}
