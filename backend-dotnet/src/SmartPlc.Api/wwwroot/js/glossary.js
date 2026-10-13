// Explicações curtas e CONCEITUAIS (conteúdo estático, sem IA). Não prometem um comportamento específico:
// o que acontece de fato depende de como o PID foi implementado no CLP.

export const TERMOS = {
    clp: ['CLP', 'Controlador lógico programável: o computador industrial que lê os sensores, executa a lógica de controle e comanda os atuadores. Neste projeto é um CLP Siemens simulado (PLCSIM).'],
    processo: ['Processo', 'O que está sendo controlado. Aqui, o nível de líquido do tanque TQ-101, que sobe quando a válvula de entrada abre.'],
    pv: ['PV', 'Variável de processo: o valor medido. Aqui, o nível do tanque, em %.'],
    sp: ['SP', 'Setpoint: o valor desejado para a PV. O controlador tenta reduzir a diferença entre PV e SP.'],
    cv: ['CV', 'Variável de controle: a saída do controlador. Aqui, a abertura da válvula de entrada FV-101, em %.'],
    malha: ['Malha fechada', 'O controlador mede a PV, compara com o SP e ajusta a CV; o efeito é medido de novo, e o ciclo se repete.'],
    pid: ['PID', 'Controlador que combina três ações sobre o erro (SP − PV): proporcional (Kp), integral (Ti) e derivativa (Td).'],
    kp: ['Kp', 'Ganho proporcional: intensidade da reação ao erro. Em geral, um Kp maior reage mais forte, mas pode causar oscilação.'],
    ti: ['Ti', 'Tempo integral: ação que considera o erro acumulado ao longo do tempo. Em geral, um Ti menor acumula mais rápido e corrige o erro residual com mais agressividade.'],
    td: ['Td', 'Tempo derivativo: ação que considera a velocidade de variação do erro. Antecipa a tendência, mas amplifica ruído.'],
    automatico: ['Modo automático', 'O PID calcula a abertura da válvula (CV) para levar a PV ao SP.'],
    manual: ['Modo manual', 'A válvula segue a abertura definida por uma pessoa; o PID não atua e a PV deixa de ser regulada.'],
    degrau: ['Degrau', 'Mudança brusca do setpoint de um valor para outro. É o teste clássico para observar a resposta de uma malha.'],
    t63: ['Tempo até 63 %', 'Tempo para a PV percorrer 63,2 % do caminho até o novo SP. Em um sistema simples de primeira ordem, equivale à constante de tempo.'],
    sobreelevacao: ['Sobre-elevação', 'Quanto a PV ultrapassa o novo SP antes de assentar, em % do tamanho do degrau. Zero significa que chegou sem passar.'],
    estabilizacao: ['Estabilização', 'Tempo de acomodação: quando a PV entra e permanece numa banda estreita ao redor do SP (aqui, ±2 % do degrau).'],
    telemetria: ['Telemetria', 'Leituras enviadas pelo Node-RED, que por sua vez as lê do CLP via protocolo S7.'],
    historico: ['Histórico', 'Amostras e eventos gravados no PostgreSQL. A gravação é seletiva (periódica, por variação e por evento), não toda leitura.']
};

/** HTML de um termo clicável/focável que abre a explicação curta. */
export function termo(chave, texto) {
    const t = TERMOS[chave];
    if (!t) return texto ?? chave;
    return `<button type="button" class="term" data-termo="${chave}">${texto ?? t[0]}</button>`;
}
