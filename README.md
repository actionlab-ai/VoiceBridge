# VoiceBridge

VoiceBridge（语音桥）是一个 Windows 全局语音输入助手：按住热键录音，松开后调用 OpenAI-compatible ASR 服务，例如 MiMo-V2.5-ASR / vLLM-Omni，然后把识别文本粘贴到当前输入框。

## 功能

- Windows 托盘常驻
- 全局按住说话热键，默认 `F8`
- 设置保存后热加载：热键、服务地址、模型名、API Key、超时时间、麦克风等不需要重启
- 录音 / 编码 / 请求模型 / 等待模型 / 解析 / 粘贴全流程状态悬浮窗
- 日志查看窗口实时刷新，不需要关闭后重新打开
- 服务地址支持填写 `IP:端口`，会自动补 `http://`
- 托盘、窗口和发布 exe 图标
- `F9` 重新粘贴上一次识别结果
- `Esc` 取消当前录音或正在进行的识别请求
- `Ctrl+Alt+R` 释放卡住的 Ctrl / Alt / Shift / Win 修饰键
- 麦克风选择
- 服务地址、模型名、API Key、超时时间通过 UI 修改
- 开机自启
- 识别历史
- GitHub Actions 构建 Windows 发布包

## 快速开始

```powershell
# Windows + .NET 8 SDK
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1
.\artifacts\publish\VoiceBridge.exe
```

首次启动后，在托盘菜单打开 **设置**，填写：

- 服务地址：例如 `36.147.35.14:30081` 或 `http://36.147.35.14:30081`
- 模型名称：默认 `mimo-v2.5-asr`
- API Key：没有鉴权可留空
- 超时时间：MiMo 首次请求或远端网关较慢时建议 120～300 秒
- 麦克风设备
- 热键
- 是否显示语音状态浮窗

> 服务地址只填基础地址，不要填写 `/v1/chat/completions`。客户端会自动请求 `{Endpoint}/v1/chat/completions`。

## 使用方式

- 按住 `F8`：开始录音，屏幕底部会显示“正在录音”
- 松开 `F8`：停止录音并调用 ASR，状态窗显示编码、请求模型、等待模型、解析、粘贴等阶段和耗时
- 识别成功：自动粘贴到当前输入框，并显示“已输入”
- `Esc`：取消当前录音；识别请求进行中也会尝试取消
- `F9`：重新粘贴上次结果

> 保存设置后立即生效，不需要重启程序。低层键盘 Hook 会读取当前内存中的配置对象。

## ASR 接口

客户端默认调用：

```text
POST {Endpoint}/v1/chat/completions
```

请求使用 OpenAI-compatible 多模态格式：`audio_url` + base64 wav data URL。

## 常见问题

### 服务地址怎么填？

可以填：

```text
36.147.35.14:30081
http://36.147.35.14:30081
https://your-domain.example.com
```

不要填：

```text
http://36.147.35.14:30081/v1/chat/completions
```

程序保存设置时会自动归一化为基础地址。

### 127.0.0.1:8004 连接被拒绝

`127.0.0.1` 表示 VoiceBridge 所在的 Windows 本机。如果 MiMo ASR 跑在远端服务器，需要填写远端服务器 IP 和端口，例如 `36.147.35.14:30081`。

### 502 Bad Gateway

502 通常不是客户端格式问题，而是中间的 Nginx / 网关 / NodePort / vLLM 服务返回异常。请检查：

- vLLM-Omni 容器是否仍在运行
- `/v1/models` 是否可访问
- 反向代理超时时间是否过短
- MiMo 第一次请求是否还在 warmup
- 是否有多个请求并发打到单卡模型

### 识别慢怎么办？

先看 VoiceBridge 日志里的阶段耗时：

- 录音时长
- 音频文件大小
- base64 payload 大小
- HTTP 请求耗时
- ASR 返回状态码
- 总耗时

短句仍然慢时，优先检查服务端 MiMo/vLLM 日志和反向代理超时时间。

## 发布

推送 tag 即可触发 GitHub Release：

```powershell
git tag v0.1.0
git push origin v0.1.0
```

也可以在 GitHub Actions 页面手动运行 `Build and Release` workflow。

## 项目结构

```text
VoiceBridge/
  apps/VoiceBridge.Desktop/      # C# WinForms 桌面客户端
  docs/                          # 设计文档
  scripts/                       # 构建脚本
  .github/workflows/             # CI / Release
```
