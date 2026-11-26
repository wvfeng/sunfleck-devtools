'use strict';

const { randomUUID } = require('node:crypto');

const browserDebugTypes = ['msedge', 'pwa-msedge', 'chrome', 'pwa-chrome'];

function inspectorUrl(configuration) {
    if (!browserDebugTypes.includes(configuration.type)) {
        return;
    }
    try {
        const url = new URL(configuration.url);
        if (url.protocol === 'http:' && url.hostname === '127.0.0.1' &&
            !url.username && !url.password && url.searchParams.get('sunfleckInspector') === '1') {
            return url;
        }
    } catch {
        // The debug configuration may launch a file or attach without a URL.
    }
}

function resolveBrowserConfiguration(_folder, configuration) {
    const url = inspectorUrl(configuration);
    if (url && configuration.request === 'launch') {
        url.searchParams.set('sunfleckInspectorSession', randomUUID());
        configuration.url = url.href;
    }
    return configuration;
}

function snapshotEndpoint(session) {
    const url = inspectorUrl(session.configuration);
    if (!url) return;
    const endpoint = new URL('/__sunfleck/snapshot', url);
    const key = url.searchParams.get('sunfleckInspectorSession');
    if (key) endpoint.searchParams.set('session', key);
    return endpoint;
}

async function requestSnapshot(endpoint) {
    const response = await fetch(endpoint, {
        headers: { 'X-Sunfleck-Inspector': '1' },
        redirect: 'error',
        signal: AbortSignal.timeout(1500)
    });
    if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
    }
    if (response.status === 204) return;
    return { snapshot: await response.json(), page: response.headers.get('X-Sunfleck-Page') };
}

async function browserCommand(endpoint, page, action, value) {
    const response = await fetch(endpoint, {
        method: 'POST',
        headers: { 'X-Sunfleck-Inspector': '1', 'X-Sunfleck-Page': page, 'Content-Type': 'application/json' },
        body: JSON.stringify({ action, value }),
        redirect: 'error',
        signal: AbortSignal.timeout(1000)
    });
    if (!response.ok) throw new Error('HTTP ' + response.status);
}

module.exports = { browserDebugTypes, resolveBrowserConfiguration, snapshotEndpoint, requestSnapshot, browserCommand };
