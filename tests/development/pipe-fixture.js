'use strict';
// In-memory app protocol fixture. No runtime state is exchanged through files.
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const { randomUUID } = require('node:crypto');

const endpoint = pipe => process.platform === 'win32' ? '\\\\.\\pipe\\' + pipe : path.join(os.tmpdir(), 'CoreFxPipe_' + pipe);

async function serve(read, command = () => ({ result: 0 })) {
    const pipe = 'sunfleck-test-' + randomUUID();
    const sockets = new Set();
    const server = net.createServer(socket => {
        sockets.add(socket);
        socket.on('close', () => sockets.delete(socket));
        socket.on('error', () => {});
        let buffer = '';
        socket.on('data', data => {
            buffer += data.toString('utf8');
            if (!buffer.includes('\n')) { return; }
            try {
                const request = JSON.parse(buffer.slice(0, buffer.indexOf('\n')));
                const result = request.method === 'snapshot' ? read() : command(request);
                socket.end(JSON.stringify({ result }) + '\n');
            } catch (error) { socket.end(JSON.stringify({ error: error.message }) + '\n'); }
        });
    });
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen(endpoint(pipe), resolve); });
    return { pipe, close: async () => {
        for (const socket of sockets) { socket.destroy(); }
        await new Promise(resolve => server.close(resolve));
    } };
}

function snapshot(pipe) {
    return new Promise((resolve, reject) => {
        const socket = net.connect(endpoint(pipe));
        const timer = setTimeout(() => socket.destroy(new Error('Debug pipe timed out')), 1500);
        let buffer = '';
        socket.on('connect', () => socket.write('{"method":"snapshot"}\n'));
        socket.on('error', reject);
        socket.on('close', () => clearTimeout(timer));
        socket.on('data', data => {
            buffer += data.toString('utf8');
            if (!buffer.includes('\n')) { return; }
            try {
                const reply = JSON.parse(buffer.slice(0, buffer.indexOf('\n')));
                if (reply.error) { throw new Error(reply.error); }
                resolve(reply.result);
            } catch (error) { reject(error); }
            socket.destroy();
        });
    });
}

module.exports = { endpoint, serve, snapshot };
