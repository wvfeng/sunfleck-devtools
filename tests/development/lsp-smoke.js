'use strict';
// Exercise a real stdio server, including the one copied into an editor package.
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const { pathToFileURL } = require('node:url');

async function main() {
    const server = path.resolve(process.argv[2]);
    const model = path.resolve(process.argv[3]);
    const fixture = fs.mkdtempSync(path.join(os.tmpdir(), 'sunfleck-models-'));
    const child = spawn(process.env.DOTNET_HOST_PATH || 'dotnet', [server, 'lsp', '--stdio', `--clientProcessId=${process.pid}`], { stdio: 'pipe', windowsHide: true });
    let buffered = Buffer.alloc(0);
    let stderr = '';
    let sequence = 0;
    const waiting = new Map();
    child.stderr.on('data', data => { stderr = (stderr + data).slice(-32768); });
    function fail(error) {
        for (const { reject, timeout } of waiting.values()) {
            clearTimeout(timeout);
            reject(error);
        }
        waiting.clear();
    }
    child.on('error', fail);
    child.on('exit', code => {
        if (waiting.size > 0) {
            fail(new Error(`LSP exited with ${code}: ${stderr}`));
        }
    });
    child.stdout.on('data', data => {
        buffered = Buffer.concat([buffered, data]);
        try {
            while (true) {
                const headerEnd = buffered.indexOf('\r\n\r\n');
                if (headerEnd < 0) {
                    break;
                }
                const header = buffered.subarray(0, headerEnd).toString('ascii');
                const match = /^Content-Length: (\d+)$/m.exec(header);
                assert.ok(match, `Unexpected stdout: ${header}`);
                const length = Number(match[1]);
                if (buffered.length < headerEnd + 4 + length) {
                    break;
                }
                const message = JSON.parse(buffered.subarray(headerEnd + 4, headerEnd + 4 + length).toString('utf8'));
                buffered = buffered.subarray(headerEnd + 4 + length);
                const key = message.id === undefined ? message.method : message.id;
                const pending = waiting.get(key);
                if (pending) {
                    clearTimeout(pending.timeout);
                    waiting.delete(key);
                    if (message.error) {
                        pending.reject(new Error(JSON.stringify(message.error)));
                    } else {
                        pending.resolve(message.id === undefined ? message.params : message.result);
                    }
                }
            }
        } catch (error) {
            fail(error);
        }
    });
    function wait(key) {
        return new Promise((resolve, reject) => {
            const timeout = setTimeout(() => {
                waiting.delete(key);
                reject(new Error(`Timed out waiting for ${key}: ${stderr}`));
            }, 10000);
            waiting.set(key, { resolve, reject, timeout });
        });
    }
    function send(method, params, id) {
        const data = Buffer.from(JSON.stringify({ jsonrpc: '2.0', method, params, ...(id === undefined ? {} : { id }) }));
        child.stdin.write(`Content-Length: ${data.length}\r\n\r\n`);
        child.stdin.write(data);
    }
    function request(method, params) {
        const id = ++sequence;
        const result = wait(id);
        send(method, params, id);
        return result;
    }
    try {
        const initialized = await request('initialize', {
            capabilities: { textDocument: { completion: { completionItem: { snippetSupport: true } } } },
            initializationOptions: { modelFiles: [model], language: 'en' }
        });
        assert.equal(initialized.capabilities.positionEncoding, 'utf-16');
        assert.equal(initialized.capabilities.documentFormattingProvider, true);
        send('initialized', {});
        const uri = 'file:///sunfleck-lsp-中文.fleck';
        const text = "<Window xmlns='urn:sunfleck:v1' Model='CCounter'><StackPanel><TextBlock Text='🌱'/><TextBlock Text='{Binding Counter.Value}'/></StackPanel></Window>";
        const published = wait('textDocument/publishDiagnostics');
        send('textDocument/didOpen', { textDocument: { uri, version: 1, languageId: 'fleck', text } });
        assert.deepEqual((await published).diagnostics, []);
        const configured = JSON.parse(fs.readFileSync(model, 'utf8'));
        const alternate = path.join(fixture, 'alternate.fleckmodel.json');
        const malformed = path.join(fixture, 'malformed.fleckmodel.json');
        fs.writeFileSync(alternate, JSON.stringify(configured));
        fs.writeFileSync(malformed, '{invalid');
        async function configure(files) {
            const next = wait('textDocument/publishDiagnostics');
            send('workspace/didChangeConfiguration', { settings: { sunfleck: { modelFiles: files } } });
            return (await next).diagnostics;
        }
        assert.ok((await configure([model, alternate, malformed])).some(item =>
            item.code === 'model_uncertain' && item.message.includes('Multiple metadata files')));
        const dynamic = wait('textDocument/publishDiagnostics');
        await request('sunfleck/setModels', { models: configured.models });
        assert.deepEqual((await dynamic).diagnostics, []);
        const reset = wait('textDocument/publishDiagnostics');
        await request('sunfleck/setModels', { models: [] });
        assert.ok((await reset).diagnostics.some(item => item.code === 'model_uncertain'));
        configured.assembly = path.join(fixture, 'missing.dll');
        fs.writeFileSync(alternate, JSON.stringify(configured));
        assert.ok((await configure([alternate, malformed])).some(item =>
            item.code === 'model_uncertain' && item.message.includes('stale')));
        assert.deepEqual(await configure([model, malformed]), []);
        const projectA = pathToFileURL(path.join(fixture, 'project-a')).href;
        const projectB = pathToFileURL(path.join(fixture, 'project-b')).href;
        const secondModel = JSON.parse(fs.readFileSync(model, 'utf8'));
        secondModel.models[0].properties[0].path = 'Counter.Other';
        fs.writeFileSync(alternate, JSON.stringify(secondModel));
        send('workspace/didChangeConfiguration', { settings: { sunfleck: {
            modelScopes: [{ uri: projectA, modelFiles: [model] }, { uri: projectB, modelFiles: [alternate] }]
        } } });
        await request('textDocument/documentSymbol', { textDocument: { uri } });
        for (const [project, binding] of [[projectA, 'Counter.Value'], [projectB, 'Counter.Other']]) {
            const scopedUri = project + '/counter.fleck';
            const pending = wait('textDocument/publishDiagnostics');
            send('textDocument/didOpen', { textDocument: {
                uri: scopedUri, version: 1, languageId: 'fleck', text: text.replace('Counter.Value', binding)
            } });
            const report = await pending;
            assert.equal(report.uri, scopedUri);
            assert.deepEqual(report.diagnostics, []);
            const closed = wait('textDocument/publishDiagnostics');
            send('textDocument/didClose', { textDocument: { uri: scopedUri } });
            await closed;
        }
        assert.deepEqual(await configure([model]), []);
        const position = { line: 0, character: text.indexOf('Counter.Value') + 3 };
        const definition = await request('textDocument/definition', { textDocument: { uri }, position });
        assert.ok(definition.uri.endsWith('model.h'));
        const hover = await request('textDocument/hover', { textDocument: { uri }, position });
        assert.ok(hover.contents.value.includes('integer'));
        const tokens = await request('textDocument/semanticTokens/full', { textDocument: { uri } });
        assert.ok(tokens.data.length > 0);
        const format = options => request('textDocument/formatting', { textDocument: { uri }, options });
        const edits = await format({ tabSize: 2, insertSpaces: true });
        assert.equal(edits.length, 5);
        const formatted = edits.reduceRight((value, { range, newText }) => {
            assert.equal(range.start.line, 0);
            assert.equal(range.end.line, 0);
            return value.slice(0, range.start.character) + newText + value.slice(range.end.character);
        }, text);
        assert.equal(formatted, "<Window xmlns='urn:sunfleck:v1' Model='CCounter'>\n  <StackPanel>\n    <TextBlock Text='🌱'/>\n    <TextBlock Text='{Binding Counter.Value}'/>\n  </StackPanel>\n</Window>");
        const tabs = await format({ tabSize: 8, insertSpaces: false });
        assert.equal(tabs[1].newText, '\n\t\t');
        send('textDocument/didChange', { textDocument: { uri, version: 2 }, contentChanges: [{ text: formatted }] });
        assert.deepEqual(await format({ tabSize: 2, insertSpaces: true }), []);
        const partial = "<Window xmlns='urn:sunfleck:v1' Model='CCounter'><TextBlock Text='{Binding Counter.";
        const changed = wait('textDocument/publishDiagnostics');
        send('textDocument/didChange', { textDocument: { uri, version: 3 }, contentChanges: [{ text: partial }] });
        assert.ok((await changed).diagnostics.length > 0);
        const items = await request('textDocument/completion', { textDocument: { uri }, position: { line: 0, character: partial.length } });
        assert.ok(items.some(item => item.label === 'Counter.Value'));
        assert.deepEqual(await format({ tabSize: 2, insertSpaces: true }), []);
        const mixed = "<Window xmlns='urn:sunfleck:v1'><TextBlock>前 <Run/> 后</TextBlock></Window>";
        send('textDocument/didChange', { textDocument: { uri, version: 4 }, contentChanges: [{ text: mixed }] });
        assert.deepEqual(await format({ tabSize: 2, insertSpaces: true }), []);
        const crlf = "<Window xmlns='urn:sunfleck:v1'>\r\n<TextBlock Text='原样'/>\r\n</Window>";
        send('textDocument/didChange', { textDocument: { uri, version: 5 }, contentChanges: [{ text: crlf }] });
        const crlfEdits = await format({ tabSize: 2, insertSpaces: true });
        assert.deepEqual(crlfEdits, [{
            range: { start: { line: 0, character: crlf.indexOf('\r') }, end: { line: 1, character: 0 } },
            newText: '\r\n  '
        }]);
        await request('shutdown', null);
        const exited = once(child, 'exit');
        send('exit', null);
        const [code] = await exited;
        assert.equal(code, 0, stderr);
    } finally {
        for (const file of fs.readdirSync(fixture)) { fs.unlinkSync(path.join(fixture, file)); }
        fs.rmdirSync(fixture);
        child.stdin.end();
        if (child.exitCode === null) {
            child.kill();
        }
    }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
