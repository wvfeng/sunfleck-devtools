# Sunfleck DevTools

Fleck 语言服务、VS Code 和 Visual Studio 扩展。使用同平台的桌面 SDK 构建，.NET 版本为 10。

```powershell
$sdk = (Resolve-Path ../sdk/local/windows-x64).Path
dotnet build tools/development/Sunfleck.Development.csproj -c Release "-p:SunfleckSdkRoot=$sdk" -o out/devtools/tools/development
dotnet build Sunfleck.DevTools.slnx "-p:SunfleckSdkRoot=$sdk"
dotnet build tests/csharp/Sunfleck.Tests.csproj "-p:SunfleckSdkRoot=$sdk" "-p:SunfleckTestFont=C:/Fonts/NotoSansMono.ttf" -o out/tests
dotnet out/tests/Sunfleck.Tests.dll --components
```

Visual Studio 通过解决方案打开，VS Code 的打包说明见 [扩展 README](editors/vscode/README.md)。[开发工具回归入口](tests/development/README.md) 中涉及源码和运行时的历史脚本可能还会用到原聚合仓库路径。

推送 `v*` 标签会构建 VS Code 的 Windows x64、Linux x64 和 macOS arm64 扩展，以及 Visual Studio 的 Windows x64 扩展。构建通过后，工作流会把四个 VSIX 上传到同一个 GitHub Release。`vscode-v*` 和 `visualstudio-v*` 标签分别发布对应的扩展。

补发已有标签时，在 GitHub Actions 从默认分支运行 **Publish editor extensions**，将 `release_tag` 填为已有标签，并将 `editor` 保持为 `auto`。工作流会从该标签的提交构建并上传资产。留空 `release_tag` 时可手动验证构建，生成的 VSIX 可在 Actions 中下载。

工作流通过 `SUNFLECK_SDK_TAG` 选择要打包的 Core SDK 发布标签，默认取 `v1.0.0`。私有 Core 仓库可给工作流配置只读的 `SUNFLECK_CORE_READ_TOKEN`。
