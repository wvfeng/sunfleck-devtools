'use strict';
// Actual installed extension + coreclr host: open editor BEFORE launching app.
const vscode = require('vscode');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const net = require('node:net');

exports.run = async function () {
    const root = path.resolve(__dirname, '../../..');
    const output = process.env.SUNFLECK_TEST_OUTPUT;
    const config = vscode.workspace.getConfiguration('sunfleck');
    const pipe = config.get('debugPipe');
    const target = { pipe, owner: 'pipe-regression' };
    const result = { passed: [], errors: [], sessions: [] };
    const observed = [];
    let client, prototype, original, debugSession, silentServer;
    const ended = new Set();
    const subscription = vscode.debug.onDidTerminateDebugSession(session => ended.add(session.id));
    const tracker = vscode.debug.registerDebugAdapterTrackerFactory('coreclr', {
        createDebugAdapterTracker: session => ({ onDidSendMessage: message => {
            if (message.type === 'event' || message.success === false) {
                fs.appendFileSync(path.join(output, 'debug-protocol.jsonl'), JSON.stringify({ session: session.id, ...message }) + '\n');
            }
        } })
    });
    const until = async (read, accept, label, timeout = 30000) => {
        const deadline = Date.now() + timeout;
        while (Date.now() < deadline) {
            const value = await read();
            if (accept(value)) { return value; }
            await new Promise(resolve => setTimeout(resolve, 100));
        }
        throw new Error(label);
    };
    try {
        const extension = vscode.extensions.getExtension('sunfleck.sunfleck');
        assert.ok(extension);
        await extension.activate();
        result.extension = { path: extension.extensionPath, version: extension.packageJSON.version };
        const modulePath = path.join(extension.extensionPath, 'node_modules/vscode-languageclient/lib/node/main.js');
        const loaded = Object.values(require.cache).find(entry => path.resolve(entry.filename).toLowerCase() === path.resolve(modulePath).toLowerCase());
        assert.ok(loaded, 'Installed language client module was not loaded');
        prototype = loaded.exports.LanguageClient.prototype;
        original = prototype.sendRequest;
        prototype.sendRequest = function (method, params, ...rest) {
            const pending = original.call(this, method, params, ...rest);
            if ((typeof method === 'string' ? method : method.method) !== 'sunfleck/debugSnapshot' || params.pipe !== pipe) { return pending; }
            client = this;
            return pending.then(value => { observed.push({ value, owner: params.owner }); return value; }, error => {
                observed.push({ error: error.message, owner: params.owner }); throw error;
            });
        };
        await until(() => observed.filter(entry => entry.error).length, n => n >= 6, 'Missing-app polls did not continue');
        assert.ok(client.isRunning());
        let servicePid = client._serverProcess.pid;
        result.servicePid = servicePid;
        result.passed.push('six-missing-app-polls-without-service-crash');
        const source = path.join(root, 'tests/development/fixtures/legacy-demos/counter/main.fleck');
        const languageFile = path.join(vscode.workspace.workspaceFolders[0].uri.fsPath, 'language.fleck');
        fs.writeFileSync(languageFile, fs.readFileSync(source, 'utf8').replace(/>\s+</g, '><'));
        const languageDocument = await vscode.workspace.openTextDocument(languageFile);
        await vscode.window.showTextDocument(languageDocument);
        const binding = languageDocument.getText().indexOf('{Binding ');
        assert.ok(binding >= 0);
        const completions = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', languageDocument.uri, languageDocument.positionAt(binding + 9));
        assert.ok(completions.items.some(item => (item.label.label || item.label) === 'Caption'));
        assert.ok(Array.isArray(await vscode.commands.executeCommand('vscode.executeFormatDocumentProvider', languageDocument.uri, { tabSize: 2, insertSpaces: true })));
        result.passed.push('completion-and-formatting-available-before-app');
        const document = await vscode.workspace.openTextDocument(source);

        for (let generation = 0; generation < Number(process.env.SUNFLECK_TEST_GENERATIONS || 2); generation++) {
            const start = observed.length;
            assert.ok(await vscode.debug.startDebugging(vscode.workspace.workspaceFolders[0], {
                name: 'Counter Pipe regression', type: 'coreclr', request: 'launch',
                program: path.join(root, 'tests/development/fixtures/legacy-demos/counter/bin/Debug/net10.0/Counter.dll'),
                cwd: path.join(root, 'tests/development/fixtures/legacy-demos/counter'), console: 'internalConsole',
                env: { SUNFLECK_DEBUG_PIPE: pipe }
            }));
            debugSession = await until(() => vscode.debug.activeDebugSession, Boolean, 'Missing debug session');
            const entry = await until(() => observed.slice(start).find(entry => entry.value?.windows?.length && entry.value.nodes.length), Boolean, 'Automatic inspector connection failed');
            const frame = entry.value;
            result.sessions.push(frame.debugSession);
            assert.equal(client._serverProcess.pid, servicePid);
            const button = frame.nodes.find(node => node.type === 'Button' && node.source?.range);
            assert.ok(button);
            const at = new vscode.Position(button.source.range.line - 1, button.source.range.column - 1);
            await vscode.window.showTextDocument(document, { selection: new vscode.Range(at, at) });
            assert.equal(await vscode.commands.executeCommand('sunfleck.locateRuntimeObject', button.id), button.id);
            for (const method of ['pick', 'cancelPick']) {
                const reply = await client.sendRequest('sunfleck/debugRequest', { ...target, method });
                assert.equal(reply.result, 0);
            }
            // The server must survive a client that disappears mid-request.
            await until(() => new Promise((resolve, reject) => {
                const socket = net.connect('\\\\.\\pipe\\' + pipe, () => { socket.write('{'); socket.destroy(); resolve(true); });
                socket.once('error', error => ['ENOENT', 'EBUSY'].includes(error.code) ? resolve(false) : reject(error));
            }), Boolean, 'Could not connect abrupt-disconnect probe');
            const afterDisconnect = observed.length;
            await until(() => observed.slice(afterDisconnect).some(entry => entry.value), Boolean, 'Pipe listener died after disconnect');
            result.passed.push(`generation-${generation + 1}-coreclr-auto-connect-locate-pick-disconnect-recovery`);
            if (generation === 0) {
                const beforeRestart = observed.length;
                await vscode.commands.executeCommand('sunfleck.restartLanguageServer');
                await until(() => observed.slice(beforeRestart).find(entry => entry.value?.debugSession === frame.debugSession), Boolean,
                    'Explicit service restart did not restore the active app');
                assert.notEqual(client._serverProcess.pid, servicePid);
                servicePid = client._serverProcess.pid;
                const beforeCrash = observed.length;
                client._serverProcess.kill();
                await until(() => observed.slice(beforeCrash).find(entry => entry.value?.debugSession === frame.debugSession), Boolean,
                    'Service crash did not automatically restore the active app');
                assert.notEqual(client._serverProcess.pid, servicePid);
                servicePid = client._serverProcess.pid;
                result.passed.push('explicit-service-restart-and-crash-recovery-preserve-app-session');
            }
            const afterStop = observed.length;
            if (generation === 0) { process.kill(frame.processId); }
            else { await vscode.debug.stopDebugging(debugSession); }
            await until(() => ended.has(debugSession.id), Boolean, 'Debugger did not stop');
            debugSession = undefined;
            await until(() => observed.slice(afterStop).some(entry => entry.error), Boolean, 'Inspector did not observe app exit');
            if (generation === 0) { result.passed.push('forced-app-exit-ends-debugger-and-next-generation-reconnects'); }
        }
        assert.equal(new Set(result.sessions).size, result.sessions.length, 'Restart reused a retired application session');
        assert.equal(new Set(observed.map(entry => entry.owner)).size, 1, 'Inspector was reopened instead of reconnecting');
        assert.equal(client._serverProcess.pid, servicePid);
        result.passed.push('same-inspector-and-language-service-reconnect-to-new-app-session');

        // Stand-in for an app paused by the debugger: accepts but never replies.
        const sockets = new Set();
        silentServer = net.createServer(socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => {}); });
        await new Promise((resolve, reject) => { silentServer.once('error', reject); silentServer.listen('\\\\.\\pipe\\' + pipe, resolve); });
        const began = Date.now();
        await assert.rejects(client.sendRequest('sunfleck/debugSnapshot', target));
        assert.ok(Date.now() - began < 4000, 'Silent pipe blocked language service');
        for (const socket of sockets) { socket.destroy(); }
        await new Promise(resolve => silentServer.close(resolve));
        silentServer = undefined;
        assert.equal(client._serverProcess.pid, servicePid);
        await client.sendRequest('sunfleck/debugDisconnect', target);
        result.passed.push('silent-pipe-bounded-timeout-and-language-service-survival');
        await vscode.commands.executeCommand('sunfleck.closeSnapshot');
        const closedPolls = observed.length;
        await new Promise(resolve => setTimeout(resolve, 1200));
        assert.equal(observed.length, closedPolls, 'Closed inspector kept polling');
        const oldOwner = observed.find(entry => entry.owner !== target.owner).owner;
        await vscode.commands.executeCommand('sunfleck.inspectSnapshot', { pipe });
        await until(() => observed.slice(closedPolls).find(entry => entry.owner !== oldOwner), Boolean,
            'Reopened inspector retained its retired owner');
        result.passed.push('inspector-close-stops-polling-and-reopen-creates-new-owner');

        // Exercise the real language client's crash budget while inspector polls continue.
        await vscode.commands.executeCommand('sunfleck.restartLanguageServer');
        await until(() => client?.isRunning() && client._serverProcess?.pid !== servicePid, Boolean, 'Restart did not create a new service');
        for (let crash = 0; crash < 5; crash++) {
            const crashed = client._serverProcess;
            crashed.kill();
            if (crash < 4) {
                await until(() => client.isRunning() && client._serverProcess?.pid !== crashed.pid, Boolean, 'Service did not recover within its crash budget');
            } else {
                await until(() => !client.isRunning(), Boolean, 'Service ignored exhausted crash budget');
                await new Promise(resolve => setTimeout(resolve, 2500));
                assert.equal(client.isRunning(), false, 'Inspector polling bypassed the crash budget');
                assert.ok(!client._serverProcess || client._serverProcess.pid === crashed.pid, 'A new service was spawned after the crash budget');
            }
        }
        const stoppedClient = client;
        await vscode.commands.executeCommand('sunfleck.restartLanguageServer');
        await until(() => client !== stoppedClient && client.isRunning(), Boolean, 'Explicit restart did not clear crash stop');
        const restored = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', languageDocument.uri, languageDocument.positionAt(binding + 9));
        assert.ok(restored.items.some(item => (item.label.label || item.label) === 'Caption'));
        result.passed.push('five-service-crashes-stop-automatic-recovery-and-explicit-restart-restores-completion');
        await vscode.commands.executeCommand('sunfleck.closeSnapshot');
    } catch (error) {
        result.errors.push(error.stack || String(error));
        throw error;
    } finally {
        if (debugSession) { await vscode.debug.stopDebugging(debugSession); }
        if (silentServer) { silentServer.close(); }
        if (prototype && original) { prototype.sendRequest = original; }
        subscription.dispose();
        tracker.dispose();
        result.polls = observed.map(entry => ({ owner: entry.owner, session: entry.value?.debugSession, error: entry.error }));
        fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2));
    }
};
