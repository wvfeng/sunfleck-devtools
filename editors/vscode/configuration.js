'use strict';
const vscode = require('vscode');

function configuredDebugPipe() {
    const folders = vscode.workspace.workspaceFolders || [];
    const activeUri = vscode.window.activeTextEditor?.document.uri;
    const activeFolder = activeUri && vscode.workspace.getWorkspaceFolder?.(activeUri);
    const ordered = activeFolder ? [activeFolder, ...folders.filter(folder => folder !== activeFolder)] : folders;
    for (const folder of ordered) {
        const pipe = vscode.workspace.getConfiguration('sunfleck', folder.uri).get('debugPipe', '');
        if (pipe) { return pipe; }
    }
    return vscode.workspace.getConfiguration('sunfleck').get('debugPipe', '');
}

module.exports = { configuredDebugPipe };
