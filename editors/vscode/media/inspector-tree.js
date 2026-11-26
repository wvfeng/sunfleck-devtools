'use strict';
const vscode = acquireVsCodeApi();
const t = (zh, en) => document.documentElement.lang === 'en' ? en : zh;
const tree = document.getElementById('tree');
const expanded = new Set(vscode.getState()?.expanded || []);
let rows = [], hovered = '', selected = '', focused = '';
let byId = new Map();
const elements = new Map();
function hover(id) {
    if (hovered === id) return;
    hovered = id;
    vscode.postMessage(id ? { type: 'hover', id } : { type: 'leave' });
}
function visible(row) {
    for (let parent = row.parent; parent; parent = byId.get(parent)?.parent) {
        if (!expanded.has(parent)) return false;
    }
    return true;
}
function render() {
    const present = new Set(rows.map(r => r.id));
    for (const [id, element] of elements) {
        if (!present.has(id)) { if (id === hovered) hover(''); element.remove(); elements.delete(id); }
    }
    let position = tree.firstElementChild;
    for (const row of rows) {
        let element = elements.get(row.id);
        if (!element) {
            element = document.createElement('div');
            element.className = 'row'; element.setAttribute('role', 'treeitem'); element.dataset.id = row.id;
            const indent = document.createElement('span'); indent.className = 'indent';
            const arrow = document.createElement('span'); arrow.className = 'arrow'; arrow.setAttribute('aria-hidden', 'true');
            const label = document.createElement('span'); label.className = 'label';
            const detail = document.createElement('span'); detail.className = 'detail';
            const locate = document.createElement('button'); locate.className = 'locate';
            locate.title = t('定位源码', 'Go to source'); locate.setAttribute('aria-label', locate.title);
            element.append(indent, arrow, label, detail, locate); elements.set(row.id, element);
        }
        element.children[0].textContent = '\u00a0\u00a0'.repeat(row.depth);
        element.children[2].textContent = row.label; element.children[3].textContent = row.description;
        element.children[4].hidden = !row.locatable;
        element.title = [row.label, row.description].filter(Boolean).join(' · ');
        element.setAttribute('aria-level', String(row.depth + 1));
        element.setAttribute('aria-selected', String(row.id === selected));
        if (row.expandable) element.setAttribute('aria-expanded', String(expanded.has(row.id)));
        else element.removeAttribute('aria-expanded');
        element.hidden = !visible(row); element.tabIndex = row.id === focused ? 0 : -1;
        if (element.hidden && row.id === hovered) hover('');
        if (element !== position) tree.insertBefore(element, position);
        position = element.nextElementSibling;
    }
    const first = [...elements.values()].find(e => !e.hidden);
    if (first && ![...elements.values()].some(e => e.tabIndex === 0 && !e.hidden)) first.tabIndex = 0;
    vscode.setState({ expanded: [...expanded] });
}
function toggle(row, open = !expanded.has(row.id)) {
    if (open) expanded.add(row.id); else expanded.delete(row.id);
    render();
}
tree.addEventListener('pointerover', event => {
    const row = event.target.closest('.row'); hover(row?.dataset.id || '');
});
tree.addEventListener('pointerleave', () => hover(''));
window.addEventListener('blur', () => hover(''));
document.addEventListener('visibilitychange', () => { if (document.hidden) hover(''); });
tree.addEventListener('click', event => {
    const element = event.target.closest('.row'); if (!element) return;
    const row = rows.find(r => r.id === element.dataset.id); if (!row) return;
    focused = selected = row.id; element.focus();
    if (event.target.classList.contains('arrow') && row.expandable) toggle(row);
    else if (event.target.closest('.locate')) vscode.postMessage({ type: 'locate', id: row.id });
    else if (row.object) vscode.postMessage({ type: 'select', id: row.id });
    else if (row.actionable) vscode.postMessage({ type: 'activate', id: row.id });
    else if (row.expandable) toggle(row);
    render();
});
tree.addEventListener('keydown', event => {
    const element = event.target.closest('.row'); if (!element) return;
    const row = rows.find(r => r.id === element.dataset.id);
    const list = rows.filter(visible); let next;
    switch (event.key) {
        case 'ArrowDown': next = list[list.indexOf(row) + 1]; break;
        case 'ArrowUp': next = list[list.indexOf(row) - 1]; break;
        case 'Home': next = list[0]; break;
        case 'End': next = list.at(-1); break;
        case 'ArrowRight': if (row.expandable && !expanded.has(row.id)) toggle(row, true); else next = list[list.indexOf(row) + 1]; break;
        case 'ArrowLeft': if (expanded.has(row.id)) toggle(row, false); else next = rows.find(r => r.id === row.parent); break;
        case 'Enter': case ' ': if (event.target.closest('.locate')) { vscode.postMessage({ type: 'locate', id: row.id }); } else if (row.object) { selected = row.id; vscode.postMessage({ type: 'select', id: row.id }); render(); } else if (row.actionable) vscode.postMessage({ type: 'activate', id: row.id }); else if (row.expandable) toggle(row); break;
        case 'Escape': hover(''); break;
        default: return;
    }
    event.preventDefault();
    if (next) { focused = selected = next.id; if (next.object) vscode.postMessage({ type: 'select', id: next.id }); render(); elements.get(next.id)?.focus(); }
});
window.addEventListener('message', event => {
    const message = event.data;
    if (message.type === 'tree') { rows = message.rows; selected = message.selected || ''; byId = new Map(rows.map(r => [r.id, r])); render(); }
    if (message.type === 'reveal') {
        let row = rows.find(r => r.id === message.id); selected = message.id;
        while (row?.parent) { expanded.add(row.parent); row = rows.find(r => r.id === row.parent); }
        render(); elements.get(selected)?.scrollIntoView({ block: 'nearest' });
    }
});
// Renew only while the DOM still reports this row under the pointer. The app's
// short lease also clears overlays when the editor crashes or the Pipe breaks.
setInterval(() => {
    const element = elements.get(hovered);
    if (element && !element.hidden && element.matches(':hover') && !document.hidden) vscode.postMessage({ type: 'hover', id: hovered });
    else if (hovered) hover('');
}, 250);
vscode.postMessage({ type: 'ready' });
