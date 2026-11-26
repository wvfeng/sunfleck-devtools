'use strict';
module.exports = {
    activate() {
        if (process.env.SUNFLECK_RELOAD_TEST !== '1') { return; }
        // A normal extension host can reload; --extensionTestsPath exits on reload.
        void require('./extension-lifecycle').run().catch(error => console.error(error)).finally(() =>
            require('vscode').commands.executeCommand('workbench.action.quit'));
    },
    deactivate() {}
};
