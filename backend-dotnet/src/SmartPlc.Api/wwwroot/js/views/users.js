// Administração de usuários (somente para administradores). Nenhuma senha é exibida ou guardada aqui: a senha provisória
// é digitada uma vez, enviada ao servidor (que guarda só o hash) e some da tela.
import * as api from '../api.js';
import { sessao } from '../sessao.js';
import { dataHora, esc } from '../format.js';
import { aviso, confirmar } from '../ui.js';

const PAPEIS = [['admin', 'Administrador'], ['operador', 'Operador'], ['visualizador', 'Visualizador']];

export function html() {
    return `
  <section aria-labelledby="i-usr" id="sec-usuarios">
    <div class="sec-head"><h2 id="i-usr" class="eyebrow">Usuários</h2><span class="badge" id="n-usr"></span></div>
    <div class="table-wrap"><table class="tb-usr">
      <caption class="sr-only">Usuários com acesso ao SmartPLC</caption>
      <thead><tr><th scope="col">Nome</th><th scope="col">E-mail</th><th scope="col">Papel</th><th scope="col">Situação</th><th scope="col">Último acesso</th><th scope="col">Ações</th></tr></thead>
      <tbody id="tb-usr"></tbody></table></div>
    <p class="error" id="e-usr" role="alert"></p>
    <form class="usr-novo" id="f-usr" novalidate>
      <h3 class="eyebrow">Novo usuário</h3>
      <div class="usr-campos">
        <div class="field"><label for="nu-nome">Nome</label><input class="input" id="nu-nome" maxlength="80" autocomplete="off"></div>
        <div class="field"><label for="nu-email">E-mail</label><input class="input" id="nu-email" type="email" maxlength="200" autocomplete="off" autocapitalize="none"></div>
        <div class="field"><label for="nu-papel">Papel</label><select class="input" id="nu-papel">${PAPEIS.map(([v, r]) => `<option value="${v}" ${v === 'operador' ? 'selected' : ''}>${r}</option>`).join('')}</select></div>
        <div class="field"><label for="nu-senha">Senha provisória</label><input class="input" id="nu-senha" type="password" maxlength="128" autocomplete="new-password"><span class="hint">Pelo menos 10 caracteres. Passe-a à pessoa por um canal privado.</span></div>
      </div>
      <button type="submit" class="btn primary" id="nu-criar">Criar usuário</button>
    </form>
    <p class="note"><strong>Papéis.</strong> Administrador: tudo, inclusive esta lista. Operador: envia comandos (quando habilitados) e usa o Laboratório. Visualizador: somente consulta. Após 5 senhas erradas seguidas a conta fica bloqueada por 15 minutos; redefinir a senha a desbloqueia.</p>
  </section>`;
}

export function montar(raiz) {
    const $ = (id) => raiz.querySelector(`#${id}`);
    let lista = [];

    const erro = (t) => { $('e-usr').textContent = t ?? ''; };

    async function carregar() {
        const r = await api.get('/api/usuarios', 8000);
        if (!r.ok) { erro(`Não foi possível listar os usuários: ${r.erro}.`); return; }
        lista = r.data.usuarios;
        pintar();
    }

    function pintar() {
        $('n-usr').textContent = `${lista.length} ${lista.length === 1 ? 'usuário' : 'usuários'}`;
        const eu = sessao.usuario?.id;
        $('tb-usr').innerHTML = lista.map((u) => {
            const proprio = u.id === eu;
            const situacao = !u.ativo ? '<span class="tag off">Desativado</span>' : u.bloqueado ? '<span class="tag alarm">Bloqueado</span>' : '<span class="tag ok">Ativo</span>';
            return `<tr data-id="${u.id}">
              <th scope="row" class="item">${esc(u.nome)}${proprio ? ' <span class="muted">(você)</span>' : ''}</th>
              <td class="mono">${esc(u.email)}</td>
              <td><label class="sr-only" for="pp-${u.id}">Papel de ${esc(u.nome)}</label><select class="input mini" id="pp-${u.id}" data-papel>${PAPEIS.map(([v, r]) => `<option value="${v}" ${u.papel === v ? 'selected' : ''}>${r}</option>`).join('')}</select></td>
              <td>${situacao}</td>
              <td class="num">${u.ultimoLoginEm ? dataHora(u.ultimoLoginEm) : '—'}</td>
              <td class="usr-acoes"><button type="button" class="btn small" data-senha>Redefinir senha</button> <button type="button" class="btn small ${u.ativo ? 'danger quiet' : ''}" data-ativo ${proprio && u.ativo ? 'disabled title="Você não pode desativar a própria conta"' : ''}>${u.ativo ? 'Desativar' : 'Reativar'}</button></td>
            </tr>`;
        }).join('');
    }

    async function alterar(id, corpo, ok) {
        erro('');
        const r = await api.patch(`/api/usuarios/${id}`, corpo, 10000);
        if (!r.ok) { erro(r.erro); await carregar(); return false; }
        aviso('ok', ok);
        await carregar();
        return true;
    }

    raiz.addEventListener('change', (e) => {
        const sel = e.target.closest('[data-papel]');
        if (!sel) return;
        const id = sel.closest('tr').dataset.id;
        alterar(id, { papel: sel.value }, 'Papel alterado. A pessoa precisa entrar de novo.');
    });

    raiz.addEventListener('click', async (e) => {
        const linha = e.target.closest('tr[data-id]');
        if (!linha) return;
        const u = lista.find((x) => x.id === linha.dataset.id);
        if (!u) return;

        if (e.target.closest('[data-ativo]')) {
            const desativar = u.ativo;
            const ok = await confirmar({
                titulo: desativar ? 'Desativar o acesso?' : 'Reativar o acesso?',
                corpo: desativar ? `<strong>${esc(u.nome)}</strong> será desconectado agora e não poderá entrar até ser reativado.` : `<strong>${esc(u.nome)}</strong> poderá entrar de novo com a senha atual.`,
                confirmarTexto: desativar ? 'Desativar' : 'Reativar'
            });
            if (ok) alterar(u.id, { ativo: !u.ativo }, desativar ? 'Acesso desativado.' : 'Acesso reativado.');
        } else if (e.target.closest('[data-senha]')) {
            const ok = await confirmar({
                titulo: 'Redefinir a senha',
                corpo: `<p>Nova senha provisória para <strong>${esc(u.nome)}</strong> (pelo menos 10 caracteres). As sessões dela serão encerradas.</p><div class="field"><label for="rs-senha">Nova senha</label><input class="input" id="rs-senha" type="password" maxlength="128" autocomplete="new-password"></div>`,
                confirmarTexto: 'Redefinir'
            });
            const campo = document.getElementById('rs-senha');
            const senha = campo?.value ?? '';
            if (campo) campo.value = '';
            if (ok) alterar(u.id, { novaSenha: senha }, 'Senha redefinida. Passe a nova senha por um canal privado.');
        }
    });

    $('f-usr').addEventListener('submit', async (e) => {
        e.preventDefault();
        erro('');
        const corpo = { nome: $('nu-nome').value.trim(), email: $('nu-email').value.trim(), papel: $('nu-papel').value, senha: $('nu-senha').value };
        if (!corpo.nome || !corpo.email || !corpo.senha) { erro('Preencha nome, e-mail e senha provisória.'); return; }
        $('nu-criar').disabled = true;
        const r = await api.post('/api/usuarios', corpo, 15000);
        $('nu-criar').disabled = false;
        $('nu-senha').value = '';
        if (!r.ok) { erro(r.erro); return; }
        $('nu-nome').value = ''; $('nu-email').value = '';
        aviso('ok', 'Usuário criado.');
        await carregar();
    });

    carregar();
}
