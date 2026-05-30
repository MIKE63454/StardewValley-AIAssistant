# AI Assistant - 星露谷物语 AI 助手

[![SMAPI](https://img.shields.io/badge/SMAPI-4.0+-blue)](https://smapi.io)
[![SDV](https://img.shields.io/badge/Stardew%20Valley-1.6-green)](https://www.stardewvalley.net)

在星露谷物语聊天框中接入 AI，让 AI 成为你的游戏伙伴。支持 OpenAI、Claude、Gemini、Azure 及任何兼容 OpenAI 协议的 API（DeepSeek、Groq、Ollama 等）。

---

## 功能

### AI 聊天
- 💬 **自然对话** — 直接在游戏聊天框（按 T）中与 AI 对话，AI 回复显示在聊天框
- 🎭 **5 种语气预设** — 友好 / 专业 / 幽默 / 暖心 / 傲娇，可在配置界面切换
- 🤖 **自动语气** — 根据你输入的内容自动适配语气（问攻略→专业，抱怨→暖心）
- 📏 **动态回复长度** — 闲聊 30 字精简，攻略问题可到 150 字

### 模型支持
- **OpenAI** — GPT-4o, GPT-4o-mini, GPT-4 等
- **Anthropic Claude** — Claude 3.5 Sonnet, Claude 3.5 Haiku
- **Google Gemini** — Gemini 2.0 Flash, Gemini 2.5 Pro
- **Azure OpenAI** — 企业级部署
- **自定义** — DeepSeek, Groq, Ollama, LM Studio 等任何 OpenAI 兼容 API
- 📋 **预设切换** — `/ai preset` 命令快速切换模型（含 11 个预设）

### 游戏感知（AI 知道你的游戏状态）
| 类别 | 信息 |
|------|------|
| 📅 日期天气 | 季节、日期、年份、天气、运势、时间、金币 |
| 📍 位置 | 当前地图、坐标、矿洞深度 |
| 🎒 背包 | 全部物品（含分类：矿石/作物/武器…） |
| 🔧 工具 | 各工具等级（基础→铜→钢→金→铱）、升级中状态 |
| 🌱 作物 | 各株作物名称、售价、生长进度、剩余天数 |
| ⚡ 技能 | 5 项技能等级 + 精确经验百分比 |
| 📋 任务 | 进行中的普通任务 + 特殊订单 |
| 💕 村民 | 好感度 ≥ 2 心的 NPC（含婚姻/约会状态） |
| 🏠 农场 | 建筑列表 + 动物种类数量 |
| 🏛️ 社区中心 | 6 个房间各完成进度（茶水间/工艺室/鱼缸/锅炉室/布告栏/金库） |
| 🏛️ 博物馆 | 已捐赠矿物 + 文物数量 |
| 🎂 生日 | 今天 + 明天生日的 NPC（提醒准备礼物） |
| 🎉 节日 | 7 天内即将到来的节日 |
| 🐟 钓鱼 | 当前地图可钓鱼种 |
| 🏪 商店 | 当前时间营业的店铺 |
| 🧍 NPC | 当前地图在线的村民 |
| ⛏️ 矿洞 | 矿洞怪物预警 + 推荐武器 |
| 🎁 增益 | 食物/咖啡当前的 buff 效果 |
| 🔓 解锁 | 温室/沙漠/姜岛/下水道等已解锁区域 |
| 📺 电视 | 今日节目表（酱料女王新菜/重播/农牧技巧） |
| 👨‍🍳 配方 | 已会烹饪 + 制造配方数量 |
| ⚔️ 公会 | 冒险家公会讨伐进度 |
| 👥 联机 | 在线玩家列表、位置、体力、房主身份 |

### 智能优化
- 🔍 **Wiki 搜索** — 自动搜索星露谷官方 Wiki（缓存在 `wikicache.json`）
- 📚 **本地知识库** — 15+ 条热门攻略预载，0 延迟命中
- 🗜️ **场景裁剪** — 闲聊仅发基础信息（省 Token），攻略问题发完整上下文
- 📝 **自动摘要** — 对话超 30 条自动压缩历史，保持精准 + 低 Token
- 🔗 **批量合并** — 连续快速发送的消息合并为单次 API 调用
- 💾 **按存档记忆** — 对话历史保存在存档中，读档恢复

### 联机
- 👥 **房主统一调用** — 只需房主配置 API Key，所有联机玩家共用
- 🧑‍🤝‍🧑 **独立对话记忆** — 每个玩家的对话历史互不干扰
- 🔄 **配置同步** — 房主改动设置自动推送给所有客户端
- 📡 **广播回复** — AI 回复对所有玩家可见

### 额外功能
- 🌅 **每日建议** — 每天清晨 AI 结合所有状态给出一日最优路线
- 📖 **AI 日记** — 每晚自动生成日记，保存为 `diary.txt`
- ⌨️ **快捷键 K** — 一键打开 GMCM 配置界面
- ⌨️ **命令系统** — `/ai help|clear|toggle|status|model|preset`

---

## 安装

### 前置
- [SMAPI](https://smapi.io) ≥ 4.0.0
- [Stardew Valley](https://www.stardewvalley.net) ≥ 1.6.0
- （推荐）[Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) — 提供游戏内配置界面

### 安装步骤
1. 下载 `AI Assistant` 模组
2. 解压到 `Stardew Valley/Mods/` 目录
3. 启动游戏（通过 SMAPI）
4. 按 **K** 打开配置界面（或通过 GMCM 设置页面）
5. 填入 API Key、选择模型
6. 按 **T** 打开聊天框，开始对话！

---

## 配置

### 通过 GMCM（推荐）
游戏内按 K 键，或在主菜单 → 设置 → Generic Mod Config Menu → AI Assistant

### 通过配置文件
编辑 `Mods/AIAssistant/config.json`：

```jsonc
{
  "Provider": "OpenAI",           // AI 提供商
  "ApiKey": "sk-...",             // API 密钥
  "ApiEndpoint": "https://api.openai.com/v1",  // API 端点
  "Model": "gpt-4o-mini",         // 模型名称
  "Enabled": true,                // 是否启用
  "Tone": "Friendly",             // 语气预设
  "AutoTone": true,               // 自动检测语气
  "MaxTokens": 500,               // 最大回复 Token
  "Temperature": 0.8,             // 创造性 (0-2)
  "NamePrefix": "[AI]",           // AI 聊天前缀
  "TriggerPrefix": "",            // 触发前缀（留空=回复全部消息）
  "HistoryLength": 20,            // 对话历史保留条数
  "InjectGameContext": true,      // 注入游戏上下文
  "DailyTips": true,              // 每日建议 + AI 日记
  "ConfigKey": "K",               // 配置快捷键
  "DebugMode": false              // 调试模式
}
```

---

## 快速切换模型

在聊天框中输入 `/ai preset` 查看所有预设，或：

```
/ai preset openai    → OpenAI GPT-4o-mini
/ai preset deepseek  → DeepSeek Chat
/ai preset ollama    → Ollama 本地模型
/ai preset claude    → Claude 3.5 Sonnet
/ai preset gemini    → Gemini 2.0 Flash
```

## 常用命令

| 命令 | 功能 |
|------|------|
| `/ai help` | 显示帮助 |
| `/ai clear` | 清除对话记忆 |
| `/ai toggle` | 开关 AI |
| `/ai status` | 显示当前状态 |
| `/ai model gpt-4o` | 切换模型 |
| `/ai preset openai` | 切换预设 |
| `/ai context` | 开关游戏上下文 |

---

## 文件结构

```
Mods/AIAssistant/
├── AIAssistant.dll       # 模组 DLL
├── manifest.json         # 模组元数据
├── ModConfig.cs          # 配置模型
├── AIService.cs          # AI 服务（多提供商、游戏上下文）
├── ModEntry.cs           # 入口（Harmony、GMCM、联机）
├── i18n/
│   └── default.json      # 中文本地化
├── config.json           # 用户配置（自动生成）
├── wikicache.json        # Wiki 缓存（自动生成）
└── diary.txt             # AI 日记（自动生成）
```

---

## 兼容性

- ✅ 单机
- ✅ 联机（房主模式）
- ✅ 与大多数模组兼容
- ⚠️ `Star Control` 用户注意：AI 问候语可能被过滤，不影响正常使用

---

## 常见问题

### Q: 按 K 打不开配置界面？
安装 [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098)。

### Q: 聊天没有 AI 回复？
1. 确认 API Key 已配置
2. 确认 `Enabled` 为 `true`
3. 如果设置了 `TriggerPrefix`，消息需要以此前缀开头
4. 查看 SMAPI 控制台是否有错误

### Q: 如何用免费模型？
- **Groq** — 免费额度，预设 `Groq`
- **Ollama** — 本地运行，完全免费
- **DeepSeek** — 极低价格，预设 `DeepSeek`

### Q: Token 消耗太快？
1. 关闭 `InjectGameContext`（不注入游戏状态）
2. 减小 `MaxTokens` 和 `HistoryLength`
3. 设置 `TriggerPrefix`（如 `!`），只有 `!` 开头的消息才触发 AI

### Q: 如何让 AI 知道更多游戏信息？
默认已注入 20+ 种游戏数据。问攻略类问题时 AI 会自动获取完整上下文。

---

## 构建

```bash
cd AIAssistant
dotnet build
# 自动部署到 Mods\AIAssistant\
```

依赖：.NET 6.0 SDK，游戏 DLL 通过 `<HintPath>` 直接引用。

---
**版本**: 1.1.0 | **作者**: Codex
