'use strict';
const assert = require('node:assert/strict');
const Module = require('node:module');
const { pathToFileURL } = require('node:url');
const path = require('node:path');
async function run() {
    let receive, changed, visibility;
    const resolvers = new Map(), registrations = new Set();
    const messages = [], hovers = [], commands = [];
    const uri = file => ({ fsPath: file, toString: () => pathToFileURL(file).href });
    const vscode = {
        workspace: { getConfiguration: () => ({ get: () => 'en' }) },
        Uri: { joinPath: (root, ...parts) => uri(path.join(root.fsPath, ...parts)) },
        window: { registerWebviewViewProvider: (id, provider) => {
            resolvers.set(id, provider.resolveWebviewView); registrations.add(id);
            return { dispose() { registrations.delete(id); } };
        } },
        commands: { executeCommand: async (...args) => commands.push(args) }
    };
    const load = Module._load;
    Module._load = function (name, ...args) { return name === 'vscode' ? vscode : load.call(this, name, ...args); };
    let createInspectorTree;
    try { ({ createInspectorTree } = require('../../editors/vscode/inspector-tree')); }
    finally { Module._load = load; }
    const parent = { id: 'node:1', label: '<StackPanel>', node: { id: '1' }, collapsibleState: 1 };
    const leaf = { id: 'node:2', label: 'Text', node: { id: '2', properties: [{ name: 'Width', value: 30, defaultValue: 'Auto' }], source: { uri: 'C:/test.fleck', range: { line: 2 } } }, command: { command: 'sunfleck.inspectObject', arguments: ['2'] } };
    const provider = {
        getChildren: element => !element ? [parent] : element === parent ? [leaf] : [],
        onDidChangeTreeData: callback => { changed = callback; return { dispose() {} }; }
    };
    const tree = createInspectorTree({ extensionUri: uri(path.resolve('editors/vscode')) }, provider, id => hovers.push(id));
    const view = { visible: true, show() {},
        onDidChangeVisibility: callback => { visibility = callback; }, onDidDispose() {},
        webview: { cspSource: 'test:', asWebviewUri: u => u, postMessage: async m => { messages.push(m); },
            onDidReceiveMessage: callback => { receive = callback; } }
    };
    resolvers.get('sunfleck.objects')(view);
    assert.ok(!view.webview.html.includes('property-content'), 'Object tree must not embed the property panel');
    await receive({ type: 'ready' });
    assert.ok(view.webview.html.includes("default-src 'none'"));
    assert.ok(!view.webview.html.includes('<StackPanel>'));
    assert.deepEqual(messages.at(-1).rows.map(r => [r.id, r.parent]), [['node:1', ''], ['node:2', 'node:1']]);
    await receive({ type: 'hover', id: 'node:1' });
    await receive({ type: 'hover', id: 'node:2' });
    await receive({ type: 'hover', id: 'unknown' });
    await receive({ type: 'leave' });
    assert.deepEqual(hovers, ['1', '2', undefined]);
    await receive({ type: 'activate', id: 'node:2', command: 'untrusted-command' });
    assert.deepEqual(commands, [], 'Row activation must not navigate');
    await receive({ type: 'select', id: 'node:2' });
    assert.equal(messages.at(-1).selected, 'node:2');
    assert.deepEqual(commands.pop(), ['sunfleck.properties.focus', { preserveFocus: true }]);
    assert.equal(messages.at(-1).rows[1].node.properties[0].value, 30);
    await receive({ type: 'locate', id: 'node:2', command: 'untrusted-command' });
    assert.deepEqual(commands, [['sunfleck.inspectObject', '2', true]]);
    leaf.node.properties[0].value = 42;
    await receive({ type: 'ready' });
    assert.equal(messages.at(-1).rows[1].node.properties[0].value, 42);
    const propertyMessages = [];
    let propertyDispose, propertyVisibility, propertyReceive, propertyShows = 0;
    const makePropertiesView = () => ({ visible: true, show(preserve) { assert.equal(preserve, true); propertyShows++; },
        onDidChangeVisibility: callback => { propertyVisibility = callback; },
        onDidDispose: callback => { propertyDispose = callback; },
        webview: { cspSource: 'test:', asWebviewUri: u => u, postMessage: async m => propertyMessages.push(m),
            onDidReceiveMessage: callback => { propertyReceive = callback; } }
    });
    let propertyView = makePropertiesView();
    resolvers.get('sunfleck.properties')(propertyView);
    await propertyReceive({ type: 'ready' });
    assert.ok(propertyView.webview.html.includes('inspector-properties.js'));
    assert.ok(!propertyView.webview.html.includes('id="tree"'));
    assert.equal(propertyMessages.at(-1).node.properties[0].value, 42, 'Late-opened panel must receive latest selection');
    propertyView.visible = false; propertyVisibility();
    leaf.node.properties[0].value = 55;
    await receive({ type: 'ready' });
    propertyDispose(); // Moving a view can dispose and recreate its webview.
    propertyView = makePropertiesView();
    resolvers.get('sunfleck.properties')(propertyView);
    await propertyReceive({ type: 'ready' });
    assert.equal(propertyMessages.at(-1).node.properties[0].value, 55, 'Moved panel must recover latest values');
    await propertyReceive({ type: 'locate', id: 'node:2' });
    assert.equal(commands.length, 1, 'Read-only property panel cannot execute arbitrary commands');
    await tree.reveal(leaf);
    assert.equal(propertyShows, 1);
    assert.equal(messages.at(-1).type, 'reveal');
    assert.equal(messages.at(-1).id, 'node:2');
    view.visible = false; visibility();
    assert.equal(hovers.at(-1), undefined);
    changed(); tree.dispose();
    assert.equal(registrations.size, 0);
    assert.equal(propertyView.webview.html, '');
    const manifest = require('../../editors/vscode/package.json');
    assert.ok(manifest.contributes.views['sunfleck.inspector'].some(v => v.id === 'sunfleck.properties'));
    assert.ok(manifest.contributes.viewsContainers.panel.some(v => v.id === 'sunfleck.inspector'));
    const count = hovers.length;
    await receive({ type: 'hover', id: 'node:1' });
    assert.equal(hovers.length, count);
    console.log('Independent inspector views: late opening, moving/recreating, live properties, selection, disposal; hierarchy, hover/leave, visibility, safe commands, reveal and disposal passed.');
}
run().catch(error => { console.error(error); process.exitCode = 1; });
