'use strict';
const vscode = require('vscode');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

async function run() {
    const project = process.env.SUNFLECK_TEST_CSHARP_PROJECT;
    const output = process.env.SUNFLECK_TEST_OUTPUT;
    const sdk = path.join(project, 'sdk', 'types');
    const result = { passed: [], definitions: [], errors: [] };
    const save = () => fs.writeFileSync(path.join(output, 'csharp-navigation.json'), JSON.stringify(result, null, 2));
    async function until(read, accept, message, timeout = 90000) {
        const deadline = Date.now() + timeout;
        while (Date.now() < deadline) {
            const value = await read();
            if (accept(value)) { return value; }
            await new Promise(resolve => setTimeout(resolve, 200));
        }
        throw new Error(message);
    }
    try {
        const csharp = vscode.extensions.getExtension('ms-dotnettools.csharp');
        assert.ok(csharp, 'Official C# extension must be installed');
        result.csharpVersion = csharp.packageJSON.version;
        await csharp.activate();
        await vscode.workspace.getConfiguration('editor').update('gotoLocation.multipleDefinitions', 'goto', vscode.ConfigurationTarget.Global);
        async function verify(file, needle, symbol, expected) {
            result.current = file + ': ' + needle; save();
            const document = await vscode.workspace.openTextDocument(path.join(sdk, file));
            const editor = await vscode.window.showTextDocument(document);
            assert.equal(document.languageId, 'csharp');
            const start = document.getText().indexOf(needle);
            assert.ok(start >= 0);
            const position = document.positionAt(start + needle.lastIndexOf(symbol) + 1);
            const definitions = await until(
                () => vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, position),
                values => expected.every(name => values?.some(v => path.basename((v.targetUri || v.uri).fsPath) === name)),
                'Missing C# definitions: ' + result.current);
            const targets = definitions.map(d => ({
                file: (d.targetUri || d.uri).fsPath,
                line: (d.targetSelectionRange || d.range).start.line + 1
            }));
            assert.ok(targets.every(t => t.file.toLowerCase().startsWith(sdk.toLowerCase() + path.sep)), 'C# navigation must stay in the SDK');
            result.definitions.push({ file, needle, targets }); save();
            editor.selection = new vscode.Selection(position, position);
            await vscode.commands.executeCommand('editor.action.revealDefinition');
            await until(() => vscode.window.activeTextEditor,
                current => current && targets.some(t => current.document.uri.fsPath.toLowerCase() === t.file.toLowerCase() && current.selection.active.line === t.line - 1),
                'Actual C# F12 did not open the source', 15000);
            result.passed.push(result.current); save();
        }
        await verify('Elements.cs', 'class TextBlock : VisualElement', 'VisualElement', ['Elements.cs']);
        await verify('Elements.cs', 'class VisualElement : global::Sunfleck.FrameworkElement', 'FrameworkElement', ['Components.cs', 'FrameworkElement.cs']);
        await verify('Components.cs', 'class UserControl : Control', 'Control', ['Components.cs']);
        await verify('Components.cs', 'class Control : FrameworkElement', 'FrameworkElement', ['Components.cs', 'FrameworkElement.cs']);
        await verify('Elements.cs', 'ElementProperty<string> IdProperty', 'ElementProperty', ['FrameworkElement.cs']);
        await verify('Components.cs', 'DataContextProperty = new("DataContext", typeof(PropertyModel))', 'PropertyModel', ['PropertyModel.cs']);
    } catch (error) {
        result.errors.push(error.stack || String(error));
        throw error;
    } finally { save(); }
}
module.exports = { run };
