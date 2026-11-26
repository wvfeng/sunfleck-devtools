'use strict';
const vscode = require('vscode');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

module.exports = async function unicode({ fixturePath, output, result, extension, until }) {
    const fixture = JSON.parse(fs.readFileSync(fixturePath, 'utf8').replace(/^\uFEFF/, ''));
    const local = value => path.resolve(value).toLowerCase();
    const point = value => ({ line: value.line, character: value.character });
    const range = value => ({ start: point(value.start), end: point(value.end) });
    const write = (name, value) => fs.writeFileSync(path.join(output, name + '.json'), JSON.stringify(value, null, 2));
    const source = fs.readFileSync(fixture.Source, 'utf8');
    const sourceLines = source.split(/\r?\n/);
    const sourceLine = sourceLines.findIndex(line => line.includes('Caption'));
    const declaration = { line: sourceLine, character: sourceLines[sourceLine].indexOf('Caption') };
    assert.ok(declaration.character > [...sourceLines[sourceLine].slice(0, declaration.character)].length);
    let stage = 'opening';
    const clients = new Set(), records = [], originals = new Map(), cases = [], subscriptions = [];
    await extension.activate();
    const modulePath = path.join(extension.extensionPath, 'node_modules/vscode-languageclient/lib/node/main.js');
    const modules = Object.values(require.cache).filter(value => local(value.filename) === local(modulePath));
    assert.equal(modules.length, 1);
    const prototype = modules[0].exports.LanguageClient.prototype;
    const record = value => {
        records.push(value);
        fs.appendFileSync(path.join(output, 'unicode-protocol.jsonl'), JSON.stringify(value) + '\n');
    };
    for (const name of ['sendRequest']) {
        originals.set(name, { own: Object.hasOwn(prototype, name), value: prototype[name] });
        const original = prototype[name];
        prototype[name] = function (method, params, ...rest) {
            if (!clients.has(this)) {
                clients.add(this);
                // Full synchronization bypasses client.sendNotification; this event fires
                // only after the actual connection has sent the document notification.
                subscriptions.push(this.getFeature('textDocument/didChange').onNotificationSent(event => record({
                    stage, method: event.type.method, params: event.params, servicePid: this._serverProcess?.pid
                })));
            }
            const answer = original.call(this, method, params, ...rest);
            const methodName = typeof method === 'string' ? method : method.method;
            if (!['textDocument/completion', 'textDocument/definition', 'textDocument/hover'].includes(methodName)) { return answer; }
            const observation = { stage, method: methodName, params, servicePid: this._serverProcess?.pid };
            return answer.then(response => { record({ ...observation, response }); return response; },
                error => { record({ ...observation, error: { code: error.code, message: error.message } }); throw error; });
        };
    }
    try {
        const document = await vscode.workspace.openTextDocument(fixture.Fleck);
        const editor = await vscode.window.showTextDocument(document, { preview: false, preserveFocus: false });
        assert.equal(document.languageId, 'fleck');
        assert.ok(document.uri.toString().includes('%2520'), 'The literal percent path must be encoded once');
        assert.equal(vscode.workspace.getConfiguration('sunfleck', document.uri).get('serverPath', ''), '');
        assert.equal(vscode.workspace.getConfiguration('editor', document.uri).get('wordBasedSuggestions'), 'off');
        for (const ending of ['lf', 'crlf']) {
            const eol = ending === 'lf' ? '\n' : '\r\n';
            for (const [name, prefix] of [['ascii', 'prefix'], ['cjk', '中文前缀'], ['astral', '中文🌱前缀']]) {
                stage = name + '-' + ending;
                const tag = `<!--${prefix}-->`;
                const typed = ["<Window xmlns='urn:sunfleck:v1' Model='UnicodeModel'>", '  <StackPanel>',
                    `    ${tag}<TextBlock Text='{Binding Cap}'/>`,
                    `    ${tag}<TextBox Text='{Binding ReadOnly}'/>`,
                    `    ${tag}<Button Command='{Binding SyncCommand}' CommandMode='Async'/>`,
                    `    ${tag}<TextBlock Text='{Binding MissingPath}'/>`, '  </StackPanel>', '</Window>'].join(eol);
                assert.ok(await editor.edit(builder => builder.setEndOfLine(ending === 'lf' ? vscode.EndOfLine.LF : vscode.EndOfLine.CRLF)));
                await vscode.window.showTextDocument(document, { preview: false, preserveFocus: false });
                await vscode.commands.executeCommand('workbench.action.focusActiveEditorGroup');
                editor.selection = new vscode.Selection(document.positionAt(0), document.positionAt(document.getText().length));
                await until(() => vscode.window.activeTextEditor,
                    current => current?.document === document && current.selection.isEqual(editor.selection),
                    'Unicode fixture editor must own typing focus: ' + stage);
                await vscode.commands.executeCommand('type', { text: typed });
                await until(() => document.getText(), value => value === typed, 'Actual type command must replace the selected fixture: ' + stage);
                const completionPoint = document.positionAt(typed.indexOf('{Binding Cap}') + '{Binding Cap'.length);
                editor.selection = new vscode.Selection(completionPoint, completionPoint);
                const suggestions = await until(() => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, completionPoint),
                    value => value?.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'Caption'), 'Unicode completion not ready: ' + stage);
                await vscode.commands.executeCommand('editor.action.triggerSuggest');
                const committed = typed.replace('{Binding Cap}', '{Binding Caption}');
                await until(async () => {
                    if (document.getText() !== committed) { await vscode.commands.executeCommand('acceptSelectedSuggestion'); }
                    return document.getText();
                }, value => value === committed, 'Real suggestion acceptance failed: ' + stage);
                const captionOffset = committed.indexOf('{Binding Caption}') + '{Binding '.length;
                const captionPoint = document.positionAt(captionOffset + 2);
                const captionRange = new vscode.Range(document.positionAt(captionOffset), document.positionAt(captionOffset + 'Caption'.length));
                const definitions = await until(() => vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, captionPoint), values => values?.some(value => {
                    const location = value.targetUri || value.uri;
                    const selection = value.targetSelectionRange || value.range;
                    return local(location.fsPath) === local(fixture.Source) && selection.start.isEqual(new vscode.Position(declaration.line, declaration.character));
                }), 'Unicode definition offset/path mismatch: ' + stage);
                const hovers = await until(() => vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri, captionPoint), values => values?.some(value =>
                    value.range?.isEqual(captionRange) && value.contents.some(content => content.value?.includes('UnicodeModel.Caption') && content.value.includes('string'))),
                'Unicode hover range mismatch: ' + stage);
                const expectedDiagnostics = [
                    { code: 'read_only', text: 'ReadOnly' }, { code: 'command_mismatch', text: 'SyncCommand' }, { code: 'path_missing', text: 'MissingPath' }
                ].map(value => {
                    const offset = committed.indexOf('{Binding ' + value.text) + '{Binding '.length;
                    return { ...value, range: new vscode.Range(document.positionAt(offset), document.positionAt(offset + value.text.length)) };
                });
                const diagnostics = await until(() => vscode.languages.getDiagnostics(document.uri), values => values.length === expectedDiagnostics.length &&
                    expectedDiagnostics.every(expected => values.some(value => value.code === expected.code && value.range.isEqual(expected.range) && document.getText(value.range) === expected.text)),
                'Unicode diagnostic ranges mismatch: ' + stage);
                assert.ok(await document.save());
                assert.equal(fs.readFileSync(fixture.Fleck, 'utf8'), committed, 'Save changed Unicode bytes or line endings');
                assert.ok(records.some(value => value.stage === stage && value.method === 'textDocument/didChange' &&
                    value.params.contentChanges.some(change => !change.range && change.text === committed)), 'Missing actual committed full sync: ' + stage);
                fs.writeFileSync(path.join(output, stage + '.fleck'), committed);
                const client = [...clients][0];
                assert.ok(Number.isInteger(client?._serverProcess?.pid));
                const evidence = { name: stage, ending, prefix, uri: document.uri.toString(), typed,
                    completionPoint: point(completionPoint), completions: suggestions.items.map(item => typeof item.label === 'string' ? item.label : item.label.label),
                    committedViaCommand: true, declaration, sourceUri: vscode.Uri.file(fixture.Source).toString(),
                    definitions: definitions.map(value => ({ uri: (value.targetUri || value.uri).toString(), range: range(value.targetSelectionRange || value.range) })),
                    hovers: hovers.map(value => ({ range: value.range && range(value.range), contents: value.contents.map(content => content.value) })),
                    diagnostics: diagnostics.map(value => ({ code: value.code, range: range(value.range), text: document.getText(value.range) })),
                    servicePid: client._serverProcess.pid, serviceArguments: client._serverProcess.spawnargs };
                write(stage, evidence);
                cases.push({ name: stage, ending, prefix, servicePid: evidence.servicePid });
                result.passed.push('unicode-type-accept-definition-hover-diagnostics-save-' + stage);
            }
        }
        assert.equal(clients.size, 1);
        assert.equal(new Set(cases.map(value => value.servicePid)).size, 1);
        assert.ok(!records.some(value => value.error));
        write('unicode-matrix', { cases, clientInstances: clients.size, clientModule: modules[0].filename,
            source: fixture.Source, fleck: fixture.Fleck, servicePid: cases[0].servicePid, protocolRecords: records.length,
            scope: 'VS Code type and suggestion commands plus language APIs; no physical keyboard or IME acceptance' });
    } finally {
        for (const subscription of subscriptions) { subscription.dispose(); }
        for (const [name, original] of originals) {
            if (original.own) { prototype[name] = original.value; }
            else { delete prototype[name]; }
        }
    }
};
