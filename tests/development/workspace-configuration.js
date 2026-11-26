'use strict';
const assert = require('node:assert/strict');
const Module = require('node:module');

const first = { uri: { fsPath: '/first' } }, second = { uri: { fsPath: '/second' } };
const pipes = new Map([[first.uri, 'first-pipe'], [second.uri, 'second-pipe']]);
let globalPipe = '', activeFolder;
const vscode = {
    workspace: {
        workspaceFolders: [first, second],
        getWorkspaceFolder: () => activeFolder,
        getConfiguration: (_, uri) => ({ get: () => uri ? pipes.get(uri) || globalPipe : globalPipe })
    },
    window: { activeTextEditor: { document: { uri: {} } } }
};
const originalLoad = Module._load;
let configuredDebugPipe;
try {
    Module._load = function (name, ...args) { return name === 'vscode' ? vscode : originalLoad.call(this, name, ...args); };
    ({ configuredDebugPipe } = require('../../editors/vscode/configuration'));
} finally { Module._load = originalLoad; }

// In a .code-workspace, a resource setting can be empty globally and populated per folder.
assert.equal(vscode.workspace.getConfiguration('sunfleck').get('debugPipe'), '');
assert.equal(configuredDebugPipe(), 'first-pipe');
activeFolder = second;
assert.equal(configuredDebugPipe(), 'second-pipe');
activeFolder = undefined;
pipes.delete(first.uri);
assert.equal(configuredDebugPipe(), 'second-pipe');
pipes.clear();
globalPipe = 'workspace-pipe';
assert.equal(configuredDebugPipe(), 'workspace-pipe');
vscode.workspace.workspaceFolders = [];
assert.equal(configuredDebugPipe(), 'workspace-pipe');
globalPipe = '';
assert.equal(configuredDebugPipe(), '');
console.log('Workspace Pipe configuration: folder scope, active folder, configured fallback and global settings passed.');
