'use strict';
// Exercise the standalone demo's actual --watch entry, on a disposable project copy.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { spawn, spawnSync } = require('node:child_process');
const { randomUUID } = require('node:crypto');
const { snapshot } = require('./pipe-fixture');
async function run() {
    const source = path.resolve(process.argv[2]);
    const output = path.resolve('out/devtools/template-watch-' + Date.now());
    const project = path.join(output, 'project'); fs.mkdirSync(project, { recursive: true });
    for (const name of fs.readdirSync(source)) {
        if (/\.(cs|csproj|fleck)$/.test(name)) fs.copyFileSync(path.join(source, name), path.join(project, name));
    }
    const toolVersion = JSON.parse(fs.readFileSync(path.join(source, '.vscode/settings.json'), 'utf8'))['sunfleck.serverPath'].split('/').slice(0, -1).join('/').replace('${workspaceFolder}/', '');
    for (const name of ['controls', 'sdk/bin', 'sdk/lib', 'sdk/share', toolVersion]) {
        fs.cpSync(path.join(source, name), path.join(project, name), { recursive: true });
    }
    const program = path.join(project, 'Program.cs');
    fs.writeFileSync(program, fs.readFileSync(program, 'utf8').replace('model.Score = 70;',
        'model.Score = 73; app.Post(() => app.FindComponent("primary").FindNode("notes").Text = "Preserved notes");'));
    const build = spawnSync('dotnet', ['build', 'FleckDemoCSharp.csproj', '--nologo'], { cwd: project, encoding: 'utf8', windowsHide: true });
    fs.writeFileSync(path.join(output, 'build.log'), build.stdout + build.stderr);
    assert.equal(build.status, 0, 'Fixture build failed');
    const pipe = 'template-watch-' + randomUUID();
    const child = spawn('dotnet', ['bin/Debug/net10.0/FleckDemoCSharp.dll', '--watch'], {
        cwd: project, env: { ...process.env, SUNFLECK_DEBUG_PIPE: pipe }, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
    });
    let log = ''; child.stdout.on('data', d => log += d); child.stderr.on('data', d => log += d);
    const result = { passed: [], errors: [], output };
    let last;
    async function until(check, label) {
        const deadline = Date.now() + 15000;
        while (Date.now() < deadline) {
            assert.equal(child.exitCode, null, 'Application exited: ' + log);
            try { last = await snapshot(pipe); } catch { await new Promise(r => setTimeout(r, 100)); continue; }
            if (check(last)) return last;
            await new Promise(r => setTimeout(r, 100));
        }
        throw new Error(label);
    }
    try {
        await until(s => s.nodes.some(n => n.text === 'Preserved notes'), 'Initial state unavailable');
        const pid = last.processId, session = last.debugSession;
        const file = path.join(project, 'controls/CompactRatingTemplate.fleck');
        const original = fs.readFileSync(file, 'utf8');
        if (process.argv.includes('--dock')) {
            const template = (height, attributes = '') => original.replace(
                /(<ControlTemplate\b[^>]*>)[\s\S]*<\/ControlTemplate>/,
                `$1\n  <DockPanel Id="dockRoot" Spacing="0" Padding="0" ${attributes}>
    <TextBlock Dock="Left" Id="display" Text="{TemplateBinding Value}" Height="${height}"/>
    <Slider Dock="Right" Id="PART_Value" Height="${height}" Value="{Binding Value, RelativeSource=TemplatedParent, Mode=TwoWay}"/>
  </DockPanel>\n</ControlTemplate>`);
            const heightOf = n => n.bounds[3] - n.bounds[1];
            async function change(height, attributes, expected) {
                const sequence = last.fleckRefresh?.sequence;
                fs.writeFileSync(file, template(height, attributes));
                await until(s => s.fleckRefresh?.sequence !== sequence && s.fleckRefresh?.result === 0 &&
                    s.nodes.some(n => n.fleckId === 'dockRoot' && heightOf(n) === expected),
                'DockPanel template did not resize to ' + expected);
                assert.equal(heightOf(last.nodes.find(n => n.fleckId === 'rating')), expected, 'Component host retains excess height');
                assert.equal(last.processId, pid); assert.equal(last.debugSession, session);
                assert.ok(last.nodes.some(n => n.text === 'Preserved notes'));
                assert.ok(last.nodes.some(n => n.fleckId === 'display' && n.text === '73'));
            }
            await change(30, '', 30);
            const widthOf = n => n.bounds[2] - n.bounds[0];
            const intrinsic = widthOf(last.nodes.find(n => n.fleckId === 'display'));
            assert.ok(intrinsic > 0 && intrinsic < 50, 'TextBlock retained a fixed-width fallback');
            const rating = last.nodes.find(n => n.fleckId === 'rating');
            assert.ok(rating.properties.some(p => p.name === 'Value' && p.value === 73), 'Custom property missing from inspector snapshot');
            for (const width of ['60', 'Auto']) {
                fs.writeFileSync(file, template(30).replace('Id="display"', `Id="display" Width="${width}"`));
                await until(s => s.nodes.some(n => n.fleckId === 'display' && widthOf(n) === (width === 'Auto' ? intrinsic : 60)), 'TextBlock Width did not refresh');
                const display = last.nodes.find(n => n.fleckId === 'display');
                assert.equal(display.properties.find(p => p.name === 'Width').value, width === 'Auto' ? 'Auto' : 60);
                assert.equal(last.nodes.find(n => n.fleckId === 'PART_Value').bounds[0], display.bounds[2]);
            }
            result.passed.push('text-content-width-explicit-width-auto-and-inspector-values');
            const scoreTop = last.nodes.find(n => n.fleckId === 'score').bounds[1];
            await change(18, '', 18);
            assert.equal(last.nodes.find(n => n.fleckId === 'score').bounds[1], scoreTop - 12, 'Following sibling did not move up');
            await change(18, 'MinHeight="40"', 40);
            await change(18, 'Height="60"', 60);
            await change(30, '', 30);
            result.passed.push('dock-auto-height-shrinks-with-template-children', 'dock-explicit-height-and-minimum', 'dock-refresh-preserves-instance-state');
            fs.writeFileSync(file, original);
            await until(s => s.fleckRefresh.result === 0 && !s.nodes.some(n => n.fleckId === 'dockRoot'), 'Dock template restore failed');
            return;
        }
        const changed = original.replace(/(<TextBlock\s+Id="display"[^>]*?)(\sHeight="[^"]*")?(\s*\/?>)/,
            (match) => match.replace(/\sHeight="[^"]*"/, '').replace(/\s*\/?>$/, ' Height="24"/>'))
            .replace('</StackPanel>', '<TextBlock Id="watchMarker" Text="Template changed"/></StackPanel>');
        fs.writeFileSync(file, changed);
        await until(s => s.nodes.some(n => n.fleckId === 'watchMarker'), 'Template save did not refresh');
        assert.equal(last.processId, pid); assert.equal(last.debugSession, session);
        assert.ok(last.nodes.some(n => n.text === 'Preserved notes'));
        assert.ok(last.nodes.some(n => n.fleckId === 'display' && n.text === '73'));
        const display = last.nodes.find(n => n.fleckId === 'display');
        assert.equal(display.bounds[3] - display.bounds[1], 24, 'Template Height did not change actual layout');
        const score = last.nodes.find(n => n.fleckId === 'score');
        const siblings = last.nodes.filter(n => n.parent === score.parent).sort((a, b) => a.siblingIndex - b.siblingIndex);
        assert.deepEqual(siblings.map(n => n.fleckId), ['primary', 'secondary', 'rating', 'score']);
        result.passed.push('height-and-sibling-order-after-template-refresh');
        result.passed.push('template-refresh-same-process-and-state');
        for (const bad of ['<ControlTemplate broken', changed.replace('Id="PART_Value"', 'Id="WrongPart"')]) {
            const sequence = last.fleckRefresh.sequence;
            fs.writeFileSync(file, bad);
            await until(s => s.fleckRefresh?.sequence !== sequence && s.fleckRefresh?.result !== 0, 'Invalid template not diagnosed');
            assert.ok(last.nodes.some(n => n.fleckId === 'watchMarker'));
            assert.ok(last.nodes.some(n => n.text === 'Preserved notes'));
            fs.writeFileSync(file, changed);
            await until(s => s.fleckRefresh?.result === 0, 'Template did not recover');
        }
        result.passed.push('syntax-and-required-part-rollback-and-recovery');
        const layoutChanged = changed.replace('Id="display"', 'Id="display" Width="120" MinWidth="80" MaxWidth="160" Margin="3,4,5,6" HorizontalAlignment="Right"');
        fs.writeFileSync(file, layoutChanged);
        await until(s => s.nodes.some(n => n.fleckId === 'display' && n.bounds[2] - n.bounds[0] === 120), 'Template width did not refresh');
        let currentDisplay = last.nodes.find(n => n.fleckId === 'display');
        let parent = last.nodes.find(n => n.id === currentDisplay.parent);
        assert.equal(currentDisplay.bounds[2], parent.bounds[2] - 5, 'Template right alignment/margin');
        const main = path.join(project, 'main.fleck'), originalMain = fs.readFileSync(main, 'utf8');
        fs.writeFileSync(main, originalMain.replace('Id="rating"', 'Id="rating" Width="260" Margin="6,2" HorizontalAlignment="Center"'));
        await until(s => s.nodes.some(n => n.fleckId === 'rating' && n.bounds[2] - n.bounds[0] === 260), 'Component host width did not refresh');
        assert.equal(last.processId, pid);
        assert.ok(last.nodes.some(n => n.text === 'Preserved notes'));
        const sequence = last.fleckRefresh.sequence;
        fs.writeFileSync(file, layoutChanged.replace('MaxWidth="160"', 'MaxWidth="40"'));
        await until(s => s.fleckRefresh.sequence !== sequence && s.fleckRefresh.result !== 0, 'Invalid size constraint not diagnosed');
        assert.ok(last.nodes.some(n => n.fleckId === 'display' && n.bounds[2] - n.bounds[0] === 120), 'Invalid layout destroyed old tree');
        fs.writeFileSync(main, originalMain);
        result.passed.push('template-and-component-layout-refresh-with-rollback');
        fs.writeFileSync(file, original);
        await until(s => s.fleckRefresh?.result === 0 && !s.nodes.some(n => n.fleckId === 'watchMarker'), 'Final restore failed');
        result.passed.push('restore-without-restart');
    } catch (error) { result.errors.push(error.stack); throw error; }
    finally {
        child.kill();
        fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2));
        fs.writeFileSync(path.join(output, 'app.log'), log);
        console.log(JSON.stringify(result, null, 2));
    }
}
run().catch(error => { console.error(error); process.exitCode = 1; });
