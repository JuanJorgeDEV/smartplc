const express = require('express');
const http = require('http');
const { Server } = require('socket.io');
const cors = require('cors');

const app = express();
const server = http.createServer(app);

app.use(cors());
app.use(express.json());

// Serve os arquivos do Front-end (public/index.html, style.css, app.js)
app.use(express.static('public'));

const io = new Server(server, { cors: { origin: "*", methods: ["GET", "POST"] } });

const NODE_RED_URL = 'http://127.0.0.1:1880';

let estadoProcesso = {
    telemetria: {
        nivel: 0.0,
        valvula: 0.0,
        leds: { low: false, normal: true, high: false },
        timestamp: new Date().toISOString()
    },
    controle: {
        setpoint: 0.0,
        modoManual: false,
        valvulaManual: 0.0,
        kp: 1.0,
        ti: 20.0,
        td: 0.0
    }
};

// Rota de receção da telemetria (Node-RED -> Back-end)
app.post('/api/telemetria', (req, res) => {
    const { nivel, valvula, setpoint, modoManual, valvulaManual, kp, ti, td } = req.body;

    const nivelNum = parseFloat(nivel) || 0.0;
    const valvulaNum = parseFloat(valvula) || 0.0;

    estadoProcesso.telemetria = {
        nivel: Number(nivelNum.toFixed(2)),
        valvula: Number(valvulaNum.toFixed(2)),
        leds: {
            low: nivelNum < 10.0,
            normal: nivelNum >= 10.0 && nivelNum <= 90.0,
            high: nivelNum > 90.0
        },
        timestamp: new Date().toISOString()
    };

    if (setpoint !== undefined && setpoint !== null) estadoProcesso.controle.setpoint = parseFloat(setpoint);
    if (modoManual !== undefined && modoManual !== null) estadoProcesso.controle.modoManual = Boolean(modoManual);
    if (valvulaManual !== undefined && valvulaManual !== null) estadoProcesso.controle.valvulaManual = parseFloat(valvulaManual);
    if (kp !== undefined && kp !== null) estadoProcesso.controle.kp = parseFloat(kp);
    if (ti !== undefined && ti !== null) estadoProcesso.controle.ti = parseFloat(ti);
    if (td !== undefined && td !== null) estadoProcesso.controle.td = parseFloat(td);

    io.emit('telemetria_update', estadoProcesso);
    res.status(200).json({ status: 'ok' });
});

// Rota de envio de comandos com proteção contra travamento (Timeout de 2s)
app.post('/api/controle', async (req, res) => {
    const { setpoint, modoManual, valvulaManual, kp, ti, td } = req.body;

    // Atualiza estado local no servidor
    if (setpoint !== undefined) estadoProcesso.controle.setpoint = parseFloat(setpoint);
    if (modoManual !== undefined) estadoProcesso.controle.modoManual = Boolean(modoManual);
    if (valvulaManual !== undefined) estadoProcesso.controle.valvulaManual = parseFloat(valvulaManual);
    if (kp !== undefined) estadoProcesso.controle.kp = parseFloat(kp);
    if (ti !== undefined) estadoProcesso.controle.ti = parseFloat(ti);
    if (td !== undefined) estadoProcesso.controle.td = parseFloat(td);

    // Notifica o dashboard Web via Socket.io
    io.emit('telemetria_update', estadoProcesso);

    // Configura tempo limite de 2 segundos para a comunicação com o Node-RED
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), 2000);

    try {
        const response = await fetch(`${NODE_RED_URL}/api/comando-clp`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(req.body),
            signal: controller.signal
        });

        clearTimeout(timeoutId);

        if (response.ok) {
            res.status(200).json({ status: 'ok', controle: estadoProcesso.controle });
        } else {
            res.status(500).json({ status: 'erro', mensagem: 'Node-RED não aceitou o comando' });
        }
    } catch (error) {
        clearTimeout(timeoutId);
        console.error('[ERRO] Falha ao comunicar com o Node-RED:', error.message);
        res.status(500).json({ status: 'erro', mensagem: 'Node-RED inacessível ou timeout' });
    }
});

app.get('/api/status', (req, res) => res.json(estadoProcesso));

const PORT = 3000;
server.listen(PORT, () => {
    console.log(`Servidor Back-end Ativo em http://localhost:${PORT}`);
});