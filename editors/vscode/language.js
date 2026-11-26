'use strict';
const vscode = require('vscode');

function english() {
    return vscode.workspace.getConfiguration('sunfleck').get('language') === 'en';
}

function tr(chinese, englishText) {
    return english() ? englishText : chinese;
}

module.exports = { english, tr };
