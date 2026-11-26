'use strict';
// Exercise the VS Code adapter against the real shared service without a desktop.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const Module = require('node:module');
const { spawn } = require('node:child_process');
const { pathToFileURL, fileURLToPath } = require('node:url');

async function run() {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sunfleck-inspector-client-'));
    let frame, available = true, picked;
    const host = await require('./pipe-fixture').serve(() => {
        if (!available) { throw new Error('Application unavailable'); }
        return frame;
    }, request => { picked = request; return { result: 0, session: 'client-session' }; });
    const pipe = host.pipe;
    const source = path.join(directory, 'source.fleck');
    const commands = new Map();
    const views = [];
    let hover;
    const output = [];
    const errors = [];
    const diagnostics = new Map();
    const contexts = new Map();
    let diagnosticCount = 0;
    const context = { subscriptions: [] };
    class Uri {
        constructor(value) { this.value = value; this.scheme = 'file'; this.fsPath = fileURLToPath(value); }
        static file(value) { return new Uri(pathToFileURL(value).href); }
        static parse(value) { return new Uri(value); }
        toString() { return this.value; }
    }
    class Position {
        constructor(line, character) { this.line = line; this.character = character; }
        isEqual(other) { return this.line === other.line && this.character === other.character; }
    }
    const vscode = {
        Uri, Position,
        Range: class { constructor(start, end) { this.start = start; this.end = end; } },
        TreeItemCollapsibleState: { None: 0, Collapsed: 1 },
        EventEmitter: class { event() {} fire() {} dispose() {} },
        commands: {
            registerCommand(name, handler) { commands.set(name, handler); return { dispose() { commands.delete(name); } }; },
            async executeCommand(name, ...args) {
                if (name === 'setContext') { contexts.set(args[0], args[1]); }
                return commands.get(name)?.(...args);
            }
        },
        workspace: {
            getConfiguration() { return { get: (_, fallback) => fallback }; },
            onDidChangeConfiguration() { return { dispose() {} }; }
        },
        window: {
            activeTextEditor: { document: { uri: Uri.file(source) }, selection: { active: new Position(1, 2) } },
            createOutputChannel() { return { clear() { output.length = 0; }, appendLine(text) { output.push(text); }, show() {}, dispose() {} }; },
            createTreeView(_, options) {
                const view = { provider: options.treeDataProvider, visible: true, async reveal(item) { this.selected = item; }, dispose() {} };
                views.push(view); return view;
            },
            async showTextDocument(uri, options) { this.activeTextEditor = { document: { uri }, selection: { active: options.selection.start } }; },
            async showQuickPick(choices) { return choices[0]; },
            showErrorMessage(message) { errors.push(message); },
            showInformationMessage(message) { errors.push(message); }
        }
    };
    const originalLoad = Module._load;
    Module._load = function (name, ...args) {
        if (name === './inspector-tree') return { createInspectorTree: (_, provider, onHover) => {
            hover = onHover;
            return vscode.window.createTreeView('sunfleck.objects', { treeDataProvider: provider });
        } };
        return name === 'vscode' ? vscode : originalLoad.call(this, name, ...args);
    };
    let registerInspector;
    try { ({ registerInspector } = require('../../editors/vscode/inspector')); }
    finally { Module._load = originalLoad; }
    const server = spawn(process.env.DOTNET_HOST_PATH || 'dotnet', [process.argv[2], 'lsp'], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    let sequence = 0;
    let buffer = Buffer.alloc(0);
    let stderr = '';
    const pending = new Map();
    server.stderr.on('data', chunk => { stderr += chunk; });
    server.stdin.on('error', () => {}); // Closing the server can race an adapter disconnect.
    server.stdout.on('data', chunk => {
        buffer = Buffer.concat([buffer, chunk]);
        while (true) {
            const end = buffer.indexOf('\r\n\r\n');
            if (end < 0) { break; }
            const length = Number(/Content-Length: (\d+)/i.exec(buffer.subarray(0, end).toString())[1]);
            if (buffer.length < end + 4 + length) { break; }
            const message = JSON.parse(buffer.subarray(end + 4, end + 4 + length));
            buffer = buffer.subarray(end + 4 + length);
            if (message.method === 'textDocument/publishDiagnostics') {
                diagnostics.set(message.params.uri, message.params.diagnostics);
                diagnosticCount++;
            }
            const waiting = pending.get(message.id);
            if (waiting) {
                pending.delete(message.id);
                clearTimeout(waiting.timer);
                if (message.error) { waiting.reject(new Error(message.error.message)); }
                else { waiting.resolve(message.result); }
            }
        }
    });
    function request(method, params) {
        return new Promise((resolve, reject) => {
            const id = ++sequence;
            const body = Buffer.from(JSON.stringify({ jsonrpc: '2.0', id, method, params }));
            const timer = setTimeout(() => { pending.delete(id); reject(new Error('Shared service timeout: ' + stderr)); }, 10000);
            pending.set(id, { resolve, reject, timer });
            server.stdin.write(Buffer.concat([Buffer.from(`Content-Length: ${body.length}\r\n\r\n`), body]));
        });
    }
    async function until(read, accept) {
        const deadline = Date.now() + 5000;
        while (Date.now() < deadline) {
            const value = await read();
            if (accept(value)) { return value; }
            await new Promise(resolve => setTimeout(resolve, 25));
        }
        throw new Error('Inspector adapter did not reach the expected state.');
    }
    try {
        await assert.rejects(request('sunfleck/debugSnapshot', { path: source }), /pipe/i);
        await assert.rejects(request('sunfleck/debugDisconnect', { path: source }), /pipe/i);
        const uri = Uri.file(source).toString();
        const otherUri = Uri.file(path.join(directory, 'other-project', 'source.fleck')).toString();
        const text = "<Window xmlns='urn:sunfleck:v1' Model='Live'><TextBlock Text='{Binding Name}'/></Window>";
        fs.writeFileSync(source, text);
        const location = { uri, model: 'Live', modelId: '7', range: { line: 2, column: 3, endLine: 2, endColumn: 15, length: 12 } };
        const metadata = path.join(directory, 'models.json');
        fs.writeFileSync(metadata, JSON.stringify({ version: 1, models: [
            { name: 'Live', properties: [{ path: 'Static', type: 'string', writable: true }] }
        ] }));
        await request('initialize', { initializationOptions: { language: 'en', modelFiles: [metadata] } });
        for (const documentUri of [uri, otherUri]) {
            await request('textDocument/didOpen', { textDocument: { uri: documentUri, version: 1, text } });
        }
        const complete = (documentUri = uri) => request('textDocument/completion', {
            textDocument: { uri: documentUri }, position: { line: 0, character: text.indexOf('Name') }
        });
        const labels = async (documentUri = uri) => (await complete(documentUri)).map(item => item.label);
        assert.ok(diagnostics.get(uri).some(item => item.code === 'path_missing'));
        const snapshot = {
            version: 1, timestamp: Date.now(), debugSession: 'client-session',
            debugProtocol: { version: 1, methods: ['snapshot', 'select', 'pick', 'cancelPick', 'highlight'] },
            windows: [{ id: 0, title: 'Main', width: 320, height: 200 }, { id: 1, title: 'Floating' }],
            nodes: [
                { id: '9007199254740993', parent: '2', page: '3', window: 0, type: 'TextBlock', source: location, bindings: [{ model: '7' }] },
                { id: '2', parent: '9007199254740993', page: '3', window: 1, type: 'TextBlock', source: location }],
            models: [{ id: '7', properties: [{ path: 'Name', type: 'string', writable: true,
                source: { uri: Uri.file(path.join(directory, 'model.cs')).toString(), line: 7, column: 4 } }] }]
        };
        frame = snapshot; available = true;
        registerInspector(context, request);
        await commands.get('sunfleck.inspectSnapshot')({ pipe });
        assert.ok((await labels()).includes('Name'), 'Inspector polling did not automatically populate LSP models');
        assert.deepEqual(diagnostics.get(uri), [], 'Runtime metadata did not recover diagnostics');
        assert.ok(!(await labels(otherUri)).includes('Name'), 'Runtime model escaped its exact source file');
        await request('workspace/didChangeConfiguration', { settings: { sunfleck: { language: 'en', modelFiles: [metadata] } } });
        await request('workspace/didChangeWatchedFiles', { changes: [{ uri: Uri.file(metadata).toString(), type: 2 }] });
        assert.ok((await labels()).includes('Name'), 'Configuration or file reload erased runtime models');
        const count = diagnosticCount;
        await request('sunfleck/debugSnapshot', { pipe, owner: 'unchanged' });
        assert.equal(diagnosticCount, count, 'Identical polling flooded diagnostics');
        await request('sunfleck/debugDisconnect', { pipe, owner: 'unchanged' });
        const definition = await request('textDocument/definition', { textDocument: { uri },
            position: { line: 0, character: text.indexOf('Name') + 2 } });
        assert.equal(definition.range.start.line, 6);
        assert.equal(definition.range.start.character, 3);
        const view = views.at(-1);
        const roots = view.provider.getChildren();
        const main = roots.find(item => item.window?.id === 0);
        const children = view.provider.getChildren(main);
        assert.equal(children[0].node.id, '9007199254740993');
        assert.equal(view.provider.getChildren(children[0]).length, 0, 'Cross-window parent escaped the shared projection');
        // Allocation order after refresh can be score, primary, secondary, rating.
        // The native sibling index must win over array order and object IDs.
        const savedNodes = snapshot.nodes;
        snapshot.nodes = [...savedNodes, ...['score', 'primary', 'secondary', 'rating'].map((id, index) => ({
            id: String(100 + index), fleckId: id, parent: '9007199254740993', window: 0,
            page: '3', type: 'TextBlock', siblingIndex: [3, 0, 1, 2][index]
        }))];
        snapshot.timestamp++;
        await until(() => view.provider.getChildren(children[0]), items => items.length === 4);
        assert.deepEqual(view.provider.getChildren(children[0]).map(item => item.node.fleckId), ['primary', 'secondary', 'rating', 'score']);
        snapshot.nodes = savedNodes; snapshot.timestamp++;
        await until(() => view.provider.getChildren(children[0]), items => items.length === 0);
        await commands.get('sunfleck.inspectObject')('9007199254740993', false);
        assert.ok(output.some(text => text.includes('Name')), 'Adapter did not show the associated model metadata');
        assert.equal(await commands.get('sunfleck.locateRuntimeObject')('2'), '2', 'Adapter did not use shared source matching');
        await commands.get('sunfleck.pickObject')();
        assert.equal(picked.request, 'pick');
        hover('9007199254740993');
        await until(() => picked, value => value.request === 'highlight' && value.object === '9007199254740993');
        hover(undefined);
        await until(() => picked, value => value.request === 'highlight' && value.object === '0');
        assert.deepEqual(errors, []);
        // A paused/unreachable app retains its last tree and snapshot time.
        const beforePause = view.provider.getChildren();
        available = false;
        await until(() => view.provider.getChildren(), items => /disconnected|中断/.test(items[0].label));
        const paused = view.provider.getChildren();
        assert.equal(paused[0].description, new Date(snapshot.timestamp).toLocaleTimeString());
        assert.deepEqual(paused.filter(item => item.window), beforePause.filter(item => item.window));
        assert.deepEqual(view.provider.getChildren(main), children);
        await commands.get('sunfleck.inspectObject')('9007199254740993', false);
        assert.ok(output.some(text => text.includes('Name')), 'Paused inspector lost object details');
        available = true;
        snapshot.timestamp = Date.now();
        await until(() => view.provider.getChildren(), items => /Connected|已连接/.test(items[0].label));
        // Existing panels update automatically, including property registration/removal.
        snapshot.models[0].properties[0].path = 'Changed';
        snapshot.timestamp++;
        frame = snapshot; available = true;
        await until(labels, items => items.includes('Changed') && !items.includes('Name'));
        assert.ok(diagnostics.get(uri).some(item => item.code === 'path_missing'));
        await request('sunfleck/setModels', { models: [{ name: 'Live', properties: [{ path: 'Manual', type: 'string', writable: false }] }] });
        assert.ok((await labels()).includes('Changed'), 'setModels erased the runtime overlay');
        for (const subscription of context.subscriptions.reverse()) { subscription.dispose(); }
        context.subscriptions.length = 0;
        await until(labels, items => items.includes('Manual') && !items.includes('Changed'));

        // Independent owners (e.g. VS split views) retain their own attachment.
        await request('sunfleck/debugSnapshot', { pipe, owner: 'view-a' });
        await request('sunfleck/debugSnapshot', { pipe, owner: 'view-b' });
        await request('sunfleck/debugDisconnect', { pipe, owner: 'view-a' });
        assert.ok((await labels()).includes('Changed'), 'Closing one view disconnected another view');
        snapshot.models[0].properties[0].path = 'NewProcess';
        snapshot.debugSession = 'replacement-session';
        frame = snapshot; available = true;
        await request('sunfleck/debugSnapshot', { pipe, owner: 'view-a' });
        assert.ok(diagnostics.get(uri).some(item => item.code === 'model_uncertain' && item.message.includes('Conflicting')),
            'Different runtime instances silently selected one model');
        await request('sunfleck/debugSnapshot', { pipe, owner: 'view-b' });
        assert.ok((await labels()).includes('NewProcess') && !(await labels()).includes('Changed'), 'Replacement session retained old model properties');
        await request('sunfleck/debugDisconnect', { pipe, owner: 'view-b' });
        snapshot.models[0].properties.push({ path: 'NewProcess', type: 'string', writable: false });
        frame = snapshot; available = true;
        await assert.rejects(request('sunfleck/debugSnapshot', { pipe, owner: 'view-a' }), /duplicate/i);
        assert.ok((await labels()).includes('Manual'), 'Malformed metadata left stale runtime models');
        snapshot.models[0].properties.pop();
        frame = snapshot; available = true;
        await request('sunfleck/debugSnapshot', { pipe });
        available = false;
        await assert.rejects(request('sunfleck/debugSnapshot', { pipe }));
        assert.ok((await labels()).includes('Manual'), 'Missing snapshot retained runtime models');
        await request('sunfleck/setModels', { models: [] });
        assert.ok((await labels()).includes('Static'), 'Disconnect/removal destroyed configured model files');
        const conflicting = structuredClone(snapshot.nodes[0]);
        conflicting.id = '4';
        conflicting.source.modelId = '8';
        snapshot.nodes.push(conflicting);
        snapshot.models.push({ id: '8', properties: [{ path: 'OtherInstance', type: 'string', writable: true }] });
        frame = snapshot; available = true;
        await request('sunfleck/debugSnapshot', { pipe });
        assert.ok(diagnostics.get(uri).some(item => item.code === 'model_uncertain'), 'Instances within one snapshot silently overwrote each other');
        snapshot.nodes.pop();
        snapshot.models.pop();
        frame = snapshot; available = true;
        await request('sunfleck/debugSnapshot', { pipe });
        assert.ok((await labels()).includes('NewProcess'), 'Removing a conflicting instance did not restore the surviving model');
        const registered = snapshot.models;
        snapshot.models = [];
        frame = snapshot; available = true;
        await request('sunfleck/debugSnapshot', { pipe });
        assert.ok((await labels()).includes('Static'), 'Destroyed runtime model retained stale metadata');
        snapshot.models = registered;
        snapshot.models.push(structuredClone(snapshot.models[0]));
        frame = snapshot; available = true;
        await assert.rejects(request('sunfleck/debugSnapshot', { pipe }), /identity/i);
        snapshot.models.pop();
        delete location.model;
        delete location.modelId;
        frame = snapshot; available = true;
        await request('sunfleck/debugSnapshot', { pipe });
        assert.ok((await labels()).includes('Static'), 'Legacy snapshot invented model associations');

        // Dispose while an RPC has reached the server but has not returned to the adapter.
        location.model = 'Live';
        location.modelId = '7';
        frame = snapshot; available = true;
        let releaseRead;
        let signalRead;
        const readStarted = new Promise(resolve => { signalRead = resolve; });
        registerInspector(context, async (method, params) => {
            const result = await request(method, params);
            if (method === 'sunfleck/debugSnapshot') {
                await new Promise(resolve => { releaseRead = resolve; signalRead(); });
            }
            return result;
        });
        const opening = commands.get('sunfleck.inspectSnapshot')({ pipe });
        await readStarted;
        for (const subscription of context.subscriptions.reverse()) { subscription.dispose(); }
        context.subscriptions.length = 0;
        releaseRead();
        await opening;
        await until(labels, items => items.includes('Static') && !items.includes('NewProcess'));
        assert.equal(contexts.get('sunfleck.snapshotOpen'), false, 'A completed RPC reopened a disposed inspector');
        console.log('Inspector adapter + real LSP: hierarchy, navigation, protocol, automatic model completion/definition/diagnostics, isolation, updates, conflicts, owner disposal, session replacement and fallback passed.');
    } finally {
        for (const subscription of context.subscriptions.reverse()) { subscription.dispose(); }
        for (const waiting of pending.values()) { clearTimeout(waiting.timer); }
        server.kill();
        await host.close();
        // Only remove the known temporary fixture files.
        for (const name of ['source.fleck', 'models.json']) {
            fs.rmSync(path.join(directory, name), { force: true });
        }
        fs.rmdirSync(directory);
    }
}
run().catch(error => { console.error(error); process.exitCode = 1; });
