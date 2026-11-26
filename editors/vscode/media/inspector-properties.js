'use strict';
const vscode = acquireVsCodeApi();
const t = (zh, en) => document.documentElement.lang === 'en' ? en : zh;
const propertyContent = document.getElementById('property-content');
function renderProperties(node) {
    propertyContent.replaceChildren();
    if (!node) { propertyContent.textContent = t('点击元素查看属性', 'Select an element'); return; }
    const heading = document.createElement('div'); heading.className = 'property-title';
    heading.textContent = `${node.fleckId || node.type} · ${node.componentType || node.type}`; propertyContent.append(heading);
    const format = value => value === null || value === undefined ? '—' : typeof value === 'object' ? JSON.stringify(value) : String(value);
    function table(title, properties) {
        const caption = document.createElement('h4'); caption.textContent = title; propertyContent.append(caption);
        const grid = document.createElement('table'); propertyContent.append(grid);
        const hasDefaults = properties.some(p => Object.hasOwn(p, 'defaultValue'));
        if (hasDefaults) {
            const header = document.createElement('tr');
            for (const text of [t('属性', 'Property'), t('当前值', 'Current'), t('默认值', 'Default')]) {
                const cell = document.createElement('th'); cell.textContent = text; header.append(cell);
            }
            grid.append(header);
        }
        for (const property of properties) {
            const row = document.createElement('tr'), name = document.createElement('th'), value = document.createElement('td');
            name.textContent = property.name; value.textContent = format(property.value);
            const hints = [property.description, Object.hasOwn(property, 'defaultValue') ? `${t("默认值", "Default")}: ${format(property.defaultValue)}` : '', property.valueSource ? `${t('值来源', 'Value source')}: ${property.valueSource}` : '', Object.hasOwn(property, 'effectiveValue') ? `${t('有效值', 'Effective value')}: ${format(property.effectiveValue)}` : '', property.readOnly ? t('只读', 'Read only') : ''].filter(Boolean);
            row.title = hints.join('\n'); row.append(name, value);
            if (hasDefaults) { const cell = document.createElement('td'); cell.textContent = format(property.defaultValue); row.append(cell); }
            grid.append(row);
            if (property.declared) name.className = 'declared';
        }
    }
    const summary = document.createElement('p'); summary.className = 'runtime-summary';
    summary.textContent = node.bounds ? `${t('实际尺寸', 'Actual size')}: ${node.bounds[2]-node.bounds[0]} × ${node.bounds[3]-node.bounds[1]} px` : '';
    propertyContent.append(summary);
    table(t('属性（只读）', 'Properties (read only)'), node.properties || [{ name: 'Text', value: node.text }, { name: 'Value', value: node.value }]);
    table(t('运行时', 'Runtime'), [
        { name: 'ActualWidth (px)', value: node.bounds ? node.bounds[2] - node.bounds[0] : null },
        { name: 'ActualHeight (px)', value: node.bounds ? node.bounds[3] - node.bounds[1] : null },
        { name: 'Bounds (px)', value: node.bounds }, { name: 'Clip (px)', value: node.clip },
        { name: 'EffectiveIsEnabled', value: node.enabled }, { name: 'Focused', value: node.focused }
    ]);
    if (node.layoutWork) table(t('布局执行（累计）', 'Layout work (cumulative)'),
        Object.entries(node.layoutWork).map(([name, value]) => ({ name, value })));
    if (node.bindings?.length) table(t('绑定', 'Bindings'), node.bindings.map(b => ({ name: b.path, value: b.attached ? 'Attached' : 'Detached', description: JSON.stringify(b) })));
}
window.addEventListener('message', event => {
    if (event.data.type === 'properties') renderProperties(event.data.node);
});
vscode.postMessage({ type: 'ready' });
