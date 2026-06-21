# VoiceBridge

VoiceBridge（语音桥）是一个 Windows 全局语音输入助手：按住热键录音，松开后调用 OpenAI-compatible ASR 服务，例如 MiMo-V2.5-ASR / vLLM-Omni，然后把识别文本粘贴到当前输入框。

## 功能

- Windows 托盘常驻
- 全局按住说话热键，默认 `F8`
- `F9` 重新粘贴上一次识别结果
- `Esc` 取消当前录音
- `Ctrl+Alt+R` 释放卡住的 Ctrl / Alt / Shift / Win 修饰键
- 麦克风选择
- 服务地址、模型名、API Key、超时时间通过 UI 修改
- 开机自启
- 识别历史
- 日志查看
- GitHub Actions 构建 Windows 发布包

## 快速开始

```powershell
# Windows + .NET 8 SDK
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1
.\artifacts\publish\VoiceBridge.exe
```

首次启动后，在托盘菜单打开 **设置**，填写：

- 服务地址：例如 `http://你的服务器IP:8004`
- 模型名称：默认 `mimo-v2.5-asr`
- API Key：没有鉴权可留空
- 超时时间：建议 60～180 秒
- 麦克风设备
- 热键

## ASR 接口

客户端默认调用：

```text
POST {Endpoint}/v1/chat/completions
```

请求使用 OpenAI-compatible 多模态格式：`audio_url` + base64 wav data URL。

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
