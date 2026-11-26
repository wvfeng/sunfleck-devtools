'use strict';
const vscode = require('vscode');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

async function until(read, accept, message, timeout = 15000) {
    const deadline = Date.now() + timeout;
    let result;
    while (Date.now() < deadline) {
        result = await read();
        if (accept(result)) {
            return result;
        }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error(message + ': ' + JSON.stringify(result));
}

async function run() {
    const root = path.resolve(__dirname, '../../..');
    const output = path.resolve(process.env.SUNFLECK_TEST_OUTPUT || path.join(root, 'out/devtools/ide-acceptance'));
    const folderTask = process.env.SUNFLECK_TEST_FOLDER_TASK === '1';
    const formatting = process.env.SUNFLECK_TEST_FORMATTING === '1';
    const unicode = process.env.SUNFLECK_TEST_UNICODE;
    const editing = process.env.SUNFLECK_TEST_EDITING;
    const result = { editor: vscode.version, passed: [], errors: [], workspaceTrusted: vscode.workspace.isTrusted };
    fs.mkdirSync(output, { recursive: true });
    const pipeHosts = [];
    const serve = async read => { const host = await require("../pipe-fixture").serve(read); pipeHosts.push(host); return { pipe: host.pipe }; };
    try {
        const extension = vscode.extensions.getExtension('sunfleck.sunfleck');
        assert.ok(extension, 'Installed VSIX was not discovered');
        result.extensionPath = extension.extensionPath;
        result.passed.push('installed-package');
        assert.ok(!extension.extensionPath.includes(path.join(root, 'editors/vscode')), 'Expected installed package, not development source');
        if (process.env.SUNFLECK_TEST_COMPONENTS === '1' || process.env.SUNFLECK_TEST_DEMO) {
            await require('./components')({ root, output, result, extension, until });
            return;
        }
        if (editing) {
            await require('./editing')({ fixturePath: editing, output, result, extension, until });
            return;
        }
        if (unicode) {
            await require('./unicode')({ fixturePath: unicode, output, result, extension, until });
            return;
        }
        if (process.env.SUNFLECK_TEST_DIAGNOSTICS === '1') {
            await extension.activate();
            const source = path.join(output, 'diagnostic-source.fleck');
            fs.writeFileSync(source, "<Window xmlns='urn:sunfleck:v1'>\n<Button Content='{Binding Caption}'/>\n</Window>");
            const uri = vscode.Uri.file(source);
            const document = await vscode.workspace.openTextDocument(uri);
            const value = document.getText().indexOf('{Binding Caption}');
            const at = document.positionAt(value);
            const diagnostic = { object: '1', model: '2', page: '3', property: 0, path: 'Caption',
                code: 'getter_failed', message: 'Getter rejected', occurrences: 3, active: true,
                source: { uri: uri.toString(), range: { line: 2, column: 1 } },
                propertySource: { uri: uri.toString(), range: { line: at.line + 1, column: at.character + 1 } } };
            const frame = { version: 1, timestamp: Date.now(), windows: [{ id: 0, title: 'Diagnostic test' }],
                nodes: [{ id: '1', parent: '0', window: 0, type: 'Button' }],
                bindingDiagnostics: { diagnostics: [diagnostic] } };
            const snapshot = await serve(() => frame);
            await vscode.commands.executeCommand('sunfleck.inspectSnapshot', snapshot);
            const actual = await vscode.commands.executeCommand('sunfleck.inspectBindingDiagnostic', diagnostic);
            assert.equal(actual.page, '3');
            assert.equal(actual.model, '2');
            assert.equal(actual.occurrences, 3);
            assert.equal(vscode.window.activeTextEditor.document.uri.toString(), uri.toString());
            assert.ok(vscode.window.activeTextEditor.selection.active.isEqual(at));
            result.passed.push('diagnostic-property-source-and-identity');
            diagnostic.active = false;
            frame.nodes = [];
            frame.timestamp += 1;

            await vscode.commands.executeCommand('sunfleck.inspectSnapshot', snapshot);
            assert.equal((await vscode.commands.executeCommand('sunfleck.inspectBindingDiagnostic', diagnostic)).active, false);
            assert.ok(vscode.window.activeTextEditor.selection.active.isEqual(at));
            result.passed.push('recovered-diagnostic-with-retired-object');
            delete diagnostic.propertySource;
            frame.timestamp += 1;

            await vscode.commands.executeCommand('sunfleck.inspectSnapshot', snapshot);
            await vscode.commands.executeCommand('sunfleck.inspectBindingDiagnostic', diagnostic);
            assert.equal(vscode.window.activeTextEditor.selection.active.line, 1);
            assert.equal(vscode.window.activeTextEditor.selection.active.character, 0);
            result.passed.push('legacy-diagnostic-source-fallback');
            return;
        }
        if (process.env.SUNFLECK_TEST_MULTI_ROOT === '1') {
            const folders = vscode.workspace.workspaceFolders;
            assert.equal(folders?.length, 2);
            await extension.activate();
            const documents = await Promise.all(folders.map(folder =>
                vscode.workspace.openTextDocument(vscode.Uri.joinPath(folder.uri, 'counter.fleck'))));
            for (const [index, document] of documents.entries()) {
                assert.equal(document.languageId, 'fleck');
                const expected = index === 0 ? 'Counter.Value' : 'Counter.Other';
                const position = document.positionAt(document.getText().indexOf('Counter.') + 'Counter.'.length);
                await until(async () => {
                    const list = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, position);
                    return list?.items.map(item => typeof item.label === 'string' ? item.label : item.label.label);
                }, items => items?.includes(expected) && !items.includes(index === 0 ? 'Counter.Other' : 'Counter.Value'),
                `Incorrect completions for ${folders[index].name}`, 30000);
                const diagnostics = vscode.languages.getDiagnostics(document.uri);
                assert.ok(!diagnostics.some(item => item.code === 'model_uncertain' || item.code === 'path_missing'));
                result.passed.push(`scoped-model-${folders[index].name}`);
            }
            result.extensionPath = extension.extensionPath;
            return;
        }
        if (folderTask) {
            assert.equal(vscode.workspace.workspaceFolders?.length, 1);
            await extension.activate();
            const targets = process.env.SUNFLECK_TEST_REBUILD_TASKS;
            for (const taskName of targets ? targets.split('|') : ['Sunfleck: Acceptance']) {
                await verifyDevelopmentTask(result, taskName);
            }
            result.extensionPath = extension.extensionPath;
            return;
        }
        const file = path.join(output, formatting ? 'vscode-formatting.fleck' : 'vscode-acceptance.fleck');
        const source = "<Window xmlns='urn:sunfleck:v1' Model='CCounter'>\n<StackPanel>\n<TextBlock Text='中文🌱'/>\n<TextBlock Text='{Binding Counter.Value}'/>\n<TextBlock Text='{Binding Missing}'/>\n</StackPanel>\n</Window>";
        fs.writeFileSync(file, source);
        const paletteName = 'acceptance-palette.fleck';
        const paletteFile = path.join(output,paletteName);
        fs.writeFileSync(paletteFile, "<ResourceDictionary xmlns='urn:sunfleck:v1'>\n  <Resource Key='PaletteAccent' Value='#223344'/>\n</ResourceDictionary>");
        const document = await vscode.workspace.openTextDocument(file);
        const editor = await vscode.window.showTextDocument(document);
        const replace = async value => {
            const edit = new vscode.WorkspaceEdit();
            edit.replace(document.uri, new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)), value);
            assert.ok(await vscode.workspace.applyEdit(edit), 'Could not edit the Fleck test document');
        };
        const focusDocument = async (action, selection) => {
            const current = await vscode.window.showTextDocument(document, { preserveFocus: false });
            await vscode.commands.executeCommand('workbench.action.focusActiveEditorGroup');
            await until(() => vscode.window.activeTextEditor?.document.uri.toString(), value => value === document.uri.toString(),
                'Expected Fleck editor before ' + action);
            if (selection) { current.selection = selection; }
            fs.appendFileSync(path.join(output, 'editor-context.jsonl'), JSON.stringify({ action,
                uri: vscode.window.activeTextEditor.document.uri.toString(), version: document.version, time: Date.now() }) + '\n');
        };
        assert.equal(document.languageId, 'fleck', 'Fleck file association failed');
        await extension.activate();
        assert.ok(extension.isActive, 'Extension did not activate');
        result.passed.push('file-association', 'activation');
        if (process.env.SUNFLECK_TEST_MODEL_SWITCH === '1') {
            const baseModel = JSON.parse(fs.readFileSync(vscode.workspace.getConfiguration('sunfleck').get('modelFiles')[0], 'utf8'));
            const first = path.join(output, 'model-switch-debug.fleckmodel.json');
            const second = path.join(output, 'model-switch-release.fleckmodel.json');
            const config = vscode.workspace.getConfiguration('sunfleck', document.uri);
            const select = files => config.update('modelFiles', files, vscode.ConfigurationTarget.Global);
            const diagnostics = () => vscode.languages.getDiagnostics(document.uri);
            try {
                fs.writeFileSync(first, JSON.stringify(baseModel));
                baseModel.models[0].properties[0].path = 'Counter.Other';
                fs.writeFileSync(second, JSON.stringify(baseModel));
                await replace("<Window xmlns='urn:sunfleck:v1' Model='CCounter'><TextBlock Text='{Binding Counter.Value}'/></Window>");
                await select([first, second]);
                await until(diagnostics, items => items.some(item => item.code === 'model_uncertain'), 'No duplicate model diagnostic');
                await select([second]);
                await until(diagnostics, items => items.some(item => item.code === 'path_missing') &&
                    !items.some(item => item.code === 'model_uncertain'), 'Second configuration did not take effect');
                await select([first]);
                await until(diagnostics, items => items.length === 0, 'First configuration did not take effect');
                result.passed.push('model-conflict-and-configuration-switch');
            } finally {
                await select(undefined);
                fs.rmSync(first, { force: true });
                fs.rmSync(second, { force: true });
            }
            return;
        }
        if (formatting) {
            const scope = { uri: document.uri, languageId: document.languageId };
            const config = vscode.workspace.getConfiguration('editor', scope);
            const set = (name, value) => config.update(name, value, vscode.ConfigurationTarget.Global, true);
            await set('defaultFormatter', 'sunfleck.sunfleck');
            await set('formatOnSave', false);
            editor.options = { tabSize: 2, insertSpaces: true };
            const compact = "<Window xmlns='urn:sunfleck:v1'><TextBlock Text='中文🌱'/></Window>";
            const expected = compact.replace('><TextBlock', '>\n  <TextBlock').replace('/></Window>', '/>\n</Window>');
            await replace(compact);
            await until(() => vscode.commands.executeCommand('vscode.executeFormatDocumentProvider', document.uri, { tabSize: 2, insertSpaces: true }),
                edits => edits?.length === 2, 'Formatting provider did not return both whitespace edits');
            await vscode.commands.executeCommand('editor.action.formatDocument');
            assert.equal(document.getText(), expected);
            result.passed.push('format-document-command');
            await replace(compact);
            assert.ok(await document.save());
            assert.equal(fs.readFileSync(file, 'utf8'), compact);
            result.passed.push('format-on-save-disabled');
            await set('formatOnSave', undefined);
            assert.equal(vscode.workspace.getConfiguration('editor', scope).get('formatOnSave'), true, 'Fleck defaults to formatting on save');
            await replace(compact.replace('中文', '保存'));
            assert.ok(await document.save());
            assert.equal(fs.readFileSync(file, 'utf8'), expected.replace('中文', '保存'));
            result.passed.push('format-on-save-enabled');
            const layout = '<Window xmlns="urn:sunfleck:v1"><TextBlock Width="Auto" Height="24" Margin="4,8" HorizontalAlignment="Center"/></Window>';
            await replace(layout);
            const autoAt = document.positionAt(layout.indexOf('Auto') + 1);
            const completions = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, autoAt);
            assert.ok(completions.items.some(item => item.label === 'Auto'), 'Width Auto completion');
            for (const name of ['Width', 'Height', 'Margin', 'HorizontalAlignment']) {
                const definitions = await vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, document.positionAt(layout.indexOf(name + '=') + 1));
                assert.ok(definitions.length > 0, 'Missing layout property definition: ' + name);
            }
            await replace(layout.replace('Margin="4,8"', 'Margin="1,2,3"').replace('Center', 'Bottom'));
            await until(() => vscode.languages.getDiagnostics(document.uri),
                values => values.filter(d => d.code === 'invalid_property_value').length >= 2, 'Missing local layout diagnostics');
            result.passed.push('layout-completion-definition-and-diagnostics');
            const template = '<ControlTemplate xmlns="urn:sunfleck:v1"\n                 Class="Demo.Compact" Namespace="urn:demo">\n  <Card Padding="12">\n     <StackPanel Spacing="4">\n    <TextBlock Text="Value"/>\n  </StackPanel>\n  </Card>\n</ControlTemplate>';
            const expectedTemplate = template.replace('     <StackPanel', '    <StackPanel').replace('    <TextBlock', '      <TextBlock').replace('  </StackPanel>', '    </StackPanel>');
            await replace(template);
            assert.ok(await document.save());
            assert.equal(document.getText(), expectedTemplate);
            assert.ok(await document.save());
            assert.equal(document.getText(), expectedTemplate, 'Formatting must be idempotent');
            result.passed.push('template-save-indentation-and-idempotence');
            await set('formatOnType', true);
            for (const name of ['StackPanel', 'controls:UserCard']) {
                await replace('<' + name);
                const at = document.positionAt(document.getText().length);
                await focusDocument('close-tag', new vscode.Selection(at, at));
                await vscode.commands.executeCommand('type', { text: '>' });
                await until(() => document.getText(), text => text === `<${name}></${name}>`, 'Tag did not close');
                await until(() => document.offsetAt(vscode.window.activeTextEditor.selection.active), offset => offset === name.length + 2, 'Caret must remain inside tag');
                await vscode.commands.executeCommand('type', { text: 'inside' });
                assert.equal(document.getText(), `<${name}>inside</${name}>`);
            }
            result.passed.push('typed-close-tag-caret-between-tags');
            result.extensionPath = extension.extensionPath;
            return;
        }
        const diagnostics = await until(() => vscode.languages.getDiagnostics(document.uri),
            items => items.some(item => item.code === 'path_missing'), 'Binding diagnostic not published');
        assert.equal(diagnostics.find(item => item.code === 'path_missing').range.start.line, 4);
        result.passed.push('binding-diagnostics');
        const at = document.positionAt(source.indexOf('Counter.Value') + 3);
        const definitions = await until(() => vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, at),
            items => items?.length > 0, 'Definition provider returned no location');
        const definition = definitions[0].targetUri || definitions[0].uri;
        assert.ok(definition.fsPath.endsWith('model.h'));
        result.passed.push('model-definition');
        const hovers = await vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri, at);
        assert.ok(hovers.length > 0);
        result.passed.push('hover');
        const symbols = await vscode.commands.executeCommand('vscode.executeDocumentSymbolProvider', document.uri);
        assert.ok(symbols.length > 0);
        result.passed.push('document-symbols');
        const appearanceXml = "<Window xmlns='urn:sunfleck:v1'><Border BorderThickness='1.5,2' CornerRadius='8'><TextBlock FontFamily='Segoe UI' FontSize='18.5' FontWeight='Bold' Foreground='#223344'><Span FontStyle='Italic'><Run Text='Unicode 😀'/></Span></TextBlock></Border></Window>";
        await replace(appearanceXml);
        await until(() => vscode.languages.getDiagnostics(document.uri), items => items.length === 0,
            'Built-in Border and inline font attributes must be valid');
        for (const name of ['Border', 'BorderThickness', 'FontSize', 'Run']) {
            await until(() => vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri,
                document.positionAt(appearanceXml.indexOf(name) + 1)),
                items => items?.some(item => (item.targetUri || item.uri).fsPath.endsWith('Elements.cs')),
                `${name} must navigate to its SDK declaration`);
        }
        result.passed.push('border-font-inlines-diagnostics-and-definition');
        const palettePage = "<Window xmlns='urn:sunfleck:v1'><Window.Resources><ResourceDictionary Source='" + paletteName +
            "'/></Window.Resources><Border Background='{StaticResource PaletteAccent}'/></Window>";
        await replace(palettePage);
        const paletteAt = document.positionAt(palettePage.indexOf('PaletteAccent') + 4);
        await until(() => vscode.commands.executeCommand('vscode.executeDefinitionProvider',document.uri,paletteAt),
            items => items?.some(item => path.relative((item.targetUri || item.uri).fsPath,paletteFile) === '' &&
                (item.targetSelectionRange || item.range).start.line === 1), 'External resource key definition missing');
        result.passed.push('external-dictionary-key-definition');
        const statePrefix = "<Window xmlns='urn:sunfleck:v1'><Button><Button.VisualStateGroups><VisualStateGroup Name='CommonStates'><Vis";
        await replace(statePrefix);
        await until(() => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, document.positionAt(statePrefix.length)),
            items => items?.items.some(item => (item.label.label ?? item.label) === 'VisualState'), 'VisualState completion missing');
        result.passed.push('visual-state-completion');
        const invalidPrefix = "<Window xmlns='urn:sunfleck:v1'><TextBox><TextBox.VisualStateGroups><VisualStateGroup Name='ValidationStates'><VisualState Name='Invalid";
        await replace(invalidPrefix);
        await until(() => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, document.positionAt(invalidPrefix.length)),
            items => ['InvalidFocused','InvalidUnfocused'].every(name => items?.items.some(item => (item.label.label ?? item.label) === name)),
            'Validation state names missing');
        result.passed.push('validation-state-name-completion');
        const pointerPrefix = "<Window xmlns='urn:sunfleck:v1'><Button><Button.InputBindings><Poi";
        await replace(pointerPrefix);
        await until(() => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, document.positionAt(pointerPrefix.length)),
            items => items?.items.some(item => (item.label.label ?? item.label) === 'PointerBinding'), 'PointerBinding completion missing');
        result.passed.push('pointer-binding-completion');
        const linkPrefix = "<Window xmlns='urn:sunfleck:v1'><Hyperlink Temp";
        await replace(linkPrefix);
        await until(() => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, document.positionAt(linkPrefix.length)),
            items => items?.items.some(item => (item.label.label ?? item.label) === 'Template'), 'Hyperlink Template completion missing');
        result.passed.push('hyperlink-template-completion');
        const gesturePrefix = "<Window xmlns='urn:sunfleck:v1'><Button><Button.InputBindings><KeyBinding Gesture='Shift+F1";
        await replace(gesturePrefix);
        await until(() => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, document.positionAt(gesturePrefix.length)),
            items => items?.items.some(item => (item.label.label ?? item.label) === 'F12'), 'Function key gesture completion missing');
        result.passed.push('function-key-completion');
        const phasePrefix = "<Window xmlns='urn:sunfleck:v1'><Button><Button.InputBindings><KeyBinding Gesture='F8' Phase='Pr";
        await replace(phasePrefix);
        await until(() => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, document.positionAt(phasePrefix.length)),
            items => items?.items.some(item => (item.label.label ?? item.label) === 'Preview'), 'Key route phase completion missing');
        result.passed.push('key-phase-completion');
        const triggerXml = "<Window xmlns='urn:sunfleck:v1'><Window.Resources><Style TargetType='Button'><Trigger Property='Visibility' Value='Hidden'><Setter Property='Opacity' Value='0.5'/></Trigger></Style></Window.Resources><Button/></Window>";
        await replace(triggerXml);
        const triggerAt = document.positionAt(triggerXml.indexOf('Visibility') + 3);
        await until(() => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri,triggerAt),
            items => items?.items.some(item => (item.label.label ?? item.label) === 'Visibility'), 'Visibility trigger completion missing');
        await until(() => vscode.commands.executeCommand('vscode.executeDefinitionProvider',document.uri,triggerAt),
            items => items?.some(item => (item.targetUri || item.uri).fsPath.endsWith('Elements.cs')), 'Visibility trigger definition missing');
        result.passed.push('trigger-property-completion-and-definition');
        const rangeTemplate = "<ControlTemplate xmlns='urn:sunfleck:v1' Class='RangeFace' Namespace='urn:test' TargetType='Slider'><TextBlock Text='{TemplateBinding Value}'/></ControlTemplate>";
        await replace(rangeTemplate);
        const valueOffset = rangeTemplate.indexOf('Value}');
        await until(() => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, document.positionAt(valueOffset + 2)),
            items => items?.items.some(item => (item.label.label ?? item.label) === 'Value'), 'Slider TemplateBinding Value completion missing');
        await until(() => vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, document.positionAt(valueOffset + 2)),
            items => items?.some(item => (item.targetUri || item.uri).fsPath.endsWith('Elements.cs')), 'Slider TemplateBinding Value definition missing');
        result.passed.push('range-template-binding-completion-and-definition');
        const animationPrefix = "<Window xmlns='urn:sunfleck:v1'><Border><Border.Storyboard><Storyboard><Timeline Property='Opacity' DurationMs='200'><Key";
        await replace(animationPrefix);
        await until(() => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, document.positionAt(animationPrefix.length)),
            items => items?.items.some(item => (item.label.label ?? item.label) === 'KeyFrame'), 'Timeline key frame completion missing');
        const drawingPrefix = "<Window xmlns='urn:sunfleck:v1'><Rectangle><Rectangle.Fill><LinearGradientBrush><Grad";
        await replace(drawingPrefix);
        await until(() => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, document.positionAt(drawingPrefix.length)),
            items => items?.items.some(item => (item.label.label ?? item.label) === 'GradientStop'), 'GradientStop completion missing');
        const drawingXml = "<Window xmlns='urn:sunfleck:v1'><Path Data='M0,0 L10,0 L0,10 Z'><Path.RenderTransform><RotateTransform Angle='30'/></Path.RenderTransform></Path></Window>";
        await replace(drawingXml);
        await until(() => vscode.languages.getDiagnostics(document.uri), items => items.length === 0, 'Valid drawing object diagnostics');
        await until(() => vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri,
            document.positionAt(drawingXml.indexOf('Path Data') + 1)),
            items => items?.some(item => (item.targetUri || item.uri).fsPath.endsWith('Elements.cs')), 'Path SDK definition missing');
        await replace(drawingXml.replace("Angle='30'", "Angle='invalid'"));
        await until(() => vscode.languages.getDiagnostics(document.uri), items => items.some(d => d.severity === vscode.DiagnosticSeverity.Error),
            'Invalid rotation requires a diagnostic');
        result.passed.push('drawing-completion-definition-and-diagnostics');
        const unknownTagXml = "<UserControl xmlns='urn:sunfleck:v1' Class='Example.UserCard' Namespace='urn:example'>\r\n  <Card><!-- 中文😀 --><MissingControl><StackPanel/></MissingControl></Card>\r\n</UserControl>";
        await replace(unknownTagXml);
        const unknownTagErrors = await until(() => vscode.languages.getDiagnostics(document.uri),
            items => items.some(item => item.code === 'unknown_element' && document.getText(item.range) === 'MissingControl'), 'Unknown tag range is incorrect');
        assert.ok(!unknownTagErrors.some(item => String(item.code).startsWith('fleck_') && item.range.start.line === 0), 'Unknown tag left a root squiggle');
        result.passed.push('unknown-tag-exact-name-range');
        const partial = "<Window xmlns='urn:sunfleck:v1' Model='CCounter'><TextBlock Text='{Binding Counter.";
        await replace(partial);
        const completions = await until(() => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, document.positionAt(partial.length)),
            items => items?.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label) === 'Counter.Value'), 'Binding completion failed');
        assert.ok(completions.items.length > 0);
        result.passed.push('binding-completion', 'incomplete-syntax');
        for (const sample of [
            { model: 'CppCounter', binding: 'Counter.Value', prefix: 'Counter.', declaration: 'model.hpp' },
            { model: 'CounterModel', binding: 'Count', prefix: 'Co', declaration: 'Program.cs' }
        ]) {
            const text = `<Window xmlns='urn:sunfleck:v1' Model='${sample.model}'><TextBlock Text='{Binding ${sample.binding}}'/></Window>`;
            await replace(text);
            const locations = await until(() => vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, document.positionAt(text.lastIndexOf(sample.binding) + 1)),
                items => items?.some(item => (item.targetUri || item.uri).fsPath.endsWith(sample.declaration)), `${sample.model} definition failed`);
            assert.ok(locations.length > 0);
            const invalid = text.replace(`{Binding ${sample.binding}}`, '{Binding Missing}');
            await replace(invalid);
            await until(() => vscode.languages.getDiagnostics(document.uri),
                items => items.some(item => item.code === 'path_missing' &&
                    document.getText(item.range).includes('Missing')), `${sample.model} binding diagnostic failed`);
            const unfinished = `<Window xmlns='urn:sunfleck:v1' Model='${sample.model}'><TextBlock Text='{Binding ${sample.prefix}`;
            await replace(unfinished);
            await until(() => vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, document.positionAt(unfinished.length)),
                items => items?.items.some(item => (typeof item.label === 'string' ? item.label : item.label.label) === sample.binding), `${sample.model} completion failed`);
            result.passed.push(`${sample.model}-definition`, `${sample.model}-completion`, `${sample.model}-diagnostics`);
        }
        const theme = "<Theme xmlns='urn:sunfleck:v1'";
        await replace(theme);
        const end = document.positionAt(theme.length);
        await focusDocument('theme-type', new vscode.Selection(end, end));
        await vscode.commands.executeCommand('type', { text: '>' });
        await until(() => document.getText(), value => value === theme + '></Theme>', 'Typing > did not close the Theme element');
        result.passed.push('theme-type-auto-close');
        const config = vscode.workspace.getConfiguration('sunfleck');
        const configuredFiles = config.get('modelFiles');
        const externalModel = path.join(output, 'watch-test.fleckmodel.json');
        const writeModel = property => fs.writeFileSync(externalModel, JSON.stringify({
            version: 1,
            models: [{ name: 'WatchedModel', properties: [{ path: property, type: 'string', writable: true }] }]
        }));
        try {
            writeModel('First');
            await config.update('modelFiles', [externalModel], vscode.ConfigurationTarget.Global);
            const binding = "<Window xmlns='urn:sunfleck:v1' Model='WatchedModel'><TextBlock Text='{Binding First}'/></Window>";
            await replace(binding);
            const at = document.positionAt(binding.indexOf('First') + 1);
            await until(() => vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri, at),
                items => items?.some(item => item.contents.some(content => content.value.includes('First'))),
                'Explicit model file was not loaded');
            writeModel('Second');
            await until(() => vscode.languages.getDiagnostics(document.uri),
                items => items.some(item => item.code === 'path_missing' && item.message.includes('WatchedModel')),
                'Changing an explicit model file outside a workspace did not refresh diagnostics');
            result.passed.push('external-model-file-refresh');
        } finally {
            await config.update('modelFiles', configuredFiles, vscode.ConfigurationTarget.Global);
        }
        const fixture = {
            version: 1,
            timestamp: Date.now(),
            fleckRefresh: { sequence: '1', result: 0, elapsedMicroseconds: 1250 },
            windows: [{ id: 0, title: 'Inspector test', width: 360, height: 180 }],
            nodes: [{ id: '1', parent: '0', window: 0, type: 'TextBlock', source: {
                uri: document.uri.toString(), range: { line: 1, column: 1, endLine: 1, endColumn: 2, length: 1 }
            } }]
        };
        const inspectorTarget = await serve(() => fixture);
        await vscode.commands.executeCommand('sunfleck.inspectSnapshot', inspectorTarget);
        await vscode.commands.executeCommand('sunfleck.inspectObject', '1', true);
        assert.equal(vscode.window.activeTextEditor.document.uri.toString(), document.uri.toString());
        assert.equal(vscode.window.activeTextEditor.selection.active.line, 0);
        result.passed.push('snapshot-inspector-source-navigation');
        await vscode.commands.executeCommand('sunfleck.properties.focus');
        await vscode.commands.executeCommand('sunfleck.objects.focus');
        result.passed.push('independent-properties-and-object-views');
        await focusDocument('locate-first');
        assert.equal(await vscode.commands.executeCommand('sunfleck.locateRuntimeObject'), '1');
        result.passed.push('snapshot-inspector-source-lookup');
        fixture.timestamp = Date.now();
        fixture.nodes.push({ ...fixture.nodes[0], id: '2', window: 1 });
        fixture.windows.push({ id: 1, title: 'Floating inspector test', width: 360, height: 180 });
        await vscode.commands.executeCommand('sunfleck.inspectSnapshot', inspectorTarget);
        await focusDocument('locate-window-1', new vscode.Selection(0, 0, 0, 0));
        assert.equal(await vscode.commands.executeCommand('sunfleck.locateRuntimeObject', '2'), '2');
        await focusDocument('locate-window-0', new vscode.Selection(0, 0, 0, 0));
        assert.equal(await vscode.commands.executeCommand('sunfleck.locateRuntimeObject', '1'), '1');
        await focusDocument('locate-missing', new vscode.Selection(0, 0, 0, 0));
        assert.equal(await vscode.commands.executeCommand('sunfleck.locateRuntimeObject', '3'), undefined);
        result.passed.push('snapshot-inspector-multiple-source-objects');
        const language = vscode.workspace.getConfiguration('sunfleck');
        assert.equal(language.inspect('language').defaultValue, 'zh-CN');
        const savedLanguage = language.inspect('language').globalValue;
        try {
            await language.update('language', 'en', vscode.ConfigurationTarget.Global);
            await replace(source);
            await until(() => vscode.languages.getDiagnostics(document.uri),
                items => items.some(item => item.code === 'path_missing' && item.message.includes('does not export path')),
                'Fleck diagnostics did not switch to English');
            await vscode.commands.executeCommand('sunfleck.inspectSnapshotEnglish', inspectorTarget);
            await vscode.commands.executeCommand('sunfleck.inspectObject', '1', true);
            assert.equal(vscode.window.activeTextEditor.document.uri.toString(), document.uri.toString());
            result.passed.push('development-language-default-and-english');
        } finally {
            await language.update('language', savedLanguage, vscode.ConfigurationTarget.Global);
        }
        if (process.env.SUNFLECK_TEST_DEVELOPMENT_TASK === '1') {
            await verifyDevelopmentTask(result);
        }
        await replace(source);
        await document.save();
        result.extensionPath = extension.extensionPath;
    } catch (error) {
        result.errors.push(error.stack || String(error));
        throw error;
    } finally {
        await vscode.commands.executeCommand('sunfleck.closeSnapshot');
        for (const host of pipeHosts) { await host.close(); }
        const resultFile = process.env.SUNFLECK_TEST_COMPONENTS === '1' || process.env.SUNFLECK_TEST_DEMO ? 'vscode-components-result.json' : editing ? 'vscode-editing-result.json' : unicode ? 'vscode-unicode-result.json' :
            process.env.SUNFLECK_TEST_DIAGNOSTICS === '1' ? 'vscode-diagnostics-result.json' :
            process.env.SUNFLECK_TEST_MULTI_ROOT === '1' ? 'vscode-multi-root-result.json' :
            process.env.SUNFLECK_TEST_MODEL_SWITCH === '1' ? 'vscode-model-switch-result.json' :
            formatting ? 'vscode-formatting-result.json' : folderTask ? 'vscode-folder-task-result.json' : 'vscode-result.json';
        fs.writeFileSync(path.join(output, resultFile), JSON.stringify(result, null, 2));
    }
}
async function verifyDevelopmentTask(result, taskName = 'Sunfleck: Acceptance') {
    assert.ok(vscode.workspace.isTrusted, 'Task fixture must be trusted');
    const tasks = await vscode.tasks.fetchTasks();
    assert.ok(tasks.some(task => task.name === taskName), 'Configured task was not discovered');
    let exitCode;
    const listener = vscode.tasks.onDidEndTaskProcess(event => {
        if (event.execution.task.name === taskName) {
            exitCode = event.exitCode;
        }
    });
    try {
        const exitFile = taskName === 'Sunfleck: Rebuild C' ? 'rebuild-c.exit' :
            taskName === 'Sunfleck: Rebuild C#' ? 'rebuild-csharp.exit' : undefined;
        const evidence = exitFile && path.resolve(process.env.SUNFLECK_TEST_OUTPUT ||
            path.resolve(__dirname, '../../../out/devtools/ide-acceptance'), exitFile);
        const started = Date.now();
        const execution = await vscode.commands.executeCommand('sunfleck.runDevelopmentTask', taskName);
        assert.equal(execution?.task.name, taskName);
        exitCode = await until(() => {
            if (exitCode !== undefined) { return exitCode; }
            if (evidence && fs.existsSync(evidence) && fs.statSync(evidence).mtimeMs >= started) {
                const value = fs.readFileSync(evidence, 'utf8').trim();
                if (/^\d+$/.test(value)) { return Number(value); }
            }
        }, value => value !== undefined, `Development task did not exit (${taskName})`, 90000);
        assert.equal(exitCode, 0);
        result.passed.push(taskName === 'Sunfleck: Acceptance' ? 'development-task-launch' : taskName);
    } finally {
        listener.dispose();
    }
}
module.exports = { run };
