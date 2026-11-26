'use strict';
const vscode = require('vscode');
const { randomUUID } = require('node:crypto');
const { tr } = require('./language');
const { configuredDebugPipe } = require('./configuration');
const { createInspectorTree } = require('./inspector-tree');

function registerInspector(context, debugRequest) {
    let closeInspector;
    let stopping = false;
    const closing = new Set();
    const beginClose = () => {
        const pending = closeInspector?.();
        if (pending) {
            closing.add(pending);
            void pending.then(() => closing.delete(pending));
        }
    };
    const closeSnapshot = async () => {
        beginClose();
        await Promise.all([...closing]);
    };
    context.subscriptions.push({ dispose: () => { stopping = true; void closeSnapshot(); } });
    context.subscriptions.push(vscode.commands.registerCommand('sunfleck.closeSnapshot', closeSnapshot));
    context.subscriptions.push(vscode.commands.registerCommand('sunfleck.inspectSnapshot', async (selected) => {
        if (stopping) { return; }
        const configured = configuredDebugPipe();
        const pipe = selected?.pipe || configured || await vscode.window.showInputBox({
            prompt: tr('输入应用的调试 Pipe 名称', 'Enter the application debug pipe name'),
            validateInput: value => !value.trim() || value.length > 256 || /[\\/\s]/.test(value)
                ? tr('请输入有效的本机 Pipe 名称', 'Enter a valid local pipe name') : undefined
        });
        if (stopping || !pipe) { return; }
        beginClose();
        const channel = vscode.window.createOutputChannel(tr('Sunfleck 检查器', 'Sunfleck Inspector'));
        const changed = new vscode.EventEmitter();
        let snapshot;
        let status = '';
        let lastError = '';
        let disposed = false;
        let reading = false;
        let resolveClosed;
        const closed = new Promise(resolve => { resolveClosed = resolve; });
        let disconnecting;
        const owner = randomUUID();
        const disconnect = () => disconnecting ??= Promise.resolve()
            .then(() => debugRequest('sunfleck/debugDisconnect', { pipe, owner }))
            .catch(() => {}).then(() => resolveClosed());
        let children = new Map();
        let parents = new Map();
        let previousSelection = '0';
        let selectedDiagnostic;
        let commandStatus;
        let highlightTarget = '0', highlightPending = false;
        const highlight = async id => {
            highlightTarget = id && snapshot?.nodes.some(n => n.id === id) ? id : '0';
            if (highlightPending || !snapshot?.debugProtocol?.methods?.includes('highlight')) return;
            highlightPending = true;
            let sent;
            try {
                do {
                    sent = highlightTarget;
                    await debugRequest('sunfleck/debugRequest', { pipe, method: 'highlight', object: sent });
                } while (sent !== highlightTarget);
            } catch { /* Polling reports connection errors; the runtime lease clears the overlay. */ }
            finally { highlightPending = false; }
        };
        const item = (label, details, collapsibleState = vscode.TreeItemCollapsibleState.None) =>
            ({ label, description: details, collapsibleState });
        const expandable = vscode.TreeItemCollapsibleState.Collapsed;
        const nodeItem = node => ({
            ...item((node.fleckId || node.type) + (node.page === '0' ? tr('（代码）', ' (code)') : ''), node.type,
                children.has(`${node.window}:${node.id}`) ? expandable : vscode.TreeItemCollapsibleState.None),
            id: `node:${node.id}`, node,
            command: { command: 'sunfleck.inspectObject', title: tr('查看对象', 'Inspect Object'), arguments: [node.id] }
        });
        const navigateSource = async source => {
            const localPath = typeof source?.uri === 'string' && /^(?:[a-z]:[\\/]|\/[^/])/i.test(source.uri);
            if (source?.range?.line > 0 && source.range.column > 0 &&
                (localPath || /^file:\/\//i.test(source.uri))) {
                const uri = localPath ? vscode.Uri.file(source.uri) : vscode.Uri.parse(source.uri);
                const at = new vscode.Position(source.range.line - 1, source.range.column - 1);
                await vscode.window.showTextDocument(uri, { selection: new vscode.Range(at, at) });
            }
        };
        const selection = async (id, navigate = false) => {
            const node = snapshot?.nodes.find(node => node.id === id);
            if (!node) {
                return;
            }
            selectedDiagnostic = undefined;
            await view.reveal(nodeItem(node));
            channel.clear();
            channel.appendLine(JSON.stringify(node, null, 2));
            const models = snapshot.inspector?.models;
            if (models) {
                channel.appendLine(JSON.stringify({ models: [...new Set((node.bindings || []).map(binding => binding.model))]
                    .map(id => models[id]).filter(Boolean) }, null, 2));
            }
            if (navigate) {
                await navigateSource(node.source);
            }
        };
        const diagnosticKey = diagnostic => JSON.stringify([diagnostic.object, diagnostic.property, diagnostic.path]);
        const showDiagnostic = async (identity, navigate = true) => {
            const diagnostic = snapshot?.bindingDiagnostics?.diagnostics?.find(entry => diagnosticKey(entry) === diagnosticKey(identity));
            if (!diagnostic) {
                return;
            }
            selectedDiagnostic = diagnostic;
            channel.clear();
            channel.appendLine(JSON.stringify({ ...diagnostic,
                runtimeObject: snapshot.nodes.find(node => node.id === diagnostic.object) }, null, 2));
            if (navigate) {
                channel.show(true);
                await navigateSource(diagnostic.propertySource || diagnostic.source);
            }
            return diagnostic;
        };
        const inspect = vscode.commands.registerCommand('sunfleck.inspectObject', selection);
        const inspectDiagnostic = vscode.commands.registerCommand('sunfleck.inspectBindingDiagnostic', showDiagnostic);
        const pick = vscode.commands.registerCommand('sunfleck.pickObject', async () => {
            try {
                if (snapshot?.debugProtocol?.methods?.includes('pick')) {
                    const ticket = await debugRequest('sunfleck/debugRequest', { pipe, method: 'pick' });
                    if (!ticket) {
                        throw new Error(tr('语言服务未连接', 'Language server is not connected'));
                    }
                    commandStatus = ticket.result === 0 ? tr('调试请求成功', 'Debug request succeeded') :
                        tr('调试请求失败', 'Debug request failed');
                    changed.fire();
                    return;
                }

                throw new Error(tr('应用尚未连接，请先启动应用。', 'Application is not connected. Start the application first.'));
            } catch (error) {
                void vscode.window.showErrorMessage(tr('无法请求 Sunfleck 拾取', 'Could not request Sunfleck picking') + ': ' + error.message);
            }
        });
        const provider = {
            onDidChangeTreeData: changed.event,
            getTreeItem: element => element,
            getParent: element => {
                const parent = parents.get(element.node?.id);
                if (parent) {
                    return nodeItem(parent);
                }
                const window = snapshot?.windows.find(window => window.id === element.node?.window);
                return window ? windowItem(window) : undefined;
            },
            getChildren(element) {
                if (!element) {
                    return [
                        item(status, snapshot ? new Date(snapshot.timestamp).toLocaleTimeString() : undefined),
                        ...(commandStatus ? [item(commandStatus)] : []),
                        ...(snapshot?.fleckRefresh ? [item(
                            snapshot.fleckRefresh.pending ? tr('Fleck 刷新等待中', 'Fleck refresh pending') :
                                snapshot.fleckRefresh.result === 0 ? tr('Fleck 刷新成功', 'Fleck refresh succeeded') :
                                tr('Fleck 刷新失败', 'Fleck refresh failed'),
                            `${(snapshot.fleckRefresh.elapsedMicroseconds / 1000).toFixed(2)} ms`)] : []),
                        { ...item(tr('拾取运行时对象', 'Pick Runtime Object')), command: { command: 'sunfleck.pickObject', title: tr('拾取运行时对象', 'Pick Runtime Object') } },
                        ...(snapshot?.windows || []).map(windowItem),
                        { ...item(tr('绑定诊断', 'Binding Diagnostics'), undefined, expandable), id: 'diagnostics', diagnostics: true }
                    ];
                }
                if (element.diagnostics) {
                    return (snapshot?.bindingDiagnostics?.diagnostics || []).map(diagnostic =>
                        ({ ...item(`${diagnostic.active ? tr('失败', 'Active') : tr('已恢复', 'Recovered')} · ${diagnostic.path}`,
                            `${diagnostic.code} · ×${diagnostic.occurrences}`),
                        id: `diagnostic:${diagnosticKey(diagnostic)}`, tooltip: diagnostic.message,
                        command: { command: 'sunfleck.inspectBindingDiagnostic', title: tr('查看绑定诊断', 'Inspect Binding Diagnostic'), arguments: [diagnostic] } }));
                }
                if (element.window || element.node) {
                    const windowId = element.window?.id ?? element.node.window;
                    return (children.get(`${windowId}:${element.node?.id || '0'}`) || []).map(nodeItem);
                }
                return [];
            }
        };
        const windowItem = window => ({
            ...item(window.title, `${window.width}×${window.height}`, expandable),
            id: `window:${window.id}`, window
        });
        const view = createInspectorTree(context, provider, id => { if (!disposed || !id) void highlight(id); });
        const updateLanguage = () => {
            view.title = tr('Sunfleck 对象树', 'Sunfleck Object Tree');
            changed.fire();
        };
        updateLanguage();
        const languageChange = vscode.workspace.onDidChangeConfiguration(event => {
            if (event.affectsConfiguration('sunfleck.language')) {
                updateLanguage();
            }
        });
        const locate = vscode.commands.registerCommand('sunfleck.locateRuntimeObject', async (objectId) => {
            const editor = vscode.window.activeTextEditor;
            if (!editor || !snapshot) {
                return;
            }
            const position = editor.selection.active;
            const shared = await debugRequest('sunfleck/debugLocate', { pipe, uri: editor.document.uri.toString(),
                line: position.line + 1, column: position.character + 1 });
            const matches = shared?.nodes || snapshot.nodes.filter(node => {
                const source = node.source;
                const range = source?.range;
                if (!source?.uri || !range?.line || !range.endLine) {
                    return false;
                }
                const uri = /^file:\/\//i.test(source.uri) ? vscode.Uri.parse(source.uri) : vscode.Uri.file(source.uri);
                return uri.toString() === editor.document.uri.toString() &&
                    range.column > 0 && range.endColumn > 0 &&
                    new vscode.Range(range.line - 1, range.column - 1, range.endLine - 1, range.endColumn - 1).contains(position) &&
                    !position.isEqual(new vscode.Position(range.endLine - 1, range.endColumn - 1));
            });
            if (!matches.length) {
                void vscode.window.showInformationMessage(tr('当前源码位置在快照中没有对应对象。', 'No runtime object at the current source location.'));
                return;
            }
            const choices = matches.sort((a, b) => a.source.range.length - b.source.range.length).map(node => ({
                label: node.fleckId || node.type, description: `${tr('窗口', 'Window')} ${node.window} · ${tr('对象', 'Object')} ${node.id}`, node
            }));
            const picked = typeof objectId === 'string' ? choices.find(choice => choice.node.id === objectId) :
                choices.length === 1 ? choices[0] : await vscode.window.showQuickPick(choices, { matchOnDescription: true });
            if (picked && !disposed) {
                await view.reveal(nodeItem(picked.node), { select: true, focus: false });
                await selection(picked.node.id, false);
                return picked.node.id;
            }
        });
        async function refresh() {
            if (reading || disposed) {
                return;
            }
            reading = true;
            try {
                const response = await debugRequest('sunfleck/debugSnapshot', { pipe, owner });
                const next = response;
                if (!next) { throw new Error(tr('语言服务未连接', 'Language server is not connected')); }
                if (disposed) {
                    return;
                }
                if (next.version !== 1 || !Array.isArray(next.nodes) || !Array.isArray(next.windows) || !Number.isFinite(next.timestamp)) {
                    throw new Error(tr('不支持的快照格式', 'Unsupported snapshot format'));
                }
                lastError = '';
                const caption = Date.now() - next.timestamp > 1500 ? tr('快照未更新', 'Snapshot idle') : tr('已连接', 'Connected');
                if (snapshot?.timestamp !== next.timestamp || status !== caption) {
                    const ids = new Map(next.nodes.map(node => [`${node.window}:${node.id}`, node]));
                    const groups = new Map();
                    const owners = new Map();
                    for (const node of next.nodes) {
                        const parent = `${node.window}:${next.inspector?.parents?.[node.id] ?? node.parent}`;
                        const key = ids.has(parent) ? parent : `${node.window}:0`;
                        if (ids.has(parent)) {
                            owners.set(node.id, ids.get(parent));
                        }
                        if (!groups.has(key)) {
                            groups.set(key, []);
                        }
                        groups.get(key).push(node);
                    }
                    for (const siblings of groups.values()) {
                        siblings.sort((a, b) => (Number.isSafeInteger(a.siblingIndex) ? a.siblingIndex : 0) -
                            (Number.isSafeInteger(b.siblingIndex) ? b.siblingIndex : 0));
                    }
                    children = groups;
                    parents = owners;
                    snapshot = next;
                    status = caption;
                    changed.fire();
                    if (selectedDiagnostic) {
                        await showDiagnostic(selectedDiagnostic, false);
                    }
                    if (next.selection && next.selection !== '0' && next.selection !== previousSelection) {
                        await selection(next.selection, false);
                        const picked = next.nodes.find(node => node.id === next.selection);
                        if (picked && view.visible) {
                            await view.reveal(nodeItem(picked), { select: true, focus: false });
                        }
                    }
                    previousSelection = next.selection || '0';
                }
            } catch (error) {
                const unavailable = snapshot ?
                    tr('应用连接中断或暂停，等待恢复', 'Application disconnected or paused; waiting to reconnect') :
                    tr('等待应用连接，请启动调试', 'Waiting for application; start debugging');
                if (!disposed && lastError !== error.message) {
                    lastError = error.message;
                    channel.appendLine(`${pipe}: ${error.message}`);
                }
                if (!disposed && status !== unavailable) {
                    status = unavailable;
                    changed.fire();
                }
            } finally {
                reading = false;
                if (disposed) {
                    await disconnect();
                }
            }
        }
        const timer = setInterval(() => void refresh(), 500);
        closeInspector = () => {
            if (disposed) {
                return closed;
            }
            disposed = true;
            clearInterval(timer);
            if (!reading) {
                void disconnect();
            }
            view.dispose();
            changed.dispose();
            channel.dispose();
            inspect.dispose();
            inspectDiagnostic.dispose();
            pick.dispose();
            locate.dispose();
            languageChange.dispose();
            void vscode.commands.executeCommand('setContext', 'sunfleck.snapshotOpen', false);
            return closed;
        };
        await refresh();
        if (disposed) {
            return;
        }
        await vscode.commands.executeCommand('setContext', 'sunfleck.snapshotOpen', true);
        await vscode.commands.executeCommand('sunfleck.objects.focus');
    }));
    return async () => {
        stopping = true;
        await closeSnapshot();
    };
}

module.exports = { registerInspector };
