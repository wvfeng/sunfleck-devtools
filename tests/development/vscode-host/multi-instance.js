'use strict';
const vscode = require('vscode');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');

exports.run = async function () {
    const root = path.resolve(__dirname, '../../..');
    const output = process.env.SUNFLECK_TEST_OUTPUT;
    const base = vscode.workspace.getConfiguration('sunfleck').get('debugPipe');
    const apps = [], observed = [], result = { passed: [], errors: [], selections: [] };
    let prototype, original;
    const until = async (read, accept, label) => {
        const deadline = Date.now() + 30000;
        while (Date.now() < deadline) {
            const value = await read();
            if (accept(value)) { return value; }
            await new Promise(resolve => setTimeout(resolve, 100));
        }
        throw new Error(label);
    };
    try {
        const extension = vscode.extensions.getExtension('sunfleck.sunfleck');
        await extension.activate();
        const modulePath = path.join(extension.extensionPath, 'node_modules/vscode-languageclient/lib/node/main.js');
        const loaded = Object.values(require.cache).find(entry => path.resolve(entry.filename).toLowerCase() === modulePath.toLowerCase());
        prototype = loaded.exports.LanguageClient.prototype;
        original = prototype.sendRequest;
        prototype.sendRequest = function (method, params, ...rest) {
            const pending = original.call(this, method, params, ...rest);
            if ((typeof method === 'string' ? method : method.method) !== 'sunfleck/debugSnapshot') { return pending; }
            return pending.then(value => { observed.push({ pipe: params.pipe, owner: params.owner, value }); return value; });
        };
        for (const suffix of ['a', 'b']) {
            const pipe = base + '-' + suffix;
            const child = spawn(path.join(root, 'tests/development/fixtures/legacy-demos/counter/bin/Debug/net10.0/Counter.exe'), [], {
                cwd: path.join(root, 'tests/development/fixtures/legacy-demos/counter'), env: { ...process.env, SUNFLECK_DEBUG_PIPE: pipe }, stdio: 'ignore', windowsHide: true
            });
            await new Promise((resolve, reject) => { child.once('spawn', resolve); child.once('error', reject); });
            apps.push({ child, pipe });
        }
        for (const index of [0, 1, 0]) {
            const before = observed.length;
            await vscode.commands.executeCommand('sunfleck.inspectSnapshot', { pipe: apps[index].pipe });
            const selected = await until(() => observed.slice(before).find(entry => entry.pipe === apps[index].pipe && entry.value.nodes.length), Boolean, 'Selected application did not populate the inspector');
            assert.equal(selected.value.processId, apps[index].child.pid);
            const button = selected.value.nodes.find(node => node.type === 'Button');
            assert.ok(button);
            await vscode.commands.executeCommand('sunfleck.inspectObject', button.id, true);
            assert.ok(vscode.window.activeTextEditor.document.uri.fsPath.toLowerCase().endsWith('main.fleck'));
            result.selections.push({ pid: selected.value.processId, pipe: selected.pipe, owner: selected.owner, session: selected.value.debugSession });
            await vscode.commands.executeCommand('sunfleck.closeSnapshot');
            const closedCount = observed.length;
            await new Promise(resolve => setTimeout(resolve, 1100));
            assert.equal(observed.length, closedCount, 'Retired instance continued polling');
        }
        assert.notEqual(result.selections[0].session, result.selections[1].session);
        assert.equal(result.selections[0].session, result.selections[2].session);
        assert.notEqual(result.selections[0].owner, result.selections[2].owner);
        result.passed.push('two-live-apps-selected-a-b-a-with-correct-process-and-session', 'object-details-navigate-to-source', 'retired-inspector-stops-polling');
    } catch (error) { result.errors.push(error.stack || String(error)); throw error; }
    finally {
        await vscode.commands.executeCommand('sunfleck.closeSnapshot');
        for (const app of apps) { if (app.child.exitCode === null) { app.child.kill(); } }
        if (prototype && original) { prototype.sendRequest = original; }
        fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2));
    }
};
