'use strict';
const vscode = require('vscode');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

exports.run = async function () {
    const result = { tasks: [], events: [], errors: [] };
    const save = () => fs.writeFileSync(path.join(process.env.SUNFLECK_TEST_OUTPUT, 'native-launch.json'), JSON.stringify(result, null, 2));
    const ended = new Set();
    const subscriptions = [
        vscode.tasks.onDidEndTaskProcess(e => { result.tasks.push({ name: e.execution.task.name, exitCode: e.exitCode }); save(); }),
        vscode.debug.onDidTerminateDebugSession(s => ended.add(s.id)),
        vscode.debug.registerDebugAdapterTrackerFactory('*', { createDebugAdapterTracker: () => ({
            onDidSendMessage: m => { if (m.type === 'event' || m.success === false) { result.events.push(m); save(); } }
        }) })
    ];
    let session, prototype, original, debugClient;
    const snapshots = [];
    async function until(check, label) {
        const deadline = Date.now() + 60000;
        while (Date.now() < deadline) {
            if (check()) return;
            assert.ok(!result.tasks.some(t => t.exitCode !== 0), 'Prelaunch task failed');
            await new Promise(r => setTimeout(r, 200));
        }
        throw new Error(label);
    }
    try {
        const folder = vscode.workspace.workspaceFolders[0];
        const config = vscode.workspace.getConfiguration('launch', folder.uri).get('configurations')[0];
        const pipe = vscode.workspace.getConfiguration('sunfleck', folder.uri).get('debugPipe');
        assert.ok(pipe, 'Demo must configure its inspector pipe');
        const extension = vscode.extensions.getExtension('sunfleck.sunfleck');
        assert.ok(extension, 'Sunfleck extension must be installed');
        // Observe the ordinary automatic connection, without manually activating or connecting.
        await until(() => extension.isActive, 'Sunfleck did not activate automatically');
        const modulePath = path.join(extension.extensionPath, 'node_modules/vscode-languageclient/lib/node/main.js');
        const loaded = Object.values(require.cache).find(entry => path.resolve(entry.filename).toLowerCase() === modulePath.toLowerCase());
        prototype = loaded.exports.LanguageClient.prototype;
        original = prototype.sendRequest;
        prototype.sendRequest = function (method, params, ...rest) {
            debugClient = this;
            const pending = original.call(this, method, params, ...rest);
            if ((typeof method === 'string' ? method : method.method) !== 'sunfleck/debugSnapshot' || params.pipe !== pipe) return pending;
            return pending.then(value => {
                if (value?.nodes?.length) snapshots.push(value);
                result.lastSnapshot = { processId: value?.processId, nodes: value?.nodes?.length }; save();
                return value;
            }, error => { result.lastSnapshotError = error.message; save(); throw error; });
        };
        result.configuration = config;
        save();
        const pending = vscode.debug.startDebugging(folder, config);
        await until(() => !!vscode.debug.activeDebugSession, 'No debug session');
        assert.ok(await pending);
        session = vscode.debug.activeDebugSession;
        await until(() => result.events.some(e => e.event === 'process'), 'No debuggee process');
        assert.ok(result.tasks.some(t => t.name === 'Sunfleck: Build' && t.exitCode === 0));
        await new Promise(r => setTimeout(r, 1500));
        assert.ok(!ended.has(session.id), 'Demo exited unexpectedly');
        const pid = result.events.find(e => e.event === 'process').body.systemProcessId;
        await until(() => snapshots.some(s => s.processId === pid), 'Object tree did not automatically connect to debuggee');
        const snapshot = snapshots.find(s => s.processId === pid);
        fs.writeFileSync(path.join(process.env.SUNFLECK_TEST_OUTPUT, 'snapshot.json'), JSON.stringify(snapshot, null, 2));
        for (const id of ['primary', 'secondary', 'rating']) {
            assert.ok(snapshot.nodes.some(n => n.fleckId === id), 'Missing component in object tree: ' + id);
        }
        result.inspector = { pipe, processId: pid, nodeCount: snapshot.nodes.length, components: ['primary', 'secondary', 'rating'] };
        // Exercise the registered tree's object selection command, beyond raw Pipe availability.
        await vscode.commands.executeCommand('sunfleck.inspectObject', snapshot.nodes.find(n => n.fleckId === 'primary').id, false);
        result.navigation = [];
        // Navigate from a leaf back to its containers, then to the page's outer container.
        const leaf = snapshot.nodes.find(n => n.fleckId === 'name');
        const inner = snapshot.nodes.find(n => n.id === leaf.parent);
        const card = snapshot.nodes.find(n => n.id === inner.parent);
        const outer = snapshot.nodes.find(n => n.type === 'StackPanel' && n.parent === '0');
        for (const node of [leaf, inner, card, outer,
            ...['primary', 'secondary', 'rating', 'score'].map(id => snapshot.nodes.find(n => n.fleckId === id))]) {
            assert.ok(node, 'Expected demo container is missing');
            assert.ok(node.source?.uri, 'Missing source URI for ' + node.type + ' ' + node.id);
            await vscode.commands.executeCommand('sunfleck.inspectObject', node.id, true);
            const expected = /^file:/i.test(node.source.uri) ? vscode.Uri.parse(node.source.uri) : vscode.Uri.file(node.source.uri);
            const editor = vscode.window.activeTextEditor;
            assert.equal(editor.document.uri.fsPath.toLowerCase(), expected.fsPath.toLowerCase());
            assert.equal(editor.selection.active.line + 1, node.source.range.line);
            const sourceLine = editor.document.lineAt(editor.selection.active.line).text;
            assert.ok(sourceLine.includes(node.fleckId ? `Id="${node.fleckId}"` : '<' + node.type), 'Navigation must land on selected tag');
            result.navigation.push({ type: node.type, file: expected.fsPath, line: node.source.range.line });
        }
        assert.ok(snapshot.debugProtocol.methods.includes('highlight'));
        result.highlights = [];
        for (const node of [leaf, inner, outer, snapshot.nodes.find(n => n.fleckId === 'rating')]) {
            const start = snapshots.length;
            const response = await debugClient.sendRequest('sunfleck/debugRequest', { pipe, method: 'highlight', object: node.id });
            assert.equal(response.result, 0);
            await until(() => snapshots.slice(start).some(s => s.highlight === node.id), 'Highlight was not observed');
            const clearAt = snapshots.length;
            await debugClient.sendRequest('sunfleck/debugRequest', { pipe, method: 'highlight', object: '0' });
            await until(() => snapshots.slice(clearAt).some(s => s.highlight === '0'), 'Highlight did not clear');
            result.highlights.push({ id: node.id, type: node.type });
        }
        const expireAt = snapshots.length;
        await debugClient.sendRequest('sunfleck/debugRequest', { pipe, method: 'highlight', object: leaf.id });
        await until(() => snapshots.slice(expireAt).some(s => s.highlight === leaf.id), 'Lease did not start');
        const leasedAt = snapshots.length;
        await until(() => snapshots.slice(leasedAt).some(s => s.highlight === '0'), 'Lease did not expire');
        result.highlightLeaseExpired = true;
        const hoverSeconds = Number(process.env.SUNFLECK_TEST_HOVER_SECONDS || 0);
        if (hoverSeconds > 0) {
            result.interactive = true; save();
            await vscode.commands.executeCommand('sunfleck.objects.focus');
            await new Promise(resolve => setTimeout(resolve, hoverSeconds * 1000));
            result.hoverObservations = snapshots.slice(leasedAt).map(s => s.highlight).filter((id, index, values) => index === 0 || id !== values[index - 1]);
        }
        result.passed = true;
    } catch (error) { result.errors.push(error.stack); throw error; }
    finally {
        if (session) await vscode.debug.stopDebugging(session);
        if (prototype && original) prototype.sendRequest = original;
        subscriptions.forEach(s => s.dispose());
        save();
    }
};
