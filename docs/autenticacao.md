# Autenticação (login de funcionários)

O painel exige login. Cada pessoa tem o próprio usuário, com papel, e todas as rotas são **negadas por padrão**: só são abertas `/health`, `/api/auth/*` (login, saída, quem sou eu), os arquivos estáticos da tela de entrada e `POST /api/telemetria` (que continua protegida pela chave de ingestão `X-Api-Key`, porque o Node-RED não usa cookie).

## Papéis

| Papel | Pode |
|---|---|
| `admin` | tudo, inclusive criar, desativar e redefinir senhas de usuários (aba Integrações → Usuários) |
| `operador` | consultar, enviar comandos (quando `Control__Enabled=true`) e criar/encerrar sessões do Laboratório |
| `visualizador` | somente consultar (Visão Geral, Histórico, sessões já registradas) |

## Como funciona

- **Senha:** nunca é guardada. Só o hash PBKDF2-HMAC-SHA256 (600 000 iterações, sal aleatório por senha) em `app_users`. Hashes antigos com menos iterações são refeitos no próximo login. Regras: 10 a 128 caracteres, sem lista de senhas óbvias, diferente do e-mail.
- **Sessão:** cookie `smartplc.sid`, `HttpOnly` (o JavaScript não o lê), `SameSite=Strict`, `Secure` fora de Development, de sessão (some ao fechar o navegador) e com **duração absoluta de 8 h** (`Auth__SessionHours`).
- **Revogação:** trocar a senha, mudar o papel ou desativar a conta muda o "carimbo de segurança" do usuário e derruba as sessões abertas em até 20 s (na hora, na instância que fez a mudança).
- **Chaves do cookie:** guardadas no banco (`data_protection_keys`), para as sessões sobreviverem a reinícios do Render. Desligável com `Auth__PersistKeysInDatabase=false` (as sessões caem a cada reinício).
- **Força bruta:** 5 senhas erradas seguidas bloqueiam a conta por 15 minutos (`Auth__MaxFailures`, `Auth__LockoutMinutes`), e há um limite de 20 tentativas por IP a cada 5 minutos. Toda falha de login devolve a **mesma** mensagem, para não revelar se o e-mail existe, se a conta está bloqueada ou se a senha estava errada, e o tempo de resposta é igualado calculando um hash mesmo para e-mails inexistentes.
- **Origem:** pedidos que alteram dados e trazem `Origin` de outro site são recusados (defesa extra ao `SameSite`).
- **Cabeçalhos:** `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: same-origin`.
- **Banco fora do ar:** quem já está logado continua (o cookie é assinado e tem prazo); novos logins recebem 503.
- **Nunca** remove o último administrador nem deixa alguém desativar a própria conta.

## Primeiro acesso

Defina no ambiente (Render: *Environment*, nunca no Git): `Auth__AdminEmail` e `Auth__AdminPassword`. Na primeira subida, se **não existir nenhum usuário**, o administrador é criado com esses dados. Entre, troque a senha em *Integrações → Minha conta* e **apague `Auth__AdminPassword`** do ambiente. Esquecimento da senha do último administrador: apague a linha em `app_users` (SQL Editor do Supabase) e suba o serviço de novo com as variáveis acima.

## Ligar e desligar

`Auth__Enabled` vazio decide pelo ambiente: **ligado fora de Development**, desligado em Development (uso local, sem banco). Ligado, o servidor **não sobe** sem banco configurado. Desligado, todos são administradores locais e o painel mostra isso; só faça fora de Development numa máquina própria (o servidor registra um aviso).

## Com login desligado

`X-Operator-Key` (`Control__OperatorApiKey`) continua valendo para comandos, como antes. Com login ligado, o papel substitui a chave de operador.

## O que não está coberto

Recuperação de senha por e-mail, autenticação em dois fatores, login social e auditoria detalhada (quem fez cada comando). Ficam para depois, se o projeto precisar.
