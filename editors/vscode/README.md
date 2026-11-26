# Sunfleck Fleck

Fleck 文件默认启用保存格式化。可在用户或项目 settings.json 中覆盖：

```json
{
  "[fleck]": {
    "editor.formatOnSave": false
  }
}
```

`Width`/`Height`、Min/Max 尺寸、`Margin` 和水平/垂直对齐支持补全、F12 和单向绑定。Width/Height 提供 Auto 补全。对象树使用运行时真实同级索引排序，模板热刷新后仍保持布局顺序。未知属性、非法数值/外边距/对齐和矛盾的最小最大尺寸会在对应属性位置报告。

为 Sunfleck C、C++、C# 项目提供 Fleck 补全、格式化、悬停、文档结构、绑定检查、模型定义跳转和运行时对象树。Windows 日常使用从 简单计数器（源码仓库 `tests/development/fixtures/legacy-demos/counter/README.md`） 开始，完整步骤见 Windows 快速开始（源码仓库 `docs/windows-quickstart.md`）。

## 使用

需要 .NET 10 运行时。VSIX 附带构建平台的语言服务；源码开发也可把 `sunfleck.serverPath` 指向构建生成的 `Sunfleck.Development.dll`。模型提示读取 `.fleckmodel.json`，通过 `sunfleck.modelFiles` 选择实际构建的元数据；细节见 开发工具接入（源码仓库 `docs/devtools.md`）。

Windows C# App 的启动环境设置 `SUNFLECK_DEBUG_PIPE`，工作区设置 `sunfleck.debugPipe` 为相同名称，框架与扩展自动建立调试连接，业务代码无需调用调试 API。当前 `tests/development/fixtures/legacy-demos/counter` 的 F5 和 Run／Watch 已配置此流程；普通运行未设置环境变量时不启动调试服务。

C/C++ App 通过 `UI_DEBUG_PIPE` 使用同一环境变量，独立示例位于 `tests/development/fixtures/legacy-demos/counter-cpp`。0.2.2 修复了 `.code-workspace` 下漏读文件夹设置导致对象树未自动连接的问题：优先使用当前编辑文件所在文件夹的 Pipe，否则选择首个配置了 Pipe 的文件夹，再回退到工作区设置；无需先打开 `.fleck` 文件。语言服务路径和 dotnet 路径也支持文件夹设置，共享服务从首个工作区文件夹解析启动配置。

在资源管理器展开 **Sunfleck 对象树**，或从命令面板执行 **Sunfleck: 连接调试应用**。未启动 App 时等待连接，启动后显示对象树；可定位源码和拾取元素。**Sunfleck: 断开调试应用** 停止轮询并释放当前视图，之后可再次打开。

应用连接仅使用 Named Pipe；名称、订阅和超时行为见 调试协议（源码仓库 `docs/devtools.md`）。

0.2.5 支持对象树悬停高亮：鼠标停在节点行上时，应用以蓝色边框和半透明填充显示该实例的可见布局范围。移出、隐藏视图或断开后清除；连接中断时最迟在应用恢复轮询后的短时租约到期清除。容器、组件实例和模板节点均使用运行时对象 ID，重复实例互不混淆。旧运行库未声明 highlight 能力时仍可查看对象树和定位源码。

对象树使用支持鼠标进入/移出事件的 Webview，保留折叠、源码定位、拾取和绑定诊断；方向键、Home/End 导航，Enter/空格激活，Escape 清除高亮。仅 VS Code 对象树接入悬停，Visual Studio 界面本轮未改动。调试器暂停应用线程时，绘制与清除需要等待应用继续执行。

## 从源码打包

0.2.6 修复键入 `>` 自动补闭合标签后的光标位置，光标保留在开始/结束标签之间。保存格式化需要项目配置 `[fleck].editor.formatOnSave: true`；独立示例显式设置两空格和关闭自动检测缩进，以免从尚未整理的文件推断出错误宽度。格式化器保留属性内容和多行属性排列，只整理节点间空白。

先按 开发工具构建（源码仓库 `docs/building.md`） 生成服务和原生库，再在本目录执行（Node 20.18+）：

```powershell
npm ci --ignore-scripts
npm run check
npm run package
```

默认从仓库 `out/devtools/tools/development` 复制服务到包内 `server`。只有 `out/windows-dev/service` 构建时，可明确选择它，然后直接打包，避免默认 prepare 步骤覆盖：

```powershell
node scripts/prepare-server.js ../../out/windows-dev/service
npx --no-install vsce package --allow-missing-repository --target win32-x64
```

安装实际生成的 VSIX 后打开 `.fleck` 文件。配置与复验入口见源码仓库的 `docs/devtools.md` 和 `tests/development/README.md`。

## 0.2.9 对象属性面板

对象树下方新增只读属性面板。点击元素显示当前属性值、默认值、实际像素尺寸、边界、有效启用状态和绑定状态；行末定位图标才跳转源码。悬停继续在应用中高亮控件。键盘 Enter/空格选择元素，Tab 到定位按钮后按 Enter 可跳转。运行时新增属性快照，需要同步更新应用的私有 SDK。

属性值和实际像素尺寸分开展示，避免将 Auto、逻辑尺寸与父容器分配的区域混淆。密码输入内容不会显示。自定义组件同时显示公开属性、DataContext 和所用模板；模板刷新后具有稳定 Id 的选择尽量按实例作用域保留。

Width/Height 的 Auto 和 Weight 的整数权重含义不同，Weight 悬停提示现在会明确说明；属性悬停也显示默认值。

## 0.2.10 可移动的独立属性视图

“Sunfleck 元素属性”已从对象树内部拆成独立的 VS Code 视图，默认位于底部面板的 Sunfleck 标签页。对象树保留在资源管理器中。选择元素会显示属性视图并同步更新，不跳转源码。

拖动“Sunfleck 元素属性”的视图标题，可以移动到主侧边栏、辅助侧边栏或底部面板；也可以右键标题，使用 VS Code 的“移动视图”菜单选择位置。对象树可以独立移动。VS Code 负责保存布局位置，隐藏、重新打开或移动属性视图后仍显示当前选择的最新属性。

## 0.2.11 标签诊断位置

未知标签直接标记完整标签名，不再因为组件资源注册失败而标到文档第一行。范围采用 LSP 的 UTF-16 坐标，支持中文、emoji、CRLF/LF 和命名空间前缀。依赖文件错误保留相关位置，同文件路径采用规范化比较。
