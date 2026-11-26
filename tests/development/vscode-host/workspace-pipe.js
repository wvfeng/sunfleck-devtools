'use strict';
const vscode = require('vscode');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');

exports.run = async function () {
    const root = path.resolve(__dirname, '../../..');
    const output = process.env.SUNFLECK_TEST_OUTPUT;
    const folder = vscode.workspace.workspaceFolders[0];
    const pipe = vscode.workspace.getConfiguration('sunfleck', folder.uri).get('debugPipe');
    const result = { passed: [], errors: [], observations: [] };
    const observed = [];
    let child, prototype, original;
    const until = async (read, label) => {
        const deadline = Date.now() + 30000;
        while (Date.now() < deadline) {
            const value = read(); if (value) { return value; }
            await new Promise(resolve => setTimeout(resolve, 100));
        }
        throw new Error(label);
    };
    try {
        assert.equal(vscode.workspace.workspaceFile.path.endsWith('.code-workspace'), true);
        assert.equal(vscode.workspace.getConfiguration('sunfleck').get('debugPipe'), '');
        assert.ok(pipe);
        await vscode.window.showTextDocument(vscode.Uri.joinPath(folder.uri, 'main.cpp'));
        const extension = vscode.extensions.getExtension('sunfleck.sunfleck');
        // Do not activate or connect explicitly: this is the user's startup path.
        await until(() => extension.isActive, 'Extension did not activate automatically');
        result.extension = { path: extension.extensionPath, version: extension.packageJSON.version };
        const modulePath = path.join(extension.extensionPath, 'node_modules/vscode-languageclient/lib/node/main.js');
        const loaded = Object.values(require.cache).find(entry => path.resolve(entry.filename).toLowerCase() === modulePath.toLowerCase());
        prototype = loaded.exports.LanguageClient.prototype;
        original = prototype.sendRequest;
        prototype.sendRequest = function (method, params, ...rest) {
            const pending = original.call(this, method, params, ...rest);
            if ((typeof method === 'string' ? method : method.method) !== 'sunfleck/debugSnapshot') { return pending; }
            return pending.then(value => { observed.push({ pipe: params.pipe, value }); return value; });
        };
        child = spawn(path.join(root, 'tests/development/fixtures/legacy-demos/counter-cpp/bin/Debug/CounterCpp.exe'), [path.join(root, 'tests/development/fixtures/legacy-demos/counter-cpp/main.fleck')], {
            cwd: path.join(root, 'tests/development/fixtures/legacy-demos/counter-cpp'), env: { ...process.env, SUNFLECK_DEBUG_PIPE: pipe }, stdio: 'ignore', windowsHide: true
        });
        await new Promise((resolve, reject) => { child.once('spawn', resolve); child.once('error', reject); });
        const initial = await until(() => observed.find(entry => entry.pipe === pipe && entry.value?.nodes?.length),
            'Folder-configured Pipe did not automatically populate the inspector');
        assert.equal(initial.value.processId, child.pid);
        assert.ok(!vscode.workspace.textDocuments.some(doc => doc.languageId === 'fleck'));
        result.passed.push('code-workspace-folder-settings-without-opening-fleck', 'automatic-inspector-polls-real-cpp-application');
        const button = initial.value.nodes.find(node => node.fleckId === 'sync');
        assert.ok(button);
        await vscode.commands.executeCommand('sunfleck.inspectObject', button.id, true);
        assert.ok(vscode.window.activeTextEditor.document.uri.fsPath.toLowerCase().endsWith('main.fleck'));
        result.passed.push('registered-inspector-object-navigates-to-fleck');
        await vscode.commands.executeCommand('sunfleck.closeSnapshot');
        const before = observed.length;
        // The normal connect command must also respect folder configuration (no input box).
        await vscode.commands.executeCommand('sunfleck.inspectSnapshot');
        await until(() => observed.slice(before).find(entry => entry.pipe === pipe && entry.value?.processId === child.pid),
            'Reconnect ignored folder configuration');
        result.passed.push('reconnect-command-resolves-folder-pipe');
        result.observations.push({ pipe, pid: child.pid, session: initial.value.debugSession, nodes: initial.value.nodes.length });
    } catch (error) { result.errors.push(error.stack || String(error)); throw error; }
    finally {
        await vscode.commands.executeCommand('sunfleck.closeSnapshot');
        if (prototype && original) { prototype.sendRequest = original; }
        if (child && child.exitCode === null) { child.kill(); }
        fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2));
    }
};
