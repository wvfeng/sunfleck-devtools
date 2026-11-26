'use strict';
const vscode = require('vscode');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');

exports.run = async function () {
    const root = path.resolve(__dirname, '../../..');
    const output = process.env.SUNFLECK_TEST_OUTPUT;
    const pipe = vscode.workspace.getConfiguration('sunfleck').get('debugPipe');
    const result = { editor: vscode.version, passed: [], errors: [], exits: [] };
    const observed = [], events = [], ended = new Set();
    let session, prototype, original, client, breakpoint;
    const until = async (read, accept, label, timeout = 30000) => {
        const deadline = Date.now() + timeout;
        while (Date.now() < deadline) {
            const value = await read();
            if (accept(value)) { return value; }
            await new Promise(resolve => setTimeout(resolve, 100));
        }
        throw new Error(label);
    };
    const subscription = vscode.debug.onDidTerminateDebugSession(value => ended.add(value.id));
    const tracker = vscode.debug.registerDebugAdapterTrackerFactory('coreclr', {
        createDebugAdapterTracker: tracked => ({ onDidSendMessage: message => {
            if (message.type === 'event' || message.success === false) {
                events.push({ session: tracked.id, at: Date.now(), ...message });
                fs.appendFileSync(path.join(output, 'debug-protocol.jsonl'), JSON.stringify(events.at(-1)) + '\n');
            }
        } })
    });
    const closeWindow = async pid => {
        const child = spawn('pwsh', ['-NoProfile', '-Command', `$target = Get-Process -Id ${pid}; if (!$target.CloseMainWindow()) { throw 'Close failed' }; if (!$target.WaitForExit(10000)) { throw 'Exit timed out' }`], { windowsHide: true });
        await new Promise((resolve, reject) => {
            child.once('error', reject);
            child.once('exit', code => code === 0 ? resolve() : reject(new Error('Application close failed: ' + code)));
        });
    };
    try {
        const extension = vscode.extensions.getExtension('sunfleck.sunfleck');
        await extension.activate();
        result.extension = { path: extension.extensionPath, version: extension.packageJSON.version };
        const modulePath = path.join(extension.extensionPath, 'node_modules/vscode-languageclient/lib/node/main.js');
        const loaded = Object.values(require.cache).find(entry => path.resolve(entry.filename).toLowerCase() === modulePath.toLowerCase());
        prototype = loaded.exports.LanguageClient.prototype;
        original = prototype.sendRequest;
        prototype.sendRequest = function (method, params, ...rest) {
            const pending = original.call(this, method, params, ...rest);
            if ((typeof method === 'string' ? method : method.method) !== 'sunfleck/debugSnapshot' || params.pipe !== pipe) { return pending; }
            client = this;
            return pending.then(value => { observed.push({ at: Date.now(), value }); return value; }, error => {
                observed.push({ at: Date.now(), error: error.message }); throw error;
            });
        };
        const launch = JSON.parse(fs.readFileSync(path.join(root, 'tests/development/fixtures/legacy-demos/counter/.vscode/launch.json'), 'utf8')).configurations[0];
        result.launch = { ...launch, env: { ...launch.env, SUNFLECK_DEBUG_PIPE: pipe } };
        // Same built demo and launch settings; the isolated workspace supplies absolute paths.
        const config = { ...launch, preLaunchTask: undefined,
            program: path.join(root, 'tests/development/fixtures/legacy-demos/counter/bin/Debug/net10.0/Counter.dll'), cwd: path.join(root, 'tests/development/fixtures/legacy-demos/counter'),
            env: { ...launch.env, SUNFLECK_DEBUG_PIPE: pipe }, justMyCode: false,
            console: process.env.SUNFLECK_TEST_CONSOLE || launch.console };
        result.effectiveLaunch = config;
        const start = observed.length;
        assert.ok(await vscode.debug.startDebugging(vscode.workspace.workspaceFolders[0], config));
        session = await until(() => vscode.debug.activeDebugSession, Boolean, 'Debug session missing');
        const initial = await until(() => observed.slice(start).find(entry => entry.value?.nodes?.length), Boolean, 'Inspector did not connect');
        result.processId = initial.value.processId;
        result.debugSession = initial.value.debugSession;
        const source = path.join(root, 'bindings/csharp/App.cs');
        const line = fs.readFileSync(source, 'utf8').split(/\r?\n/).findIndex(value => value.includes('var result = Native.Poll(Handle, timeoutMilliseconds);'));
        assert.ok(line > 0);
        breakpoint = new vscode.SourceBreakpoint(new vscode.Location(vscode.Uri.file(source), new vscode.Position(line, 0)));
        vscode.debug.addBreakpoints([breakpoint]);
        const stopped = await until(() => events.find(event => event.session === session.id && event.event === 'stopped' && event.body.reason === 'breakpoint'), Boolean, 'Real source breakpoint was not hit');
        const pausedAt = observed.length;
        const last = observed.filter(entry => entry.value).at(-1).value;
        await until(() => observed.slice(pausedAt).filter(entry => entry.error).length, count => count >= 3, 'Paused app did not time out');
        assert.ok(!observed.slice(pausedAt).some(entry => entry.value), 'App unexpectedly supplied a fresh snapshot while stopped');
        assert.ok(client.isRunning(), 'Paused app stopped the language service');
        fs.writeFileSync(path.join(output, 'last-snapshot-before-breakpoint.json'), JSON.stringify(last, null, 2));
        result.passed.push('real-coreclr-source-breakpoint-with-three-pipe-timeouts');
        vscode.debug.removeBreakpoints([breakpoint]); breakpoint = undefined;
        await session.customRequest('continue', { threadId: stopped.body.threadId });
        const resumed = await until(() => observed.slice(pausedAt).find(entry => entry.value?.timestamp > last.timestamp), Boolean, 'Snapshot did not recover after continue');
        assert.equal(resumed.value.debugSession, last.debugSession);
        result.passed.push('same-session-snapshot-refreshes-after-continue');

        // Observe natural shutdown BEFORE any stopDebugging call.
        await closeWindow(initial.value.processId);
        const exitedAt = Date.now();
        let automatic = true;
        try { await until(() => ended.has(session.id), Boolean, 'IDE session remains after process exit', 15000); }
        catch { automatic = false; }
        result.exits.push({ console: config.console, automatic, observedMs: Date.now() - exitedAt,
            events: events.filter(event => event.session === session.id && ['exited', 'terminated'].includes(event.event)) });
        if (automatic) { result.passed.push('window-close-automatically-terminates-coreclr-session'); }
        else { await vscode.debug.stopDebugging(session); await until(() => ended.has(session.id), Boolean, 'Explicit stop failed'); }
        session = undefined;
        assert.ok(automatic, 'Application exited, but coreclr required an explicit stop after 15 seconds; see debug-protocol.jsonl');
    } catch (error) {
        result.errors.push(error.stack || String(error));
        throw error;
    } finally {
        if (breakpoint) { vscode.debug.removeBreakpoints([breakpoint]); }
        if (session) { await vscode.debug.stopDebugging(session); }
        if (prototype && original) { prototype.sendRequest = original; }
        tracker.dispose(); subscription.dispose();
        result.polls = observed.map(entry => ({ at: entry.at, timestamp: entry.value?.timestamp, session: entry.value?.debugSession, error: entry.error }));
        fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2));
    }
};
