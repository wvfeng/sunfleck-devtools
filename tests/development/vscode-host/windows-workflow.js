'use strict';
const vscode = require('vscode');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');

async function run() {
    const root = process.env.SUNFLECK_WINDOWS_ROOT;
    const output = process.env.SUNFLECK_WINDOWS_CHECK;
    const result = { passed: [], errors: [], editor: vscode.version };
    let session, watcher;
    let debugFailure;
    const debugTrace = vscode.debug.registerDebugAdapterTrackerFactory('coreclr', {
        createDebugAdapterTracker: () => ({ onDidSendMessage: message => {
            if (message.type === 'event' || message.success === false) {
                fs.appendFileSync(path.join(output, 'debug-protocol.jsonl'), JSON.stringify(message) + '\n');
            }
            if (message.event === 'stopped' && message.body.reason === 'exception') { debugFailure = message.body.text; }
        } })
    });
    const wait = async (read, accept, message, timeout = 30000) => {
        const until = Date.now() + timeout;
        while (Date.now() < until) {
            if (debugFailure) { throw new Error(debugFailure); }
            const value = await read();
            if (accept(value)) { return value; }
            await new Promise(resolve => setTimeout(resolve, 100));
        }
        throw new Error(message);
    };
    const readFrame = async pipe => {
        try {
            const frame = await require('../pipe-fixture').snapshot(pipe);
            return { pid: frame.processId, frame };
        } catch { return undefined; }
    };
    const text = (value, id) => value?.frame.nodes.find(node => node.fleckId === id)?.text;
    const save = async (document, value) => {
        const edit = new vscode.WorkspaceEdit();
        edit.replace(document.uri, new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)), value);
        assert.ok(await vscode.workspace.applyEdit(edit));
        assert.ok(await document.save());
    };
    const closeWindow = async pid => {
        const child = spawn('pwsh', ['-NoProfile', '-Command', `$p = Get-Process -Id ${pid}; if (!$p.CloseMainWindow()) { throw 'Close failed' }; if (!$p.WaitForExit(10000)) { throw 'Exit timed out' }`], { windowsHide: true });
        await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', code => code === 0 ? resolve() : reject(new Error('Application close failed: ' + code))); });
    };
    try {
        const extension = vscode.extensions.getExtension('sunfleck.sunfleck');
        await extension.activate();
        const page = path.join(output, 'counter.fleck');
        const original = fs.readFileSync(page, 'utf8');
        const document = await vscode.workspace.openTextDocument(page);
        await vscode.window.showTextDocument(document);
        const partial = "<Window xmlns='urn:sunfleck:v1' Model='CounterModel'><TextBlock Text='{Binding Co";
        const edit = new vscode.WorkspaceEdit();
        edit.replace(document.uri, new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)), partial);
        assert.ok(await vscode.workspace.applyEdit(edit));
        await wait(() => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, document.positionAt(partial.length)),
            value => value?.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'Count'), 'Model completion missing');
        result.passed.push('fleck-model-completion');
        await save(document, original.replace(/>\s+</g, '><'));
        await vscode.commands.executeCommand('editor.action.formatDocument');
        assert.ok(document.getText().includes('\n  <StackPanel'));
        await document.save();
        result.passed.push('fleck-format-command-and-save');
        const debugRoot = 'sunfleck-workflow-debug-' + process.pid;
        const ended = new Set();
        const subscription = vscode.debug.onDidTerminateDebugSession(value => ended.add(value.id));
        try {
            assert.ok(await vscode.debug.startDebugging(undefined, {
                name: 'Sunfleck Windows workflow', type: 'coreclr', request: 'launch',
                program: path.join(root, 'out/windows-dev/app/CounterFleck.dll'), cwd: root,
                args: ['--watch-fleck', page], console: 'internalConsole',
                env: { SUNFLECK_DEBUG_PIPE: debugRoot }
            }), 'C# debug launch failed');
            session = await wait(() => vscode.debug.activeDebugSession, Boolean, 'C# debug session missing', 90000);
            const initial = await wait(() => readFrame(debugRoot), value => text(value, 'caption') === 'Current count', 'Debug application not running', 90000);
            await save(document, document.getText().replace('Id="caption" Text="{Binding Caption}"', 'Id="caption" Text="Saved Fleck preview"'));
            const updated = await wait(() => readFrame(debugRoot), value => text(value, 'caption') === 'Saved Fleck preview', 'Saved Fleck did not update debug window');
            assert.equal(updated.pid, initial.pid);
            assert.equal(updated.frame.debugSession, initial.frame.debugSession);
            await save(document, '<Window><');
            const rejected = await wait(() => readFrame(debugRoot), value => value?.frame.fleckRefresh?.result !== undefined && value.frame.fleckRefresh.result !== 0, 'Invalid Fleck did not report failure');
            assert.equal(text(rejected, 'caption'), 'Saved Fleck preview');
            await save(document, original);
            await wait(() => readFrame(debugRoot), value => text(value, 'caption') === 'Current count', 'Valid Fleck did not recover');
            result.passed.push('actual-coreclr-debug-launch', 'fleck-save-updates-same-debug-process', 'invalid-fleck-keeps-page-and-recovers');
            fs.writeFileSync(path.join(output, 'debug-frame.json'), JSON.stringify(updated, null, 2));
            await closeWindow(initial.pid);
            // The app has exited. Explicitly release the IDE session before Watch;
            // coreclr can retain it while waiting for its Hot Reload service.
            if (!ended.has(session.id)) { await vscode.debug.stopDebugging(session); }
            await wait(() => ended.has(session.id), Boolean, 'Debugger did not finish');
            session = undefined;
        } finally { subscription.dispose(); }

        const watchRoot = 'sunfleck-workflow-watch-' + process.pid;
        watcher = spawn('dotnet', ['watch', '--non-interactive', '--project', path.join(output, 'Workflow.csproj'), 'run', '--no-launch-profile', '--', '--watch-fleck', page],
            { cwd: output, windowsHide: true, env: { ...process.env, SUNFLECK_DEBUG_PIPE: watchRoot, DOTNET_CLI_UI_LANGUAGE: 'en-US' } });
        watcher.stdout.on('data', data => fs.appendFileSync(path.join(output, 'watch.log'), data));
        watcher.stderr.on('data', data => fs.appendFileSync(path.join(output, 'watch.log'), data));
        const initial = await wait(() => readFrame(watchRoot), value => text(value, 'caption') === 'Current count', 'Watch application not running', 90000);
        const code = await vscode.workspace.openTextDocument(path.join(output, 'Program.cs'));
        await save(code, code.getText().replace('public string Caption => "Current count";', 'public string Caption => "C# Hot Reload works";'));
        const hot = await wait(() => readFrame(watchRoot), value => text(value, 'caption') === 'C# Hot Reload works', 'C# Getter Hot Reload failed', 90000);
        assert.equal(hot.pid, initial.pid);
        assert.equal(hot.frame.debugSession, initial.frame.debugSession);
        await save(document, document.getText().replace('Id="caption" Text="{Binding Caption}"', 'Id="caption" Text="Fleck and C# together"'));
        const both = await wait(() => readFrame(watchRoot), value => text(value, 'caption') === 'Fleck and C# together', 'Fleck update under dotnet watch failed');
        assert.equal(both.pid, initial.pid);

        fs.writeFileSync(path.join(output, 'watch-frame.json'), JSON.stringify(both, null, 2));
        result.passed.push('csharp-getter-hot-reload-same-process', 'fleck-save-under-watch-does-not-restart');
        await closeWindow(initial.pid);
    } catch (error) {
        result.errors.push(error.stack || String(error));
        throw error;
    } finally {
        if (session) { await vscode.debug.stopDebugging(session); }
        if (watcher && watcher.exitCode === null) {
            // This exact child owns only the isolated validation application.
            await new Promise(resolve => spawn('taskkill', ['/PID', String(watcher.pid), '/T', '/F'], { windowsHide: true }).once('exit', resolve));
        }
        fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2));
        debugTrace.dispose();
    }
}
module.exports = { run };
