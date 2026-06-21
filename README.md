# VoiceBridge

VoiceBridge（语音桥）是一个 Windows 全局语音输入助手：按住热键录音，松开后调用 OpenAI-compatible ASR 服务，例如 MiMo-V2.5-ASR / vLLM-Omni，然后把识别文本粘贴到当前输入框。

## 功能

- Windows 托盘常驻
- 全局按住说话热键，默认 `F8`
- 设置保存后热加载：热键、服务地址、模型名、API Key、超时时间、麦克风等不需要重启
- 录音 / 识别 / 成功 / 失败状态悬浮窗，类似语音输入法的小反馈 UI
- 托盘、窗口和发布 exe 图标
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
- 是否显示语音状态浮窗

## 使用方式

- 按住 `F8`：开始录音，屏幕底部会显示“正在录音”
- 松开 `F8`：停止录音并调用 ASR，状态窗显示“正在识别”
- 识别成功：自动粘贴到当前输入框，并显示“已输入”
- `Esc`：取消本次录音
- `F9`：重新粘贴上次结果

> 保存设置后立即生效，不需要重启程序。低层键盘 Hook 会读取当前内存中的配置对象。

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
