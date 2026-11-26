'use strict';
const vscode = require('vscode');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

module.exports = async function editing({ fixturePath, output, result, extension, until }) {
    const fixture = JSON.parse(fs.readFileSync(fixturePath, 'utf8').replace(/^\uFEFF/, ''));
    const clients = new Set(), subscriptions = [], cases = [], protocol = [], actions = [];
    const point = value => ({ line: value.line, character: value.character });
    const range = value => ({ start: point(value.start), end: point(value.end) });
    const symbol = value => ({ name: value.name, kind: value.kind, range: range(value.range),
        selectionRange: range(value.selectionRange), children: value.children.map(symbol) });
    let stage = 'opening', request = 0;
    const write = (name, value) => fs.writeFileSync(path.join(output, name + '.json'), JSON.stringify(value, null, 2));
    const record = value => {
        protocol.push(value);
        fs.appendFileSync(path.join(output, 'editing-protocol.jsonl'), JSON.stringify(value) + '\n');
    };
    await extension.activate();
    const modulePath = path.join(extension.extensionPath, 'node_modules/vscode-languageclient/lib/node/main.js');
    const loaded = Object.values(require.cache).filter(value => path.resolve(value.filename).toLowerCase() === path.resolve(modulePath).toLowerCase());
    assert.equal(loaded.length, 1);
    const prototype = loaded[0].exports.LanguageClient.prototype;
    const own = Object.hasOwn(prototype, 'sendRequest'), original = prototype.sendRequest;
    prototype.sendRequest = function (method, params, ...rest) {
        if (!clients.has(this)) {
            clients.add(this);
            subscriptions.push(this.getFeature('textDocument/didChange').onNotificationSent(event => record({
                stage, method: event.type.method, params: event.params, servicePid: this._serverProcess?.pid
            })));
        }
        const name = typeof method === 'string' ? method : method.method;
        const observation = { stage, request: ++request, method: name, params, servicePid: this._serverProcess?.pid };
        const answer = original.call(this, method, params, ...rest);
        if (!name.startsWith('textDocument/')) { return answer; }
        return answer.then(response => { record({ ...observation, response }); return response; }, error => {
            record({ ...observation, error: { code: error.code, message: error.message } }); throw error;
        });
    };
    try {
        const document = await vscode.workspace.openTextDocument(fixture.Fleck);
        const editor = await vscode.window.showTextDocument(document);
        const at = offset => { const point = document.positionAt(offset); editor.selection = new vscode.Selection(point, point); };
        const command = async (name, args) => {
            const before = document.getText(), version = document.version;
            await vscode.commands.executeCommand(name, args);
            const action = { stage, command: name, args, beforeVersion: version, version: document.version, before, after: document.getText() };
            actions.push(action);
            fs.appendFileSync(path.join(output, 'editing-actions.jsonl'), JSON.stringify(action) + '\n');
        };
        const type = async text => {
            for (const character of text.match(/\r\n|[\s\S]/gu) || []) { await command('type', { text: character }); }
        };
        const complete = async (label, expected) => {
            const point = editor.selection.active;
            const before = document.getText();
            const list = await until(() => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, point),
                value => value?.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label) === label),
                'Missing completion: ' + stage + '/' + label);
            await vscode.commands.executeCommand('editor.action.triggerSuggest');
            await until(async () => {
                if (document.getText() !== expected) { await command('acceptSelectedSuggestion'); }
                return document.getText();
            }, value => value === expected, 'Suggestion commit differs: ' + label);
            const evidence = { stage, label, point: { line: point.line, character: point.character }, before, expected,
                suggestions: list.items.map(item => typeof item.label === 'string' ? item.label : item.label.label) };
            cases[cases.length - 1].completions.push(evidence);
        };
        const close = async name => {
            const before = document.getText(), offset = document.offsetAt(editor.selection.active);
            await command('type', { text: '>' });
            const expected = before.slice(0, offset) + '></' + name + '>' + before.slice(offset);
            await until(() => document.getText(), value => value === expected, 'Auto close failed: ' + name);
            cases[cases.length - 1].closings.push({ name, expected });
            await vscode.commands.executeCommand('leaveSnippet');
            at(offset + 1);
        };
        const append = async text => {
            await vscode.commands.executeCommand('leaveSnippet');
            await type(text);
        };
        for (const ending of ['lf', 'crlf']) {
            const eol = ending === 'lf' ? '\n' : '\r\n';
            for (const root of ['Window', 'Theme']) {
                stage = root.toLowerCase() + '-' + ending;
                const current = { name: stage, root, ending, completions: [], closings: [] };
                cases.push(current);
                await vscode.commands.executeCommand('leaveSnippet');
                assert.ok(await editor.edit(edit => {
                    edit.replace(new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)), '');
                    edit.setEndOfLine(ending === 'lf' ? vscode.EndOfLine.LF : vscode.EndOfLine.CRLF);
                }));
                at(0);
                await type(root === 'Window' ? '<Win' : '<The');
                await complete(root, '<' + root);
                await append(' xmlns="urn:sunfleck:v1" ' + (root === 'Window' ? 'Tit' : 'Ba'));
                const prefix = '<' + root + ' xmlns="urn:sunfleck:v1" ';
                await complete(root === 'Window' ? 'Title' : 'Base', prefix + (root === 'Window' ? 'Title=""' : 'Base=""'));
                if (root === 'Window') {
                    await type('中文🌱');
                    await vscode.commands.executeCommand('leaveSnippet');
                    at(document.getText().length);
                    await append(' Width="640"');
                } else {
                    await type('Flu');
                    await complete('Fluent2', prefix + 'Base="Fluent2"');
                    await vscode.commands.executeCommand('leaveSnippet');
                    at(document.getText().length);
                }
                await close(root);
                await append(eol + '<!--中文🌱-->' + eol + (root === 'Window' ? '<StackP' : '<DockT'));
                const nested = root === 'Window' ? 'StackPanel' : 'DockTemplate';
                let text = document.getText();
                await complete(nested, text.replace(root === 'Window' ? '<StackP' : '<DockT', '<' + nested));
                if (root === 'Theme') {
                    await append(' Pa');
                    text = document.getText();
                    await complete('Part', text.replace(' Pa', ' Part=""'));
                    await type('Tit');
                    await complete('Title', document.getText().replace('Part="Tit"', 'Part="Title"'));
                    await vscode.commands.executeCommand('leaveSnippet');
                    at(document.getText().indexOf('</Theme>'));
                }
                await close(nested);
                if (root === 'Window') {
                    await append(eol + '<But');
                    await complete('Button', document.getText().replace('<But', '<Button'));
                    await append(' IsE');
                    await complete('IsEnabled', document.getText().replace(' IsE', ' IsEnabled=""'));
                    await type('tr');
                    await complete('true', document.getText().replace('IsEnabled="tr"', 'IsEnabled="true"'));
                    await vscode.commands.executeCommand('leaveSnippet');
                    at(document.getText().indexOf('</StackPanel>'));
                    await append('/>' + eol);
                } else {
                    await append(eol + '<Fill/>' + eol);
                }
                at(document.getText().indexOf('</' + root + '>'));
                await append(eol);
                const valid = document.getText();
                await until(() => vscode.languages.getDiagnostics(document.uri), values => values.length === 0, 'Valid document rejected: ' + stage);
                at(valid.length);
                await command('deleteLeft');
                const malformed = document.getText();
                const invalid = await until(() => vscode.languages.getDiagnostics(document.uri), values => values.some(value => String(value.code).startsWith('fleck_')), 'Deleted closing delimiter not diagnosed');
                current.malformed = { text: malformed, codes: invalid.map(value => value.code) };
                await command('type', { text: '>' });
                assert.equal(document.getText(), valid);
                const recovered = await until(() => vscode.languages.getDiagnostics(document.uri), values => values.length === 0, 'Diagnostics did not recover');
                const legend = await vscode.commands.executeCommand('vscode.provideDocumentSemanticTokensLegend', document.uri);
                assert.ok(legend?.tokenTypes.includes('type') && legend.tokenTypes.includes('property') && legend.tokenTypes.includes('string'));
                const semantic = await until(() => vscode.commands.executeCommand('vscode.provideDocumentSemanticTokens', document.uri),
                    value => value?.data?.length > 0, 'Semantic tokens unavailable');
                const tokens = [];
                let line = 0, column = 0, previousEnd = 0;
                const lines = valid.split(/\r?\n/);
                for (let i = 0; i < semantic.data.length; i += 5) {
                    const [delta, start, length, kind, modifiers] = semantic.data.slice(i, i + 5);
                    column = delta ? start : column + start;
                    line += delta;
                    assert.ok(length > 0 && line < lines.length && column + length <= lines[line].length);
                    const offset = document.offsetAt(new vscode.Position(line, column));
                    assert.ok(offset >= previousEnd, 'Overlapping or unsorted semantic spans');
                    previousEnd = offset + length;
                    const token = { line, column, length, type: legend.tokenTypes[kind], modifiers, text: lines[line].slice(column, column + length) };
                    assert.ok(['type', 'property', 'string'].includes(token.type));
                    assert.equal(modifiers, 0);
                    assert.ok(!token.text.includes('<!--'), 'Comment was classified as an element');
                    tokens.push(token);
                }
                for (const [typeName, tokenText] of root === 'Window' ?
                    [['type', 'Window'], ['type', 'StackPanel'], ['type', 'Button'], ['property', 'Title'], ['string', '中文🌱'], ['property', 'Width'], ['string', '640'], ['property', 'IsEnabled'], ['string', 'true']] :
                    [['type', 'Theme'], ['type', 'DockTemplate'], ['type', 'Fill'], ['property', 'Base'], ['string', 'Fluent2'], ['property', 'Part'], ['string', 'Title']]) {
                    assert.ok(tokens.some(token => token.type === typeName && token.text === tokenText), 'Semantic classification missing: ' + tokenText);
                }
                const symbols = await vscode.commands.executeCommand('vscode.executeDocumentSymbolProvider', document.uri);
                assert.equal(symbols.length, 1);
                assert.equal(symbols[0].name, root);
                assert.equal(symbols[0].children.length, 1);
                assert.equal(symbols[0].children[0].name, nested);
                assert.equal(symbols[0].children[0].children[0].name, root === 'Window' ? 'Button' : 'Fill');
                assert.ok(await document.save());
                assert.equal(fs.readFileSync(fixture.Fleck, 'utf8'), valid);
                fs.writeFileSync(path.join(output, stage + '.fleck'), valid);
                assert.equal(clients.size, 1);
                const client = [...clients][0];
                assert.ok(Number.isInteger(client._serverProcess?.pid));
                Object.assign(current, { text: valid, uri: document.uri.toString(), recoveredDiagnostics: recovered, tokens, legend, rawTokens: [...semantic.data],
                    symbols: symbols.map(symbol), servicePid: client._serverProcess.pid, serviceArguments: client._serverProcess.spawnargs });
                write(stage, current);
                result.passed.push('incremental-completion-close-recovery-semantics-symbols-save-' + stage);
            }
        }
        assert.ok(!protocol.some(value => value.error && value.error.code !== -32800), 'Unexpected language protocol error');
        write('editing-matrix', { cases: cases.map(value => value.name), clientInstances: clients.size,
            servicePid: cases[0].servicePid, protocolRecords: protocol.length, actions: actions.length, clientModule: loaded[0].filename,
            scope: 'Actual VS Code type/delete/suggestion commands and language APIs; no physical keyboard, IME or rendered-color claim' });
    } finally {
        for (const subscription of subscriptions) { subscription.dispose(); }
        if (own) { prototype.sendRequest = original; } else { delete prototype.sendRequest; }
    }
};
