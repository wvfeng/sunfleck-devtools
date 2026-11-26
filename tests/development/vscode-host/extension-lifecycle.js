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
    const statePath = path.join(output, 'reload-state.json');
    const result = { passed: [], errors: [], editor: vscode.version };
    const prior = fs.existsSync(statePath) ? JSON.parse(fs.readFileSync(statePath, 'utf8')) : undefined;
    let appPid = prior?.appPid, prototype, original, client, reloading = false;
    const observed = [];
    const reload = async () => {
        reloading = true;
        await vscode.commands.executeCommand('workbench.action.reloadWindow').catch(error => {
            if (error.name !== 'Canceled' && error.name !== 'CancellationError') { throw error; }
        });
        await new Promise(() => {});
    };
    const setInstalledExtensionsEnabled = async enabled => {
        await vscode.commands.executeCommand('workbench.view.extensions');
        const command = 'workbench.extensions.action.' + (enabled ? 'enableAll' : 'disableAll');
        await until(() => vscode.commands.getCommands(true), commands => commands.includes(command), 'Extension management command unavailable');
        await vscode.commands.executeCommand(command);
    };
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
        if (prior?.phase === 'disabled') {
            assert.equal(vscode.extensions.getExtension('sunfleck.sunfleck'), undefined, 'Disabled extension remained in the extension host');
            const live = await require('../pipe-fixture').snapshot(pipe);
            assert.equal(live.debugSession, prior.debugSession, 'Disabling extension stopped the application');
            await until(() => {
                try { process.kill(prior.servicePid, 0); return false; }
                catch (error) { if (error.code === 'ESRCH') { return true; } throw error; }
            }, Boolean, 'Disabling extension left the language service running');
            prior.phase = 'enabled';
            fs.writeFileSync(statePath, JSON.stringify(prior, null, 2));
            await setInstalledExtensionsEnabled(true);
            await reload();
        }
        const extension = vscode.extensions.getExtension('sunfleck.sunfleck');
        await extension.activate();
        const modulePath = path.join(extension.extensionPath, 'node_modules/vscode-languageclient/lib/node/main.js');
        const loaded = Object.values(require.cache).find(entry => path.resolve(entry.filename).toLowerCase() === modulePath.toLowerCase());
        prototype = loaded.exports.LanguageClient.prototype;
        original = prototype.sendRequest;
        prototype.sendRequest = function (method, params, ...rest) {
            const pending = original.call(this, method, params, ...rest);
            if ((typeof method === 'string' ? method : method.method) !== 'sunfleck/debugSnapshot' || params.pipe !== pipe) { return pending; }
            client = this;
            return pending.then(value => { observed.push({ value, owner: params.owner }); return value; });
        };
        if (!prior) {
            const child = spawn(path.join(root, 'tests/development/fixtures/legacy-demos/counter/bin/Debug/net10.0/Counter.exe'), [], {
                cwd: path.join(root, 'tests/development/fixtures/legacy-demos/counter'), env: { ...process.env, SUNFLECK_DEBUG_PIPE: pipe },
                detached: true, stdio: 'ignore', windowsHide: true
            });
            await new Promise((resolve, reject) => { child.once('spawn', resolve); child.once('error', reject); });
            appPid = child.pid;
            child.unref();
        }
        const entry = await until(() => observed.find(entry => entry.value?.nodes?.length), Boolean, 'Inspector did not reconnect to the running application');
        assert.equal(entry.value.processId, appPid);
        if (!prior) {
            fs.writeFileSync(statePath, JSON.stringify({ appPid, debugSession: entry.value.debugSession,
                owner: entry.owner, servicePid: client._serverProcess.pid, extensionHostPid: process.pid,
                phase: process.env.SUNFLECK_DISABLE_CYCLE === '1' ? 'disabled' : 'reload' }, null, 2));
            if (process.env.SUNFLECK_DISABLE_CYCLE === '1') {
                await setInstalledExtensionsEnabled(false);
            }
            await reload();
        }
        assert.notEqual(process.pid, prior.extensionHostPid, 'Window reload did not replace the extension host');
        assert.notEqual(client._serverProcess.pid, prior.servicePid, 'Reload retained the retired language service');
        assert.notEqual(entry.owner, prior.owner, 'Reload retained the retired inspector owner');
        assert.equal(entry.value.debugSession, prior.debugSession, 'Reload changed the running application session');
        await until(() => {
            try { process.kill(prior.servicePid, 0); return false; }
            catch (error) { if (error.code === 'ESRCH') { return true; } throw error; }
        }, Boolean, 'Old language service survived extension-host reload');
        result.passed.push('real-window-reload-replaces-extension-host-and-language-service',
            'retired-language-service-exits', 'new-inspector-reconnects-to-same-running-app-session');
        if (prior.phase === 'enabled') { result.passed.push('installed-extension-disable-and-reenable-preserve-running-app'); }
        result.before = prior;
        result.after = { extensionHostPid: process.pid, servicePid: client._serverProcess.pid,
            owner: entry.owner, debugSession: entry.value.debugSession };
        await vscode.commands.executeCommand('sunfleck.closeSnapshot');
    } catch (error) {
        result.errors.push(error.stack || String(error));
        reloading = false;
        throw error;
    } finally {
        if (!reloading) {
            if (appPid) { try { process.kill(appPid); } catch (error) { if (error.code !== 'ESRCH') { result.errors.push(error.message); } } }
            if (prototype && original) { prototype.sendRequest = original; }
            fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2));
        }
    }
};
