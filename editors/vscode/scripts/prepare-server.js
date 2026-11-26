'use strict';
const fs = require('node:fs');
const path = require('node:path');

const extension = path.resolve(__dirname, '..');
const source = path.resolve(process.argv[2] || path.join(extension, '..', '..', 'out', 'devtools', 'tools', 'development'));
const destination = path.join(extension, 'server');
const required = ['Sunfleck.Development.dll', 'Sunfleck.dll', 'Sunfleck.Development.runtimeconfig.json', 'Sunfleck.Development.deps.json',
    'Sunfleck.ComponentGenerator.dll', 'Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll', 'types/Elements.cs', 'types/Components.cs'];
for (const name of required) {
    if (!fs.existsSync(path.join(source, name))) {
        throw new Error(`缺少 ${name}，请先构建 sunfleck_development 或传入其输出目录。`);
    }
}
const native = fs.readdirSync(source).filter(name => /^(lib)?sunfleck_native\.(dll|so|dylib)$/.test(name));
if (native.length === 0) {
    throw new Error('未找到共享服务所需的 Sunfleck 原生库。');
}
fs.mkdirSync(destination, { recursive: true });
for (const name of [...required, ...native]) {
    fs.mkdirSync(path.dirname(path.join(destination, name)), { recursive: true });
    fs.copyFileSync(path.join(source, name), path.join(destination, name));
}
console.log(`已准备共享语言服务：${destination}`);
