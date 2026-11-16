# ChatGPT 全量备份助手

Windows 桌面备份工具，通过已登录的 Microsoft Edge 和 Tampermonkey 连接组件，把当前账号可访问的对话保存到本机。

> 初始预览版。模拟导出与断点续传测试已通过；实际账号仅完成过首批 25 条的三格式导出，200 条全量任务在限流等待期间由用户停止。尚无完整账号端到端成功验证，请查看 [验证记录](docs/validation-history.txt)。

## 下载与使用

在 [Releases](../../releases) 下载 Windows ZIP，解压后双击 `ChatGPT全量备份助手.exe`。也可以从源码构建。

1. 安装 Microsoft Edge 和 [Tampermonkey](https://www.tampermonkey.net/)，登录需要备份的 ChatGPT 账号。
2. 在助手中点击“安装 / 更新浏览器连接组件”，在 Tampermonkey 页面确认安装。请允许该脚本在 chatgpt.com 运行。
3. 选择备份根目录，设置重试间隔和次数，点击“一键导出并备份”。
4. 保持助手及任务标签页打开，等待结果；需要中止时点击“停止”。

完整操作、重试和结果判定见 [使用说明](使用说明.txt)。浏览器登录、扩展安装及可能出现的人机验证需要使用者操作。

## 导出内容

- 普通 JSON 数组文件，保留原始会话结构。
- Markdown ZIP，图片单独保存于 assets 并使用相对引用。
- Markdown ZIP，图片以 base64 嵌入 Markdown。
- 每批最多 25 条，附会话清单；保留可导出的思考内容、来源、时间戳和四项元数据。
- 合并普通及项目对话，按 ID 去重；同日重复备份使用独立文件名。
- HTTP 错误和限流交由桌面程序按设置重试，复用本次已完成分卷及缓存。

备份按新加坡日期存放。每类实际会话数量及清单核对通过后才显示全部成功。已删除、临时或无权读取的对话不能恢复；只保存网页接口实际提供的内容。

## 从源码构建

需要 Windows、系统 .NET Framework C# 编译器与 Windows PowerShell。引擎和 JSZip 已随源码固定，无须 npm 安装。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\程序源码\build.ps1
```

输出为仓库根目录 `ChatGPT全量备份助手.exe`。构建脚本同时生成已嵌入 EXE 的 `程序源码/bridge.user.js`。

## 检查

导出引擎测试另需 Node.js 22 或更新版本：

```powershell
node .\程序源码\test-engine.cjs
$p = Start-Process -FilePath .\ChatGPT全量备份助手.exe -ArgumentList '--self-test' -PassThru -Wait -WindowStyle Hidden
if ($p.ExitCode -ne 0) { throw 'Self-test failed' }
```

测试使用合成会话，不访问真实账号。覆盖两种图片模式、时间戳及元数据、同名标题、HTTP 429、JSON 已提交后失败并在新标签页恢复，以及保存数量和清单校验。

## 数据与运行边界

程序只在 `127.0.0.1:18746` 接收浏览器任务数据，不提供远程备份服务。ChatGPT 和图片资源仍通过浏览器访问原服务。备份、缓存和日志包含私人内容，请保存在自己的设备上；本仓库只发布程序与合成测试。

默认最多读取 20000 条对话。网页内部接口可能变化；限流或图片下载失败会阻止完成。本工具未进行完整安全审计，暂不保证重启程序后的跨任务续传。关闭 EXE 后不会继续自动重试。

## 来源和许可证

本项目适配代码使用 MIT 许可证。导出解析及渲染引擎来自 [pionxzh/chatgpt-exporter](https://github.com/pionxzh/chatgpt-exporter) 2.38.3，JSZip 固定为 3.9.1。保留了原始引擎、构建适配逻辑与第三方许可证，见 [来源说明](THIRD_PARTY_NOTICES.md)。本项目与 OpenAI、Microsoft、Tampermonkey 及原导出插件作者无隶属关系。
