'use strict';
const vscode = require('vscode');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const net = require('node:net');
const {spawn} = require('node:child_process');
const {endpoint} = require('../pipe-fixture');

exports.run = async function () {
    const root = path.resolve(__dirname, '../../..'), output = process.env.SUNFLECK_TEST_OUTPUT;
    const pipe = vscode.workspace.getConfiguration('sunfleck').get('debugPipe');
    const result = {passed: [], errors: [], sessions: []}, observed = [];
    let child, prototype, original, client;
    const until = async (read, label) => {
        const end = Date.now() + 30000;
        while (Date.now() < end) { const value = await read(); if (value) { return value; } await new Promise(r => setTimeout(r, 80)); }
        throw new Error(label);
    };
    try {
        const extension = vscode.extensions.getExtension('sunfleck.sunfleck');
        await extension.activate();
        const modulePath = path.join(extension.extensionPath, 'node_modules/vscode-languageclient/lib/node/main.js');
        const loaded = Object.values(require.cache).find(entry => path.resolve(entry.filename).toLowerCase() === modulePath.toLowerCase());
        prototype = loaded.exports.LanguageClient.prototype; original = prototype.sendRequest;
        prototype.sendRequest = function (method, params, ...rest) {
            const pending = original.call(this, method, params, ...rest);
            if ((typeof method === 'string' ? method : method.method) !== 'sunfleck/debugSnapshot') { return pending; }
            client = this;
            return pending.then(value => { observed.push(value); return value; });
        };
        await vscode.commands.executeCommand('sunfleck.inspectSnapshot', {pipe});
        for (const language of ['c', 'cpp', 'c']) {
            const executable = path.join(root, `out/devtools/examples/simple/${language}/fleck/counter_${language}_fleck.exe`);
            child = spawn(executable, [], {cwd:path.dirname(executable), env:{...process.env, SUNFLECK_DEBUG_PIPE:pipe}, stdio:'ignore', windowsHide:true});
            await new Promise((resolve,reject) => { child.once('spawn',resolve); child.once('error',reject); });
            const current = await until(() => observed.find(s => s.processId === child.pid && s.nodes.length), 'Native app was not connected');
            const button = current.nodes.find(n => n.type === 'Button');
            assert.ok(button.source.uri && button.source.range.line > 0);
            await vscode.commands.executeCommand('sunfleck.inspectObject', button.id, true);
            assert.equal(vscode.window.activeTextEditor.document.uri.fsPath.toLowerCase(), path.normalize(button.source.uri).toLowerCase());
            for (const method of ['pick', 'cancelPick']) {
                assert.equal((await client.sendRequest('sunfleck/debugRequest',{pipe,owner:'native-acceptance',method})).result,0);
            }
            const broken = net.connect(endpoint(pipe)); broken.on('error',()=>{});
            await new Promise(resolve=>{ broken.once('connect',()=>{broken.write('{');broken.destroy();resolve();});broken.once('error',resolve); });
            const timestamp=current.timestamp;
            await until(()=>observed.find(s=>s.processId===child.pid && s.timestamp>timestamp), 'Listener did not recover');
            result.sessions.push({language,pid:child.pid,session:current.debugSession,source:button.source});
            result.passed.push(`${language}-connect-source-pick-broken-client-recovery`);
            const stopped=new Promise(resolve=>child.once('exit',resolve)); child.kill(); await stopped; child=null;
            await new Promise(resolve=>setTimeout(resolve,700));
        }
        assert.equal(new Set(result.sessions.map(s=>s.session)).size,3);
        result.passed.push('same-pipe-three-generations-reconnect');
    } catch(error) { result.errors.push(error.stack||String(error)); throw error; }
    finally {
        await vscode.commands.executeCommand('sunfleck.closeSnapshot');
        if(child && child.exitCode===null) {child.kill();}
        if(prototype) {prototype.sendRequest=original;}
        fs.writeFileSync(path.join(output,'result.json'),JSON.stringify(result,null,2));
    }
};
