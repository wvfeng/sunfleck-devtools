'use strict';
const vscode = require('vscode');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

module.exports = async function components({ root, output, result, extension, until }) {
    await extension.activate();
    result.definitions = [];
    async function verify(file, needle, expectedFile, expectedLine) {
        const document = await vscode.workspace.openTextDocument(file);
        const editor = await vscode.window.showTextDocument(document);
        assert.equal(document.languageId, 'fleck');
        assert.equal(vscode.workspace.getConfiguration('sunfleck', document.uri).get('serverPath', ''),
            process.env.SUNFLECK_TEST_DEMO_ROOT ? '${workspaceFolder}/.tools/fleck/Sunfleck.Development.dll' : '');
        const position = document.positionAt(document.getText().indexOf(needle) + 1);
        assert.ok(document.getText().includes(needle), needle);
        const definitions = await until(() => vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, position),
            values => values?.length > 0, `${needle} has no source definition`);
        const location = definitions[0];
        const uri = location.targetUri || location.uri;
        const range = location.targetSelectionRange || location.range;
        assert.equal(path.basename(uri.fsPath), expectedFile);
        assert.ok(fs.readFileSync(uri.fsPath, 'utf8').split(/\r?\n/)[range.start.line].includes(expectedLine), 'Definition points to the declaration');
        result.definitions.push({ source: document.uri.toString(), needle, uri: uri.toString(), line: range.start.line + 1 });
        editor.selection = new vscode.Selection(position, position);
        await vscode.commands.executeCommand('editor.action.revealDefinition');
        await until(() => vscode.window.activeTextEditor,
            current => current?.document.uri.toString() === uri.toString() && current.selection.active.line === range.start.line,
            `${needle}: actual F12 did not open the source`);
        result.passed.push(`${needle}-definition-provider-and-F12`);
    }
    const demo = process.env.SUNFLECK_TEST_DEMO;
    if (demo) {
        const project = path.join(process.env.SUNFLECK_TEST_DEMO_ROOT || path.join(root, 'demos'), demo);
        const page = path.join(project, 'main.fleck');
        const extension = demo === 'c' ? 'h' : demo === 'cpp' ? 'hpp' : 'cs';
        await verify(page, 'Value=', 'Rating.' + extension, demo === 'csharp' ? 'public int Value' : 'ValueProperty');
        await verify(page, 'controls:Rating', 'Rating.' + extension, demo === 'c' ? 'struct DemoC_Rating' : 'class Rating');
        await verify(page, 'controls:UserCard', 'UserCard.' + extension, demo === 'c' ? 'struct DemoC_UserCard' : 'class UserCard');
        await verify(page, 'Score,', 'MainModel.' + extension, demo === 'c' ? 'int score' : 'Score');
        const builtIn = demo === 'csharp' ? 'Elements.cs' : 'elements.' + extension;
        await verify(page, 'StackPanel', builtIn, demo === 'c' ? 'struct ui_StackPanel' : 'class StackPanel');
        await verify(page, 'Padding=', builtIn, 'PaddingProperty');
        await verify(page, 'Window', builtIn, demo === 'c' ? 'struct ui_DocumentWindow' : 'class DocumentWindow');
        await verify(path.join(project, 'controls', 'UserCard.fleck'), 'Id=', builtIn, 'IdProperty');
        await verify(page, 'DataContext=', demo === 'csharp' ? 'Components.cs' : demo === 'cpp' ? 'components.hpp' : 'component_attributes.h', 'DataContextProperty');
        await verify(page, 'Id=', builtIn, 'IdProperty');
        const manifest = JSON.parse(fs.readFileSync(path.join(project, 'sdk', 'fleck.types.json'), 'utf8'));
        for (const [type, property, parent] of [['StackPanel', 'Padding', 'LayoutElement'], ['TextBlock', 'Id', 'VisualElement']]) {
            const entry = manifest.types.find(t => t.group === '' && t.name === type).properties.find(p => p.name === property);
            assert.ok(entry.declaringType.endsWith(parent));
            assert.equal(result.definitions.find(d => d.needle === property + '=').line, entry.line, 'F12 opens inherited property declaration');
        }
        assert.ok(result.definitions.every(d => vscode.Uri.parse(d.uri).fsPath.toLowerCase().startsWith(project.toLowerCase() + path.sep)), 'All definitions, including built-in types, stay inside the independent project');
        return;
    }
    // The exact multi-language sample reported by the user, using CMake exports.
    const sample = path.join(root, 'tests/fixtures/reusable/main.fleck');
    await verify(sample, 'Value=', 'controls.h', 'example_rating_value');
    await verify(sample, 'controls:Rating', 'controls.h', 'class Rating');
    await verify(sample, 'controls:UserCard', 'controls.h', 'class UserCard');
    await verify(sample, 'StackPanel', 'Elements.cs', 'class StackPanel');
    await verify(sample, 'Padding=', 'Elements.cs', 'PaddingProperty');
    // A managed project before its first build, with encoded path characters.
    const project = path.join(output, '中文 🌱 %20 component project');
    fs.mkdirSync(project, { recursive: true });
    fs.writeFileSync(path.join(project, 'Controls.csproj'), '<Project Sdk="Microsoft.NET.Sdk"/>');
    fs.writeFileSync(path.join(project, 'Card.cs'), 'using Sunfleck; namespace Example; public class Card : UserControl {\n[FleckProperty] public string Caption { get => Get<string>("Caption"); set => Set("Caption", value); }\n}');
    fs.writeFileSync(path.join(project, 'Card.fleck'), '<UserControl xmlns="urn:sunfleck:v1" Class="Example.Card" Namespace="urn:test"><TextBlock/></UserControl>');
    const page = path.join(project, 'Main.fleck');
    fs.writeFileSync(page, '<Window xmlns="urn:sunfleck:v1" xmlns:c="urn:test"><c:Card Caption="test"/></Window>');
    await verify(page, 'Caption=', 'Card.cs', 'public string Caption');
    assert.ok(result.definitions.at(-1).source.includes('%2520'), 'VS Code encodes the literal percent only once');
};
