# 开发工具测试入口

所有命令从仓库根目录执行。依赖和基础构建见 [构建与测试](../../../core/docs/building.md)，当前验证范围见 [验证与复现](../../../core/docs/validation.md)。桌面运行时调试使用 Named Pipe，Web 检查器通过本机 HTTP 连接。测试结果和截图可单独保存。

## Web 检查器回归

使用 .NET 10 运行协议回归。

```powershell
dotnet run --project tests/development/web-inspector --no-launch-profile -- --verify
```

测试启动临时本机端口，用扩展的 HTTP 客户端检查 gzip 快照、属性当前值和默认值、模板相对路径、高亮、拾取、取消和重连。

在独立 Visual Studio 实例中打开 [Sunfleck.WebInspector.Regression.slnx](web-inspector/Sunfleck.WebInspector.Regression.slnx)，打开项目中的 Main.fleck 后按 F5。工程会启动本机页面，检查器自动连接。展开 primary 下的 Border，点击该行的「定位源码」，应跳到 ActionTemplate.fleck 第 3 行第 3 列。选择 primary 可检查 Width 当前值 144、默认值 0。浏览器页面用五个固定节点模拟运行时。真实 Wasm 验收可使用文档站工程。

## 三语言项目 F12

模板监听回归：`node tests/development/template-watch.js C:/Users/Super/Documents/Demos/csharp` 在 out 下复制独立 C# 项目，以真实 `--watch` 入口运行。仅修改副本中的模板，验证同进程刷新、评分和输入状态保持、语法错误/必需部件缺失的回滚，以及恢复。实际 VS Code 的 formatting 套件覆盖嵌套 ControlTemplate 保存缩进与幂等性，并通过输入 `>` 后继续键入检查普通/命名空间标签的光标位置。

DockPanel 模板回归：同一命令追加 `--dock`，验证 Auto 高度随子项从 30 收缩到 18、组件宿主及后续兄弟节点随之更新、显式 Height/MinHeight 约束，以及热刷新期间评分和输入状态保持。测试仅修改临时副本。

先按 [独立 Demos](../../../docs/guides/windows-quickstart.md#独立-demos) 构建三个项目，然后运行：

```powershell
& tests/development/vscode-acceptance.ps1 -BuildDirectory out/dev -Suite demo-c,demo-cpp,demo-csharp -DemoRoot C:/Users/Super/Documents/Demos -TimeoutSeconds 60
```

每个套件单独打开对应项目，使用隔离配置和实际 VSIX，检查控件类、公开属性、模型属性和内建标签的定义查询与 F12 打开文件，确保自定义声明留在本项目。

C# SDK 源文件内继续沿父类导航需要额外验证官方 C# 扩展。准备包含应用及 `sdk/types/Sunfleck.Navigation.csproj` 的解决方案，并安装官方 C# 扩展后运行：

```powershell
& tests/development/csharp-sdk-navigation.ps1 -Project C:/Users/Super/Documents/Demos/csharp
```

该测试使用隔离的 VS Code 配置及已安装的官方 C# 扩展，执行实际 F12，覆盖 TextBlock → VisualElement → FrameworkElement、UserControl → Control → FrameworkElement，以及属性描述和模型类型。断言目标均在项目 SDK 内，并检查 FrameworkElement 的两个 partial 定义。结果保存在 `out/devtools/csharp-navigation-*`，不修改用户编辑器设置。

## Pipe 与 Windows 日常使用

对象树悬停专项：`ctest --test-dir out/devtools -R '^debug_highlight$' --output-on-failure` 验证叶节点/容器像素变化、裁剪、主动清除、租约过期及页面替换；`node tests/development/inspector-tree.js` 验证 Webview 消息、层级、命令白名单、显隐和销毁；inspector-client.js 通过真实 LSP 验证大于 JavaScript 精确整数范围的对象 ID 和移出清除。

下述 F5 验收还检查四类节点的高亮/清除及租约过期。加 `-HoverSeconds 150 -TimeoutSeconds 330` 可保留可见测试窗口进行鼠标检查，结果的 hoverObservations 保存交互阶段的高亮对象变化；不带该选项时不进行手工鼠标验证。

独立 C/C++ 示例的 F5 前置任务与原生调试启动使用以下入口验证（需安装官方 C/C++ 扩展）：

```powershell
& tests/development/native-demo-launch.ps1 -Project C:/Users/Super/Documents/Demos/cpp
& tests/development/native-demo-launch.ps1 -Project C:/Users/Super/Documents/Demos/c
```

测试在隔离的 VS Code 配置中使用项目原有 launch.json 和 preLaunchTask，检查构建任务退出码为零、调试器创建目标进程且应用保持运行，以及 Sunfleck 对象树自动连接到同一进程并包含 primary、secondary、rating 三个组件实例，最后停止测试会话。测试不手动激活或连接检查器。证据写入 `out/devtools/native-launch-*`。该入口能发现独立 PowerShell 构建无法复现的集成终端问题；独立示例脚本明确选择 Visual Studio 自带的 CMake/Ninja/CTest，避免 PATH 中 Strawberry Perl 的 CMake 在终端环境中发生字符转换异常。

该入口也支持 `-Project C:/Users/Super/Documents/Demos/csharp`，此时需要官方 C# 调试扩展，并使用项目的 coreclr 配置。三语言示例分别配置不同的 `sunfleck.debugPipe`，launch.json 通过对应调试器的环境变量字段传入同名 `SUNFLECK_DEBUG_PIPE`；C/C++ 的可视应用创建选项还需启用 `UI_DEBUG_PIPE`，C# App 自动读取环境变量。

源码导航检查从 UserCard 的 TextBlock 依次切换到内部 StackPanel、Card、主页面外层 StackPanel，再检查 primary/secondary/rating 实例及 score，断言实际编辑器文件、行号及标签，并保存 snapshot.json。C# 从嵌入资源调用 LoadFleck 后必须用 SetFleckSourceUri 补充主页面的文件身份；示例使用 CallerFilePath 取得编译时项目位置，不从进程工作目录猜测地址，也不为运行增加源码文件依赖。

| 入口 | 范围与前置条件 |
| --- | --- |
| [inspector-client.js](inspector-client.js) | 内存 Pipe 夹具＋真实 LSP＋VS Code 适配器，覆盖层级、定位、模型补全／定义／诊断、冲突、断开、独立 owner 与在途读取释放；无需 IDE |
| [vscode-pipe-acceptance.ps1](vscode-pipe-acceptance.ps1) | 真实 VS Code／coreclr，两代 App 自动连接、定位、拾取／取消、客户端突然断开、App 缺席和无响应超时 |
| [debug-lifecycle.js](vscode-host/debug-lifecycle.js) | 通过上述脚本的 `-Suite debug-lifecycle` 运行实际 demo；真实源码断点、暂停超时、继续后快照恢复、关闭窗口后的 IDE 自然退出；`-Console internalConsole` 可做控制台对照 |
| [extension-lifecycle.js](vscode-host/extension-lifecycle.js) | `-Suite extension-lifecycle` 使用隔离的普通扩展宿主，执行真实 Reload Window 后核对旧服务退出、新 owner 和同一 App 会话恢复 |
| [workspace-pipe.js](vscode-host/workspace-pipe.js) | `-Suite workspace-pipe` 打开 `.code-workspace`，只显示 C++ 文件；不显式激活／连接扩展，验证文件夹配置自动连接真实 C++ 应用、对象定位及重新连接；需先构建 `tests/development/fixtures/legacy-demos/counter-cpp` |
| [windows-workflow.ps1](windows-workflow.ps1) | 真实 VS Code／C#，提示、格式化、F5、Fleck 更新／回滚、Getter 热重载与两种更新共存 |
| [DebugInspectorTests.cs](../csharp/DebugInspectorTests.cs) | 环境配置自动启动真实 C# App Pipe、非法配置拒绝、内存快照校验、源码映射、LSP 模型同步，不执行业务 getter |
| [InspectorDesktopTests.cs](../csharp/InspectorDesktopTests.cs) | 独立 LSP 通过 Pipe 控制真实多窗口应用，验证拾取、取消、输入隔离、IME 忙碌拒绝及主窗关闭后的继续拾取 |

先构建当前库和 demo，再按变更选择运行：

```powershell
pwsh -NoProfile -File tools/development/windows-dev.ps1 -Action Prepare
dotnet build tests/development/fixtures/legacy-demos/counter/Counter.csproj
node tests/development/inspector-client.js out/windows-dev/service/Sunfleck.Development.dll
pwsh -NoProfile -File tests/development/vscode-pipe-acceptance.ps1
pwsh -NoProfile -File tests/development/vscode-pipe-acceptance.ps1 -Suite debug-lifecycle
pwsh -NoProfile -File tests/development/vscode-pipe-acceptance.ps1 -Suite extension-lifecycle
pwsh -NoProfile -File tests/development/vscode-pipe-acceptance.ps1 -Suite workspace-pipe -InstalledPackage <当前VSIX>
node tests/development/workspace-configuration.js
pwsh -NoProfile -File tests/development/windows-workflow.ps1
```

Pipe 宿主默认加载扩展源码及 `out/windows-dev/service`，可用 `-ExtensionPath <解压后extension目录>` 验证实际 VSIX 中的扩展和随包服务。每轮使用独立 profile、唯一 Pipe 和用户扩展目录中的官方 C# 调试器；输出为 `out/windows-dev/debug-pipe-*` 或 `debug-lifecycle-*`，包含结果、DAP 事件和产物哈希。默认连接组还覆盖服务手动重启、崩溃恢复、连续崩溃停止恢复、应用强制退出及检查器关闭重开；`-Generations 5 -TimeoutSeconds 180` 可检查连续五代应用。日常流程检查输出为 `out/windows-dev/check-*`，其中 C# 热重载按当前计划暂时搁置。

## 编辑器专项

[vscode-acceptance.ps1](vscode-acceptance.ps1) 保留 `base`、`diagnostics`、`formatting`、`model-switch`、`multi-root`、`folder-task` 六个默认组，以及 `editing`、`unicode` 两个可选组。诊断与对象树使用内存 Pipe 夹具，其余语言查询通过真实 VS Code API 进入随包服务。

需要 Windows、PowerShell 7、Node、VS Code、.NET 和 [开发工具构建](../../../core/docs/building.md#开发工具构建) 生成的 C／C++／C# 元数据。包从当前源码正常构建；建议显式传入实际包路径：

```powershell
& ./tests/development/vscode-acceptance.ps1 -Package out/windows-dev/sunfleck-through-step8.vsix
& ./tests/development/vscode-acceptance.ps1 -Package out/windows-dev/sunfleck-through-step8.vsix -Suite editing,unicode -TimeoutSeconds 240
& ./tests/development/vscode-acceptance.ps1 -Package editors/vscode/sunfleck-0.2.3.vsix -Suite components,unicode -TimeoutSeconds 60
```

每组使用隔离 profile／extensions／shared-data，结果位于 `out/devtools/vscode-current/run-*`。合成数据验证编辑器行为，不能代替实际 App 验收。部分 editing／unicode 独立审计仍含历史包身份对照，使用前核对适用条件。

## 桌面与滚动复验

需要已解锁的 Windows 桌面。CTest 用例为 `win32_inspector_protocol`、`node_scale`、`win32_node_scale`、`csharp_node_scale`、`multi_window`、`win32_multi_window`、`win32_fleck_refresh`。独立 C# 桌面入口：

```powershell
dotnet tests/csharp/bin/Debug/net10.0/Sunfleck.Tests.dll --inspector-desktop out/windows-dev/pipe-desktop
```

运行目录需要当前 `sunfleck_native.dll`。桌面测试将结果保存为 JSON／日志用于审计，通信仍通过 Pipe。合成 IME 和注入消息不代表物理输入或真实输入法验证。

4096 项外部视觉复验使用 [node-scale-visual-fixture.ps1](node-scale-visual-fixture.ps1) 和 [node-scale-visual-audit.ps1](node-scale-visual-audit.ps1)。准备其默认构建：

```powershell
cmake -S . -B out/devtools-scrollbar -G Ninja -DCMAKE_BUILD_TYPE=Debug -DBUILD_TESTING=ON -DUI_BUILD_CSHARP=OFF -DUI_BUILD_EXAMPLES=OFF -DUI_BUILD_DEMOS=OFF -DUI_ENABLE_D3D11=OFF
cmake --build out/devtools-scrollbar --target node_scale_test win32_tree development_test multi_window_test
./tests/development/node-scale-visual-fixture.ps1
./tests/development/node-scale-visual-audit.ps1 -Run <本次运行目录>
```

三个加载路径分别拖动至中段、点击 1995、按 Left 到 1994、滚轮后拖至 4095 并点击，再正常关闭。夹具不自动操作窗口，审计需要真实截图、观察记录和退出时直接采集的内存状态。

## 原生 Pipe 与构建重启

先按开发工具构建生成 `out/devtools`。C／C++ 示例启用 `UI_DEBUG_PIPE`，C# 读取同一环境变量；各自用唯一 Pipe 隔离。生成页面随构建生成 sidecar，C# 二进制示例也复制并加载 `counter.flkb.fleckmap`。

```powershell
ctest --test-dir out/devtools -R '^native_debug_pipe$' --output-on-failure
pwsh -NoProfile -File tests/development/vscode-pipe-acceptance.ps1 -Suite native-pipe
pwsh -NoProfile -File tests/development/native-watch-acceptance.ps1
pwsh -NoProfile -File tests/development/vscode-pipe-acceptance.ps1 -Suite multi-instance
pwsh -NoProfile -File tests/development/vscode-pipe-acceptance.ps1 -Suite extension-lifecycle -InstalledPackage <当前VSIX> -DisableCycle
```

[原生监听验收](native-watch-acceptance.ps1) 在独立目录生成三语言实际夹具，验证构建错误不结束旧进程、成功后新会话及应用计数交接；用 `-Language c,cpp` 可定向执行。

[桌面夹具](desktop-fixture/Program.cs) 包含真实文本框、列表、树、异步命令及可切换 getter 错误。构建到 `out/windows-dev/desktop-fixture` 并复制当前原生 DLL；运行副本后只修改输出目录的 main.fleck。观察中文 IME 组合／异步期间 pending、提交后的状态保留；Toggle getter failure 用于诊断失败／恢复。预期业务异常在 Poll 边界处理，错误仍可供检查器查询。

## Visual Studio 实际宿主与 EnC

入口均使用独立 `/RootSuffix SunfleckAcceptance`，不使用 VSIXInstaller。先构建当前 VSIX，关闭已有实验宿主，再运行 [准备脚本](prepare-visualstudio.ps1)。默认显式三语言元数据；`-DiscoverModels` 用于实际项目／配置发现。已有配置可用 `-SkipConfiguration`。输出目录为 `out/devtools/ide-acceptance`，host.json 给出实际 PID，artifacts.json 记录包和服务哈希。

IDE 自动化脚本使用 Windows PowerShell（Framework）和 STA；参数中的 PID 必须来自本轮 host.json。实际桌面、Pipe、WMI 和 MSBuild FileTracker 需要可访问桌面的进程权限。首次打开有混合行尾的源码时，保留原行尾，处理宿主提示后重跑；测试不应自动保存仓库源码。

```powershell
dotnet build editors/visualstudio/Sunfleck.VisualStudio.csproj -p:SunfleckServerDirectory=C:/绝对路径/out/windows-dev/service
powershell.exe -NoProfile -STA -File tests/development/prepare-visualstudio.ps1
powershell.exe -NoProfile -STA -File tests/development/inspect-visualstudio.ps1 -ProcessId <PID> -VerifyFeatures
pwsh -NoProfile -File tests/development/prepare-enc.ps1
powershell.exe -NoProfile -STA -File tests/development/visualstudio-enc.ps1 -ProcessId <PID> -Action start
powershell.exe -NoProfile -STA -File tests/development/visualstudio-enc.ps1 -ProcessId <PID> -Action break
powershell.exe -NoProfile -STA -File tests/development/visualstudio-enc.ps1 -ProcessId <PID> -Action apply
powershell.exe -NoProfile -STA -File tests/development/visualstudio-enc.ps1 -ProcessId <PID> -Action continue
```

[EnC 工程](prepare-enc.ps1) 使用本机 VS 2026 v145、/ZI、/Od、/MDd 和增量链接，复制夹具到输出目录。apply 前确认已进入断点模式（mode=2）。真实点击 Increment，核对 Count: 5 以及前后 PID／debugSession 相同；最后 `-Action stop`。原始夹具源码不被修改。

[配置验收](visualstudio-configuration.ps1) 需要无 SUNFLECK_MODEL_FILES 的宿主及准备好的 EnC 工程，测试 Debug／Release／Debug 和另一解决方案的模型发现。先在真实选项对话框启用 Format on save、确定、重启，再加 `-VerifySavedSettings` 检查持久化；直接设置 DTE 属性不等于已保存。

[运行时切换](visualstudio-runtime.ps1) 的 `-Language c|cpp|csharp|csharp-xml` 启动当前应用、采集会话与源码并打开对应 Fleck；由实际侧栏核对树、反查、拾取及诊断。它只清理自身记录且 PID／路径／启动时间一致的夹具。结束时 `-Language stop`；关闭实验 IDE 用 inspect-visualstudio.ps1 的 `-Close`。

## PDB 诊断工具

以下项目用于 [portable PDB 回退问题](../../../docs/guides/devtools.md#portable-pdb-回退)，不参与日常 demo：

| 项目 | 用途 |
| --- | --- |
| [pdb-lock-console](watch-fixture/pdb-lock-console/LockConsole.csproj) | 无 Sunfleck／无 IDE 的 dotnet watch 对照，由 `pdb-lock-repro.ps1` 运行 |
| [sdk-inspect](watch-fixture/sdk-inspect/SdkInspect.csproj) | 只读检查 SDK IL |
| [session-probe](watch-fixture/session-probe/SessionProbe.csproj) | 独立会话／PDB 映射和编译对照；需要 net11.0 及 `SdkWatchPath`／`SdkCompilerPath` |
| [watch-patch](watch-fixture/watch-patch/WatchPatch.csproj) | 对匹配哈希的 SDK 11 watcher 创建工作区修正副本；输出必须不存在，非正式 SDK 修复方案 |

诊断输出统一放在 `out/devtools`，不替换全局 SDK。条件验证通过不等于默认 SDK 问题已解决。

## 清理与文档检查

运行输出、复制的项目、IDE profile 和诊断 SDK 副本放在被忽略的 `out/`，核对结果后按需清理。保留 `vscode-host`、Pipe 夹具和诊断项目源码；`bin/obj` 可重建。

```powershell
node tests/development/check-doc-links.js
```

内置属性运行时核对：`python tests/development/audit-runtime-properties.py out/devtools/element-schema.json out/devtools/sunfleck_native.dll out/property-runtime-audit.md`。遍历当前 schema 的全部内置标签，核对真实 Headless 页面中的默认值、显式赋值，以及普通视觉节点的实际宽高；Dock 结构节点的几何区域由 workspace 决定，单独列明。

`node tests/development/inspector-tree.js` 验证选择/定位分离、属性数据更新和安全的命令转发。`node tests/development/inspector-client.js out/devtools/tools/development/Sunfleck.Development.dll` 验证真实语言服务和检查器适配器。
