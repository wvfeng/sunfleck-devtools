'use strict';
const vscode = require('vscode');
const { randomUUID } = require('node:crypto');
const path = require('node:path');
const { tr } = require('./language');
const { configuredDebugPipe } = require('./configuration');
const { createInspectorTree } = require('./inspector-tree');
const { browserDebugTypes, resolveBrowserConfiguration, snapshotEndpoint, requestSnapshot, browserCommand } = require('./browser-inspector');

async function resolveSourceUri(sourceUri, browser) {
    if (!sourceUri) return;
    if (/^file:\/\//i.test(sourceUri)) return vscode.Uri.parse(sourceUri);
    if (/^(?:[a-z]:[\\/]|\/)/i.test(sourceUri) || /^\\\\[^\\]/.test(sourceUri)) {
        return vscode.Uri.file(sourceUri);
    }

    const relative = sourceUri.replaceAll('\\', '/').split('/').filter(part => part && part !== '.');
    if (!relative.length || relative.some(part => part === '..' || part.includes(':'))) return;
    const webRoot = browser?.configuration.webRoot;
    const folder = browser?.workspaceFolder?.uri;
    const scopes = [];
    if (typeof webRoot === 'string' && path.isAbsolute(webRoot)) scopes.push([vscode.Uri.file(webRoot)]);
    if (folder) scopes.push([folder]);
    else scopes.push(vscode.workspace.workspaceFolders?.map(entry => entry.uri) ?? []);

    const isFile = async uri => {
        try {
            return ((await vscode.workspace.fs.stat(uri)).type & vscode.FileType.File) !== 0;
        } catch (error) {
            if (error.code === 'FileNotFound' || error.code === 'FileNotADirectory') return false;
            throw error;
        }
    };
    // Fleck resources can report a name relative to the resource loader.
    const pattern = '**/' + relative.join('/').replace(/[*?[\]{}]/g, char => '[' + char + ']');
    const searched = new Set();
    for (const scope of scopes) {
        const roots = scope.filter(root => {
            const key = root.toString();
            if (searched.has(key)) return false;
            searched.add(key);
            return true;
        });
        const direct = roots.map(root => vscode.Uri.joinPath(root, ...relative));
        const matches = (await Promise.all(direct.map(async uri => await isFile(uri) ? uri : undefined))).filter(Boolean);
        if (matches.length) return matches.length === 1 ? matches[0] : undefined;

        const found = new Map();
        for (const root of roots) {
            const files = await vscode.workspace.findFiles(new vscode.RelativePattern(root, pattern), undefined, 2);
            for (const uri of files) found.set(uri.toString(), uri);
            if (found.size > 1) return;
        }
        if (found.size) return found.values().next().value;
    }
}

function registerInspector(context, debugRequest) {
    let closeInspector;
    let stopping = false;
    const closing = new Set();
    const changed = new vscode.EventEmitter();
    let activeProvider;
    let hover;
    const view = createInspectorTree(context, {
        onDidChangeTreeData: changed.event,
        getChildren: element => activeProvider?.getChildren(element) ||
            (element ? [] : [{ label: tr('启动 Sunfleck 应用或文档调试以查看对象树', 'Start debugging a Sunfleck app or the docs to view the object tree') }])
    }, id => hover?.(id));
    const updateLanguage = () => {
        view.title = tr('Sunfleck 对象树', 'Sunfleck Object Tree');
        changed.fire();
    };
    updateLanguage();
    context.subscriptions.push(view, changed, vscode.workspace.onDidChangeConfiguration(event => {
        if (event.affectsConfiguration('sunfleck.language')) updateLanguage();
    }));
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
    let browserSession;
    const openSnapshot = async (selected, browser) => {
        if (stopping) { return; }
        const endpoint = browser && snapshotEndpoint(browser);
        if (endpoint && browserSession === browser.id && closeInspector) return;
        const pipe = endpoint ? undefined : selected?.pipe || configuredDebugPipe() || await vscode.window.showInputBox({
            prompt: tr('输入应用的调试 Pipe 名称', 'Enter the application debug pipe name'),
            validateInput: value => !value.trim() || value.length > 256 || /[\\/\s]/.test(value)
                ? tr('请输入有效的本机 Pipe 名称', 'Enter a valid local pipe name') : undefined
        });
        if (stopping || (!pipe && !endpoint)) { return; }
        beginClose();
        browserSession = endpoint ? browser.id : undefined;
        const channel = vscode.window.createOutputChannel(tr('Sunfleck 检查器', 'Sunfleck Inspector'));
        let snapshot;
        let page;
        let browserPick;
        const waiting = endpoint
            ? tr('等待 Edge 页面发送调试快照', 'Waiting for the browser runtime snapshot')
            : tr('等待应用连接，请启动调试', 'Waiting for application; start debugging');
        let status = waiting;
        let lastError = '';
        let disposed = false;
        let reading = false;
        let resolveClosed;
        const closed = new Promise(resolve => { resolveClosed = resolve; });
        let disconnecting;
        const owner = randomUUID();
        const disconnect = () => disconnecting ??= Promise.resolve()
            .then(async () => {
                if (pipe) await debugRequest('sunfleck/debugDisconnect', { pipe, owner });
                if (page) {
                    await highlightTask;
                    await browserCommand(endpoint, page, 'clear');
                }
            })
            .catch(() => {}).then(() => resolveClosed());
        let children = new Map();
        let previousSelection = '0';
        let selectedDiagnostic;
        let commandStatus;
        let highlightTarget = '0';
        let highlightTask;
        hover = id => {
            highlightTarget = id && snapshot?.nodes.some(node => node.id === id) ? id : '0';
            if (disposed || highlightTask || !(page || snapshot?.debugProtocol?.methods?.includes('highlight'))) return;
            const send = async () => {
                let sent;
                do {
                    sent = highlightTarget;
                    if (page) await browserCommand(endpoint, page, 'highlight', sent);
                    else await debugRequest('sunfleck/debugRequest', { pipe, method: 'highlight', object: sent });
                } while (!disposed && sent !== highlightTarget);
            };
            highlightTask = send().catch(() => {}).finally(() => { highlightTask = undefined; });
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
            if (!(source?.uri && source.range?.line > 0 && source.range.column > 0)) return;
            try {
                const uri = await resolveSourceUri(source.uri, browser);
                if (!uri) {
                    void vscode.window.showWarningMessage(tr('无法唯一定位源码文件', 'Could not uniquely locate the source file') +
                        ': ' + source.uri);
                    return;
                }
                const at = new vscode.Position(source.range.line - 1, source.range.column - 1);
                if (!disposed) await vscode.window.showTextDocument(uri, { selection: new vscode.Range(at, at) });
            } catch (error) {
                void vscode.window.showErrorMessage(tr('无法打开源码', 'Could not open the source file') + ': ' + error.message);
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
                if (page) {
                    const request = randomUUID();
                    await browserCommand(endpoint, page, 'pick', request);
                    if (disposed) return;
                    browserPick = { request, deadline: Date.now() + 60000 };
                    commandStatus = tr('前往 Edge 点击元素，按 Esc 取消', 'Click an element in the browser or press Esc to cancel');
                    changed.fire();
                    return;
                }
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
        activeProvider = {
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
                        ...(pipe || page ? [{ ...item(tr('拾取运行时对象', 'Pick Runtime Object')),
                            command: { command: 'sunfleck.pickObject', title: tr('拾取运行时对象', 'Pick Runtime Object') } }] : []),
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
        view.reset();
        changed.fire();
        const locate = vscode.commands.registerCommand('sunfleck.locateRuntimeObject', async (objectId) => {
            const editor = vscode.window.activeTextEditor;
            if (!editor || !snapshot) {
                return;
            }
            const nodes = snapshot.nodes;
            const position = editor.selection.active;
            const shared = pipe && await debugRequest('sunfleck/debugLocate', { pipe, uri: editor.document.uri.toString(),
                line: position.line + 1, column: position.character + 1 });
            let matches = shared?.nodes;
            if (!matches) {
                const ranges = nodes.filter(node => {
                    const source = node.source;
                    const range = source?.range;
                    return source?.uri && range?.line > 0 && range.column > 0 && range.endLine > 0 && range.endColumn > 0 &&
                        new vscode.Range(range.line - 1, range.column - 1, range.endLine - 1, range.endColumn - 1).contains(position) &&
                        !position.isEqual(new vscode.Position(range.endLine - 1, range.endColumn - 1));
                });
                const uris = new Map();
                await Promise.all([...new Set(ranges.map(node => node.source.uri))].map(async sourceUri => {
                    const uri = await resolveSourceUri(sourceUri, browser).catch(() => undefined);
                    uris.set(sourceUri, uri?.toString());
                }));
                matches = ranges.filter(node => uris.get(node.source.uri) === editor.document.uri.toString());
            }
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
                const browserState = endpoint ? await requestSnapshot(endpoint) : undefined;
                const next = endpoint ? browserState?.snapshot : await debugRequest('sunfleck/debugSnapshot', { pipe, owner });
                if (!next) {
                    throw new Error(endpoint ? waiting : tr('语言服务未连接', 'Language server is not connected'));
                }
                if (disposed) {
                    return;
                }
                if (next.version !== 1 || !Array.isArray(next.nodes) || !Array.isArray(next.windows) || !Number.isFinite(next.timestamp)) {
                    throw new Error(tr('不支持的快照格式', 'Unsupported snapshot format'));
                }
                lastError = '';
                const pageChanged = page !== browserState?.page;
                if (pageChanged) {
                    if (snapshot) view.reset();
                    page = browserState.page;
                    browserPick = undefined;
                    commandStatus = undefined;
                }
                if (browserPick && Date.now() >= browserPick.deadline) {
                    browserPick = undefined;
                    commandStatus = tr('拾取已超时', 'Picking timed out');
                    changed.fire();
                }
                const caption = endpoint && !page
                    ? tr('预览服务需要重启以启用拾取和高亮', 'Restart the preview server to enable picking and highlighting')
                    : Date.now() - next.timestamp > (endpoint ? 5000 : 1500)
                        ? tr('快照未更新', 'Snapshot idle') : tr('已连接', 'Connected');
                if (pageChanged || snapshot?.timestamp !== next.timestamp || status !== caption) {
                    const ids = new Map(next.nodes.map(node => [`${node.window}:${node.id}`, node]));
                    const groups = new Map();
                    for (const node of next.nodes) {
                        const parent = `${node.window}:${next.inspector?.parents?.[node.id] ?? node.parent}`;
                        const key = ids.has(parent) ? parent : `${node.window}:0`;
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
                    snapshot = next;
                    status = caption;
                    changed.fire();
                    if (selectedDiagnostic) {
                        await showDiagnostic(selectedDiagnostic, false);
                    }
                    if (browserPick && next.browserPick?.request === browserPick.request) {
                        browserPick = undefined;
                        const id = next.browserPick.selection;
                        commandStatus = id === '0' ? tr('拾取已结束', 'Picking ended') : undefined;
                        changed.fire();
                        if (id !== '0') await selection(id);
                    }
                    if (!endpoint && next.selection && next.selection !== '0' && next.selection !== previousSelection) {
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
                    waiting;
                if (!disposed && lastError !== error.message) {
                    lastError = error.message;
                    channel.appendLine(`${endpoint || pipe}: ${error.message}`);
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
            hover = undefined;
            activeProvider = undefined;
            view.reset();
            changed.fire();
            channel.dispose();
            inspect.dispose();
            inspectDiagnostic.dispose();
            pick.dispose();
            locate.dispose();
            void vscode.commands.executeCommand('setContext', 'sunfleck.snapshotOpen', false);
            return closed;
        };
        void refresh();
        await vscode.commands.executeCommand('setContext', 'sunfleck.snapshotOpen', true);
        if (!disposed) await vscode.commands.executeCommand('sunfleck.objects.focus');
    };
    context.subscriptions.push(vscode.commands.registerCommand('sunfleck.inspectSnapshot', selected => {
        const active = vscode.debug.activeDebugSession;
        return openSnapshot(selected, !selected?.pipe && active && snapshotEndpoint(active) ? active : undefined);
    }));
    if (vscode.debug) {
        for (const type of browserDebugTypes) {
            context.subscriptions.push(vscode.debug.registerDebugConfigurationProvider(type, {
                resolveDebugConfigurationWithSubstitutedVariables: resolveBrowserConfiguration
            }));
        }
        const attachBrowser = session => {
            if (snapshotEndpoint(session)) {
                void openSnapshot(undefined, session);
            }
        };
        context.subscriptions.push(
            vscode.debug.onDidStartDebugSession(attachBrowser),
            vscode.debug.onDidChangeActiveDebugSession(session => { if (session) attachBrowser(session); }),
            vscode.debug.onDidTerminateDebugSession(session => {
                if (session.id === browserSession) {
                    browserSession = undefined;
                    void closeSnapshot();
                }
            })
        );
        if (vscode.debug.activeDebugSession) {
            attachBrowser(vscode.debug.activeDebugSession);
        }
    }
    return async () => {
        stopping = true;
        await closeSnapshot();
    };
}

module.exports = { registerInspector };
