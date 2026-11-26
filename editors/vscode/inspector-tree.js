'use strict';
const vscode = require('vscode');
const { randomUUID } = require('node:crypto');
const { tr } = require('./language');

// TreeView has no row mouse-enter/leave events. This view keeps the inspector's
// data provider and commands, while its DOM owns pointer and keyboard interaction.
function createInspectorTree(context, provider, hover) {
    let view, propertiesView, disposed = false, revision = 0, title = 'Sunfleck', selected;
    let items = new Map();
    async function update() {
        const current = ++revision;
        const rows = [], next = new Map();
        async function visit(parent, parentId, depth) {
            const children = await provider.getChildren(parent);
            for (let index = 0; index < children.length; index++) {
                const item = children[index];
                const id = item.id || `${parentId || 'root'}:${index}`;
                next.set(id, item);
                rows.push({ id, parent: parentId, depth, label: String(item.label || ''),
                    description: String(item.description || ''), expandable: !!item.collapsibleState,
                    object: item.node?.id, actionable: !!item.command, node: item.node,
                    locatable: !!item.node?.source?.uri && item.node.source.range?.line > 0 });
                if (item.collapsibleState) await visit(item, id, depth + 1);
            }
        }
        await visit(undefined, '', 0);
        if (disposed || current !== revision) return;
        if (selected && !next.has(selected)) {
            const previous = items.get(selected)?.node;
            selected = previous?.fleckId ? rows.find(row => row.node?.fleckId === previous.fleckId &&
                row.node.scopePath === previous.scopePath && row.node.source?.uri === previous.source?.uri)?.id : undefined;
        }
        items = next;
        if (view) await view.webview.postMessage({ type: 'tree', rows, selected });
        await updateProperties();
    }
    async function updateProperties() {
        if (propertiesView) await propertiesView.webview.postMessage({ type: 'properties', node: items.get(selected)?.node });
    }
    async function showProperties() {
        if (propertiesView) propertiesView.show(true);
        else await vscode.commands.executeCommand('sunfleck.properties.focus', { preserveFocus: true });
    }
    function initializeWebview(target, scriptName, content) {
        const nonce = randomUUID().replaceAll('-', '');
        const root = vscode.Uri.joinPath(context.extensionUri, 'media');
        target.webview.options = { enableScripts: true, localResourceRoots: [root] };
        const script = target.webview.asWebviewUri(vscode.Uri.joinPath(root, scriptName));
        const style = target.webview.asWebviewUri(vscode.Uri.joinPath(root, 'inspector-tree.css'));
        target.webview.html = `<!doctype html><html lang="${tr('zh-CN', 'en')}"><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${target.webview.cspSource}; script-src 'nonce-${nonce}';">
<link rel="stylesheet" href="${style}"></head><body>${content}
<script nonce="${nonce}" src="${script}"></script></body></html>`;
    }
    const propertiesRegistration = vscode.window.registerWebviewViewProvider('sunfleck.properties', {
        resolveWebviewView(resolved) {
            propertiesView = resolved;
            resolved.title = tr('Sunfleck 元素属性', 'Sunfleck Properties');
            initializeWebview(resolved, 'inspector-properties.js', '<section id="properties"><div id="property-content"></div></section>');
            resolved.webview.onDidReceiveMessage(message => {
                if (!disposed && message.type === 'ready') void updateProperties();
            });
            resolved.onDidChangeVisibility(() => { if (resolved.visible) void updateProperties(); });
            resolved.onDidDispose(() => { if (propertiesView === resolved) propertiesView = undefined; });
            void updateProperties();
        }
    });
    const registration = vscode.window.registerWebviewViewProvider('sunfleck.objects', {
        resolveWebviewView(resolved) {
            view = resolved;
            view.title = title;
            initializeWebview(view, 'inspector-tree.js', '<div id="tree" role="tree" aria-label="Sunfleck Object Tree"></div>');
            view.webview.onDidReceiveMessage(async message => {
                if (disposed) return;
                if (message.type === 'ready') { await update(); return; }
                if (message.type === 'leave') { hover(undefined); return; }
                const item = items.get(message.id);
                if (!item) return;
                if (message.type === 'hover') { hover(item.node?.id); }
                if (message.type === 'select' && item.node) { selected = message.id; await showProperties(); await update(); return; }
                if (message.type === 'locate' && item.node && item.command) {
                    await vscode.commands.executeCommand(item.command.command, ...(item.command.arguments || []), true);
                    return;
                }
                if (message.type === 'activate' && item.command && !item.node) {
                    await vscode.commands.executeCommand(item.command.command, ...(item.command.arguments || []));
                }
            });
            view.onDidChangeVisibility(() => { if (!view.visible) hover(undefined); else void update(); });
            view.onDidDispose(() => { view = undefined; hover(undefined); });
            void update();
        }
    });
    const changes = provider.onDidChangeTreeData(() => void update());
    return {
        get visible() { return !!view?.visible; },
        set title(value) { title = value; if (view) view.title = value; if (propertiesView) propertiesView.title = tr('Sunfleck 元素属性', 'Sunfleck Properties'); },
        async reveal(item) {
            selected = item.id;
            view?.show(true);
            await showProperties();
            await update();
            if (view) await view.webview.postMessage({ type: 'reveal', id: selected });
        },
        dispose() {
            disposed = true; ++revision; hover(undefined);
            if (view) view.webview.html = '';
            if (propertiesView) propertiesView.webview.html = '';
            changes.dispose(); registration.dispose(); propertiesRegistration.dispose();
        }
    };
}
module.exports = { createInspectorTree };
