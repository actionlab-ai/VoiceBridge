# VoiceBridge 设计说明

## 目标

VoiceBridge 不是传统 Windows IME，而是一个全局语音输入助手：

```text
按住热键录音 → 调用 ASR 服务 → 文本后处理 → 粘贴到当前输入框
```

第一阶段优先保证稳定可用，避免直接进入 TSF/IME 框架的复杂度。

## 组件

### Desktop Client

C# WinForms 托盘程序，负责：

- 全局键盘 Hook
- 麦克风录音
- ASR HTTP 调用
- UI 设置
- 开机自启
- 历史与日志
- 剪贴板粘贴

### ASR Backend

默认对接 OpenAI-compatible `/v1/chat/completions`，当前目标后端为 MiMo-V2.5-ASR / vLLM-Omni。

后续可以增加 Gateway：

```text
VoiceBridge.Desktop → VoiceBridge.Gateway → MiMo / SenseVoice / Whisper
```

## 配置

用户通过设置 UI 修改：

- Endpoint
- ModelName
- ApiKey
- TimeoutSeconds
- MicrophoneDeviceNumber
- HoldKey
- AutoStart
- Prompt
- MaxTokens

配置文件仍落地到 `%APPDATA%/VoiceBridge/config.json`，但不要求用户手动编辑。

## 发布

GitHub Actions 在 Windows runner 上执行：

```text
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

发布产物：`VoiceBridge-win-x64.zip`。
