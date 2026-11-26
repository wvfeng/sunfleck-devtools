'use strict';
const vscode = require('vscode');
const path = require('node:path');
const fs = require('node:fs');
const { LanguageClient, TransportKind, ErrorAction, CloseAction } = require('vscode-languageclient/node');
const { registerInspector } = require('./inspector');
const { configuredDebugPipe } = require('./configuration');
const { english, tr } = require('./language');

let client;
let restarting = Promise.resolve();
let stopping = false;
let recoveryBlocked = false;
let closeInspector;
const modelWatchers = new Map();

function resolveFile(value, folder) {
    return path.resolve(folder, value.replaceAll('${workspaceFolder}', folder));
}

async function modelFiles() {
    const files = new Set();
    const modelScopes = [];
    if (!vscode.workspace.workspaceFolders?.length) {
        const configured = vscode.workspace.getConfiguration('sunfleck').get('modelFiles', []);
        configured.forEach(file => {
            if (path.isAbsolute(file)) {
                files.add(file);
            }
        });
    }
    for (const folder of vscode.workspace.workspaceFolders || []) {
        const scoped = new Set();
        const configured = vscode.workspace.getConfiguration('sunfleck', folder.uri).get('modelFiles', []);
        if (configured.length > 0) {
            configured.forEach(file => scoped.add(resolveFile(file, folder.uri.fsPath)));
        } else {
            const matches = await vscode.workspace.findFiles(new vscode.RelativePattern(folder, '**/*.fleckmodel.json'), '**/{.git,node_modules,obj}/**', 128);
            matches.forEach(file => scoped.add(file.fsPath));
        }
        modelScopes.push({ uri: folder.uri.toString(), modelFiles: [...scoped] });
    }
    return { modelFiles: [...files], modelScopes };
}

function watchedFiles(models) {
    return [...models.modelFiles, ...models.modelScopes.flatMap(scope => scope.modelFiles)];
}

async function updateModels() {
    const current = client;
    if (!stopping && current?.isRunning()) {
        const files = await modelFiles();
        if (!stopping && current === client && current.isRunning()) {
            watchModelFiles(watchedFiles(files));
            await current.sendNotification('workspace/didChangeConfiguration', { settings: { sunfleck: { ...files, language: english() ? 'en' : 'zh-CN' } } });
        }
    }
}

function watchModelFiles(files) {
    const wanted = new Set(files);
    for (const [file, watcher] of modelWatchers) {
        if (!wanted.has(file)) {
            watcher.dispose();
            modelWatchers.delete(file);
        }
    }
    for (const file of wanted) {
        if (!modelWatchers.has(file)) {
            const watcher = vscode.workspace.createFileSystemWatcher(
                new vscode.RelativePattern(vscode.Uri.file(path.dirname(file)), '*.fleckmodel.json'));
            const onChange = uri => {
                if (path.relative(file, uri.fsPath) === '') {
                    void updateModels();
                }
            };
            watcher.onDidCreate(onChange);
            watcher.onDidChange(onChange);
            watcher.onDidDelete(onChange);
            modelWatchers.set(file, watcher);
        }
    }
}

async function start(context) {
    if (stopping || !vscode.workspace.isTrusted) {
        return;
    }
    recoveryBlocked = false;
    if (client) {
        await client.stop();
        client = undefined;
    }
    const firstFolder = vscode.workspace.workspaceFolders?.[0];
    const config = vscode.workspace.getConfiguration('sunfleck', firstFolder?.uri);
    const folder = firstFolder?.uri.fsPath || context.extensionPath;
    const configured = config.get('serverPath', '');
    const bundled = path.join(context.extensionPath, 'server', 'Sunfleck.Development.dll');
    const server = configured ? resolveFile(configured, folder) : fs.existsSync(bundled) ? bundled : path.join(folder, 'out', 'devtools', 'tools', 'development', 'Sunfleck.Development.dll');
    if (!fs.existsSync(server)) {
        await vscode.window.showErrorMessage(tr('未找到 Fleck 语言服务。请构建 sunfleck_development，并配置 sunfleck.serverPath。', 'Fleck language server not found. Build sunfleck_development and configure sunfleck.serverPath.'));
        return;
    }
    const files = await modelFiles();
    let recovery;
    const next = new LanguageClient('sunfleck', 'Sunfleck Fleck', {
        command: config.get('dotnetPath', 'dotnet'),
        args: [server, 'lsp'],
        transport: TransportKind.stdio,
        options: { cwd: path.dirname(server) }
    }, {
        documentSelector: [{ scheme: 'file', language: 'fleck' }],
        middleware: {
            async provideOnTypeFormattingEdits(document, position, ch, options, token, next) {
                const version = document.version;
                const edits = await next(document, position, ch, options, token);
                const editor = vscode.window.activeTextEditor;
                // LSP TextEdit insertion moves the caret past the closing tag.
                // Apply this edit with an explicit caret, while the original
                // version and selection still identify the user's typed '>'.
                if (ch === '>' && edits?.length === 1 && /^<\/[\w:.-]+>$/.test(edits[0].newText) &&
                    edits[0].range.isEmpty && edits[0].range.start.isEqual(position) &&
                    !token.isCancellationRequested && document.version === version &&
                    editor?.document === document && editor.selections.length === 1 &&
                    editor.selection.isEmpty && editor.selection.active.isEqual(position)) {
                    const applied = await editor.edit(builder => builder.insert(position, edits[0].newText),
                        { undoStopBefore: false, undoStopAfter: false });
                    if (applied && vscode.window.activeTextEditor === editor && document.version === version + 1) {
                        editor.selection = new vscode.Selection(position, position);
                    }
                    return [];
                }
                return edits;
            }
        },
        initializationOptions: { ...files, language: english() ? 'en' : 'zh-CN' },
        errorHandler: {
            error: async (...args) => {
                const response = await recovery.error(...args);
                if (client === next && response.action === ErrorAction.Shutdown) {
                    recoveryBlocked = true;
                    return { ...response, message: tr(
                        'Fleck 语言服务连接发生故障，已停止。请检查输出日志后执行「Sunfleck: 重启语言服务」。',
                        'Fleck language server connection failed and has stopped. Check the output log, then run Sunfleck: Restart Language Server.') +
                        '\n' + args[0].message };
                }
                return response;
            },
            closed: async () => {
                const response = await recovery.closed();
                if (client === next && response.action === CloseAction.DoNotRestart) {
                    recoveryBlocked = true;
                    return { ...response, message: tr(
                        'Fleck 语言服务连续故障，已停止自动恢复。请排查输出日志后执行「Sunfleck: 重启语言服务」。',
                        'Fleck language server repeatedly failed. Automatic recovery has stopped. Check the output log, then run Sunfleck: Restart Language Server.') };
                }
                return response;
            }
        }
    });
    recovery = next.createDefaultErrorHandler();
    client = next;
    try {
        await next.start();
        watchModelFiles(watchedFiles(files));
    } catch (error) {
        if (client === next) {
            client = undefined;
        }
        await vscode.window.showErrorMessage(tr('Fleck 语言服务启动失败', 'Fleck language server failed to start') + ': ' + error.message);
    }
}

function restart(context) {
    restarting = restarting.then(() => start(context), () => start(context));
    return restarting;
}

function ensureStarted(context) {
    // A poll during an explicit restart must wait for that restart. Recheck
    // inside the queue so concurrent demand never stops a newly started client.
    const resume = async () => {
        if (stopping || recoveryBlocked || client?.isRunning()) { return; }
        if (!client) { return start(context); }
        // Retain this client's error history during automatic recovery. Creating
        // a fresh client here would reset its crash budget on every failed poll.
        const current = client;
        try { await current.start(); }
        catch (error) {
            if (client === current) { recoveryBlocked = true; }
            throw error;
        }
    };
    restarting = restarting.then(resume, resume);
    return restarting;
}

async function activate(context) {
    stopping = false;
    recoveryBlocked = false;
    closeInspector = registerInspector(context, async (method, parameters) => {
        if (stopping && method !== 'sunfleck/debugDisconnect') { return undefined; }
        if (!client?.isRunning()) {
            if (method === 'sunfleck/debugDisconnect') {
                return undefined;
            }
            await ensureStarted(context);
        }
        if (stopping && method !== 'sunfleck/debugDisconnect') { return undefined; }
        return client?.isRunning() ? client.sendRequest(method, parameters) : undefined;
    });
    const debugPipe = configuredDebugPipe();
    if (debugPipe) {
        void vscode.commands.executeCommand('sunfleck.inspectSnapshot', { pipe: debugPipe });
    }
    const updateLanguage = () => vscode.commands.executeCommand('setContext', 'sunfleck.english', english());
    await updateLanguage();
    context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(event => {
        if (event.affectsConfiguration('sunfleck.language')) {
            void updateLanguage();
            void updateModels();
        }
    }));
    for (const command of ['restartLanguageServer', 'inspectSnapshot', 'closeSnapshot', 'runDevelopmentTask', 'locateRuntimeObject']) {
        context.subscriptions.push(vscode.commands.registerCommand('sunfleck.' + command + 'English',
            (...args) => vscode.commands.executeCommand('sunfleck.' + command, ...args)));
    }
    context.subscriptions.push(vscode.commands.registerCommand('sunfleck.runDevelopmentTask', async (name) => {
        if (!vscode.workspace.isTrusted) {
            return;
        }
        const tasks = (await vscode.tasks.fetchTasks()).filter(task => task.name.startsWith('Sunfleck: '));
        const choices = tasks.map(task => ({
            label: task.name, description: task.source, task
        }));
        const chosen = typeof name === 'string' ? choices.find(choice => choice.label === name) :
            await vscode.window.showQuickPick(choices, { placeHolder: tr('选择工作区中配置的 Sunfleck 开发任务', 'Select a Sunfleck development task configured in this workspace') });
        if (chosen) {
            return await vscode.tasks.executeTask(chosen.task);
        } else if (!tasks.length) {
            void vscode.window.showInformationMessage(tr('工作区尚未配置名称以 Sunfleck: 开头的任务。', 'This workspace has no task whose name starts with Sunfleck: .'));
        }
    }));
    context.subscriptions.push(vscode.commands.registerCommand('sunfleck.restartLanguageServer', () => restart(context)));
    const watcher = vscode.workspace.createFileSystemWatcher('**/*.fleckmodel.json');
    context.subscriptions.push(watcher, watcher.onDidCreate(updateModels), watcher.onDidChange(updateModels), watcher.onDidDelete(updateModels));
    const typeWatcher = vscode.workspace.createFileSystemWatcher('**/*.{cs,csproj,fleck,fleckcomponents.json}');
    const typesChanged = uri => {
        if (client?.isRunning() && !stopping) {
            void client.sendNotification('workspace/didChangeWatchedFiles', { changes: [{ uri: uri.toString(), type: 2 }] });
        }
    };
    context.subscriptions.push(typeWatcher, typeWatcher.onDidCreate(typesChanged), typeWatcher.onDidChange(typesChanged), typeWatcher.onDidDelete(typesChanged));
    context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(event => {
        if (event.affectsConfiguration('sunfleck.serverPath') || event.affectsConfiguration('sunfleck.dotnetPath')) {
            void restart(context);
        } else if (event.affectsConfiguration('sunfleck.modelFiles')) {
            void updateModels();
        }
    }));
    context.subscriptions.push(vscode.workspace.onDidChangeWorkspaceFolders(updateModels));
    context.subscriptions.push(vscode.workspace.onDidGrantWorkspaceTrust(() => restart(context)));
    if (vscode.workspace.textDocuments.some(document => document.languageId === 'fleck')) {
        await ensureStarted(context);
    } else {
        const onOpen = vscode.workspace.onDidOpenTextDocument(document => {
            if (document.languageId === 'fleck') {
                onOpen.dispose();
                if (!client?.isRunning()) {
                    void ensureStarted(context);
                }
            }
        });
        context.subscriptions.push(onOpen);
    }
}

async function deactivate() {
    stopping = true;
    await restarting;
    await closeInspector?.();
    closeInspector = undefined;
    watchModelFiles([]);
    if (client) {
        await client.stop();
        client = undefined;
    }
}

module.exports = { activate, deactivate };
