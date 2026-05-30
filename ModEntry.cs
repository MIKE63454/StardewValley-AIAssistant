using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace AIAssistant
{
    public class ModEntry : Mod
    {
        private ModConfig _config = null!;
        private AIService _aiService = null!;
        private List<ChatMessage> _conversationHistory = new();
        private bool _isProcessing;
        private static ModEntry? _instance;
        private bool _dailyTipShownToday;
        private object? _gmcmApi;

        // Batch merge: queue messages within a short window
        private string? _pendingMessage;
        private string? _pendingPlayerName;
        private DateTime _lastMessageTime;
        private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(1500);

        public static ModEntry? Instance => _instance;

        public override void Entry(IModHelper helper)
        {
            _instance = this;
            _config = helper.ReadConfig<ModConfig>();
            _aiService = new AIService(_config, Monitor);

            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
            helper.Events.GameLoop.SaveCreated += (s, e) => helper.WriteConfig(_config);
            helper.Events.GameLoop.Saving += OnSaving;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.DayEnding += OnDayEnding;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

            ApplyHarmonyPatch();

            Monitor.Log("AI Assistant ready. Press K for config.", LogLevel.Info);
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e) { SetupGMCM(); }

        private void ApplyHarmonyPatch()
        {
            var harmony = new Harmony(ModManifest.UniqueID);
            var method = typeof(ChatBox).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "receiveChatMessage");
            if (method != null)
            {
                var sig = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name));
                Monitor.Log("Patching receiveChatMessage(" + sig + ")", LogLevel.Info);
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ModEntry), nameof(Prefix_ReceiveChatMessage)));
                Monitor.Log("Harmony OK", LogLevel.Info);
            }
            else Monitor.Log("FATAL: receiveChatMessage not found!", LogLevel.Error);
        }

        private static void Prefix_ReceiveChatMessage(object __instance, object[] __args)
        {
            if (_instance == null) return;
            try
            {
                string? text = null; int chatKind = -1; long sourceFarmer = 0;

                if (__args.Length >= 4 && __args[0] is long fid && __args[1] is int knd && __args[3] is string txt)
                { sourceFarmer = fid; chatKind = knd; text = txt; }
                else if (__args.Length >= 3 && __args[0] is long fid2 && __args[1] is int knd2)
                {
                    sourceFarmer = fid2; chatKind = knd2;
                    var m = __args[2];
                    if (m is string s) text = s;
                    else if (m != null) text = m.GetType().GetProperty("Text")?.GetValue(m) as string;
                }
                else if (__args.Length >= 2 && __args[0] is string msg) { text = msg; chatKind = 0; }

                if (string.IsNullOrWhiteSpace(text)) return;
                if (chatKind != 0 && __args.Length >= 3) return;
                if (!_instance._config.Enabled || _instance._isProcessing) return;
text = text.Trim();

                if (text.StartsWith("/ai", StringComparison.OrdinalIgnoreCase))
                { _ = _instance.HandleCommandAsync(text); return; }
                if (text.StartsWith(_instance._config.NamePrefix, StringComparison.OrdinalIgnoreCase)) return;

                if (!string.IsNullOrEmpty(_instance._config.TriggerPrefix))
                {
                    if (!text.StartsWith(_instance._config.TriggerPrefix, StringComparison.OrdinalIgnoreCase)) return;
                    text = text.Substring(_instance._config.TriggerPrefix.Length).Trim();
                    if (string.IsNullOrEmpty(text)) return;
                }
                string pn = "Farmer";
                var f = Game1.GetPlayer(sourceFarmer);
                if (f != null && !string.IsNullOrWhiteSpace(f.Name)) pn = f.Name;

                // Batch merge: if AI is processing, queue; otherwise check timing
                if (_instance._isProcessing)
                {
                    _instance.EnqueueMessage(text, pn);
                    return;
                }
                var now = DateTime.UtcNow;
                if (_instance._pendingMessage != null && (now - _instance._lastMessageTime) < BatchWindow)
                {
                    _instance.EnqueueMessage(text, pn);
                    return;
                }
                _instance._pendingMessage = null;
                _instance._pendingPlayerName = null;
                _ = _instance.ProcessMessageAsync(text, pn);
            }
            catch (Exception ex) { _instance.Monitor.Log("Prefix: " + ex.Message, LogLevel.Error); }
        }

        private void EnqueueMessage(string text, string playerName)
        {
            if (_pendingMessage == null)
            {
                _pendingMessage = text;
                _pendingPlayerName = playerName;
            }
            else
            {
                _pendingMessage += "\n\n[continued] " + text;
                if (_pendingPlayerName != playerName && !string.IsNullOrEmpty(playerName))
                    _pendingPlayerName += ", " + playerName;
            }
            _lastMessageTime = DateTime.UtcNow;
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!_isProcessing || _pendingMessage == null) return;
            // Messages queued while processing - they'll be handled when AI finishes
        }

        private async Task HandleCommandAsync(string raw)
        {
            try
            {
                var p = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) { ShowChat(_config.NamePrefix + " /ai help|clear|toggle|status|model|preset|context|tips", Color.Gold); return; }
                switch (p[1].ToLowerInvariant())
                {
                    case "help": ShowChat(_config.NamePrefix + " /ai help|clear|toggle|status|model|preset|context|tips", Color.Gold); break;
                    case "clear": _conversationHistory.Clear(); ShowChat(_config.NamePrefix + " 记忆已清除", Color.Lime); break;
                    case "toggle": _config.Enabled = !_config.Enabled; Helper.WriteConfig(_config); ShowChat(_config.NamePrefix + " " + (_config.Enabled ? "已开启" : "已关闭"), Color.Lime); break;
                    case "status": ShowChat(_config.NamePrefix + " " + _config.Provider + "|" + _config.Model + "|" + (_config.Enabled ? "ON" : "OFF") + "|历史:" + _conversationHistory.Count, Color.Gold); break;
                    case "model":
                        if (p.Length < 3) { ShowChat(_config.NamePrefix + " 模型: " + _config.Model, Color.Gold); return; }
                        _config.Model = string.Join(" ", p.Skip(2)); Helper.WriteConfig(_config); _aiService.UpdateConfig(_config);
                        ShowChat(_config.NamePrefix + " 模型已切换: " + _config.Model, Color.Lime); break;
                    case "preset":
                        if (p.Length < 3) { foreach (var x in ModConfig.Presets) ShowChat("  " + x.Name, Color.White); }
                        else { var n = string.Join(" ", p.Skip(2)); var x = ModConfig.Presets.FirstOrDefault(y => y.Name.Contains(n, StringComparison.OrdinalIgnoreCase)); if (x == null) ShowChat(_config.NamePrefix + " 未找到预设", Color.Orange); else { _config.Provider = x.Provider; _config.ApiEndpoint = x.Endpoint; _config.Model = x.Model; Helper.WriteConfig(_config); _aiService.UpdateConfig(_config); ShowChat(_config.NamePrefix + " 已切换: " + x.Name, Color.Lime); } }
                        break;
                    case "context": _config.InjectGameContext = !_config.InjectGameContext; Helper.WriteConfig(_config); ShowChat(_config.NamePrefix + " 上下文: " + (_config.InjectGameContext ? "ON" : "OFF"), Color.Lime); break;
                    case "tips": _config.DailyTips = !_config.DailyTips; Helper.WriteConfig(_config); ShowChat(_config.NamePrefix + " 每日提示: " + (_config.DailyTips ? "ON" : "OFF"), Color.Lime); break;
                    default: ShowChat(_config.NamePrefix + " 未知命令. /ai help", Color.Orange); break;
                }
            }
            catch (Exception ex) { Monitor.Log("CMD: " + ex.Message, LogLevel.Error); }
        }

        private async Task ProcessMessageAsync(string message, string playerName)
        {
            if (_isProcessing) return; _isProcessing = true;
            try
            {
                if (string.IsNullOrWhiteSpace(_config.ApiKey)) { ShowChat(_config.NamePrefix + " 请设置API密钥! 按K打开设置.", Color.Orange); return; }

                // Auto-tone detection
                if (_config.AutoTone)
                {
                    var detected = DetectTone(message);
                    _config.Tone = detected;
                    _aiService.UpdateConfig(_config);
                }

                _conversationHistory.Add(new ChatMessage("user", message, playerName));

                // Auto-summarize: compress old messages if history is long
                if (_conversationHistory.Count > 30)
                    await SummarizeHistoryAsync();

                ShowChat(_config.NamePrefix + " ...", Color.Gray);

                var fullResponse = await _aiService.SendChatAsync(_conversationHistory);
                if (fullResponse == null) { ShowChat(_config.NamePrefix + " API请求失败", Color.Red); return; } else { Monitor.Log("Process: Got response, length=" + fullResponse.Length, LogLevel.Debug); }

                fullResponse = Sanitize(fullResponse);
                _conversationHistory.Add(new ChatMessage("assistant", fullResponse));

                // Display the response in chat
                ShowChat(_config.NamePrefix + " " + fullResponse, Color.Gold);

                // Trim history
                if (_conversationHistory.Count > _config.HistoryLength * 2)
                    _conversationHistory = _conversationHistory.Skip(_conversationHistory.Count - _config.HistoryLength).ToList();

                // Broadcast in multiplayer
                if (Context.IsMultiplayer && Game1.player != null)
                    Helper.Multiplayer.SendMessage(new AIReplyMessage { Text = fullResponse, FromPlayerId = Game1.player.UniqueMultiplayerID, FromPlayerName = Game1.player.Name, NamePrefix = _config.NamePrefix }, "AIReply", modIDs: new[] { ModManifest.UniqueID });

                // Process queued messages
                if (_pendingMessage != null)
                {
                    var qm = _pendingMessage;
                    var qn = _pendingPlayerName ?? "Farmer";
                    _pendingMessage = null; _pendingPlayerName = null;
                    _ = ProcessMessageAsync(qm, qn);
                }
            }
            catch (Exception ex) { Monitor.Log("Process: " + ex.Message, LogLevel.Error); ShowChat(_config.NamePrefix + " 处理出错", Color.Red); }
            finally { if (_pendingMessage == null) _isProcessing = false; }
        }

        private async Task SummarizeHistoryAsync()
        {
            try
            {
                // Keep last 20 messages, summarize the rest
                var toKeep = _conversationHistory.Skip(Math.Max(0, _conversationHistory.Count - 20)).ToList();
                var toSummarize = _conversationHistory.Take(Math.Max(0, _conversationHistory.Count - 20)).ToList();
                if (toSummarize.Count == 0) return;

                var summaryText = string.Join("\n", toSummarize.Select(m => (m.Role == "user" ? "玩家" : "AI") + ": " + m.Content));
                var prompt = "请将以下对话压缩为一句话摘要（中文，50字以内），只输出摘要：\n" + summaryText;

                var summary = await _aiService.SendChatAsync(new List<ChatMessage> { new ChatMessage("user", prompt) });
                if (!string.IsNullOrEmpty(summary))
                    _conversationHistory = new List<ChatMessage> { new ChatMessage("system", "[对话摘要] " + summary) }.Concat(toKeep).ToList();
                else
                    _conversationHistory = toKeep;

                Monitor.Log("History summarized. Now: " + _conversationHistory.Count + " messages", LogLevel.Debug);
            }
            catch { }
        }

        private static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
            text = Regex.Replace(text, @"\*(.+?)\*", "$1");
            text = Regex.Replace(text, @"__(.+?)__", "$1");
            text = Regex.Replace(text, @"_(.+?)_", "$1");
            text = Regex.Replace(text, @"`+(.+?)`+", "$1");
            text = Regex.Replace(text, @"~~(.+?)~~", "$1");
            text = Regex.Replace(text, @":[a-z_]+:", "");
            return text.Trim();
        }

        private static List<string> Split(string t, int m)
        {
            if (t.Length <= m) return new List<string> { t };
            var r = new List<string>();
            var s = t.Split(new[] { ". ", "! ", "? ", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var c = "";
            foreach (var x in s) { var y = x.Trim(); if (y.Length == 0) continue; if (c.Length + y.Length + 2 > m) { if (c.Length > 0) r.Add(c.Trim()); c = y; } else c += (c.Length > 0 ? ". " : "") + y; }
            if (c.Length > 0) r.Add(c.Trim());
            return r.Count > 0 ? r : new List<string> { t };
        }

        private void OnSaving(object? sender, SavingEventArgs e)
        {
            Helper.WriteConfig(_config);
            var data = new ConversationSaveData
            {
                Messages = _conversationHistory.Select(m => new ChatMessageData
                {
                    Role = m.Role,
                    Content = m.Content,
                    PlayerName = m.PlayerName
                }).ToList()
            };
            Helper.Data.WriteSaveData("ai-conversation", data);
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            try
            {
                var data = Helper.Data.ReadSaveData<ConversationSaveData>("ai-conversation");
                if (data?.Messages != null)
                {
                    _conversationHistory = data.Messages
                        .Select(m => new ChatMessage(m.Role, m.Content, m.PlayerName))
                        .Take(_config.HistoryLength)
                        .ToList();
                    Monitor.Log("Loaded " + _conversationHistory.Count + " conversation messages from save.", LogLevel.Debug);
                }
                else _conversationHistory.Clear();
            }
            catch (Exception ex) { Monitor.Log("Failed to load conversation: " + ex.Message, LogLevel.Debug); _conversationHistory.Clear(); }
        }

        private static AITone DetectTone(string msg)
        {
            var lower = msg.ToLower();
            if (lower.Contains("?") || lower.Contains("怎么") || lower.Contains("如何") || lower.Contains("攻略") || lower.Contains("多少") || lower.Contains("哪里") || lower.Contains("什么")) return AITone.Professional;
            if (lower.Contains("哈哈") || lower.Contains("笑") || lower.Contains("搞笑") || lower.Contains("逗")) return AITone.Humorous;
            if (lower.Contains("累") || lower.Contains("难") || lower.Contains("烦") || lower.Contains("哭") || lower.Contains("伤心") || lower.Contains("安慰")) return AITone.Warm;
            if (lower.Contains("哼") || lower.Contains("讨厌") || lower.Contains("才不")) return AITone.Tsundere;
            return _instance!._config.Tone;
        }

        private void OnDayEnding(object? sender, DayEndingEventArgs e)
        {
            Helper.WriteConfig(_config);
            if (_config.DailyTips && _config.Enabled && !string.IsNullOrWhiteSpace(_config.ApiKey) && Game1.player != null)
                _ = AutoDiaryAsync();
        }

        private async Task AutoDiaryAsync()
        {
            try
            {
                var ctx = _aiService.BuildGameContext();
                var prompt = "请根据以上游戏状态，用中文写一段今天在鹈鹕镇的简短日记（80字以内，第一人称，温馨口吻，总结今天的收获和心情）。只输出日记内容。";
                var r = await _aiService.SendChatAsync(new List<ChatMessage> { new ChatMessage("user", prompt + "\n\n" + ctx) });
                if (r != null)
                {
                    try { System.IO.File.AppendAllText(System.IO.Path.Combine(Helper.DirectoryPath, "diary.txt"), "\n\n=== 第" + Game1.player?.yearForSaveGame + "年 " + Game1.currentSeason + "第" + Game1.player?.dayOfMonthForSaveGame + "天 ===\n" + r); }
                    catch { }
                }
            }
            catch { }
        }

        private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            if ((e.Type != "AIReply" && e.Type != "AIDailyTip") || e.FromModID != ModManifest.UniqueID) return;
            var m = e.ReadAs<AIReplyMessage>(); if (m == null) return;
            ShowChat(m.NamePrefix + " " + m.Text, Color.Gold);
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            _dailyTipShownToday = false;
            if (!_config.DailyTips || !_config.Enabled || string.IsNullOrWhiteSpace(_config.ApiKey)) return;
            _ = ShowDailyTipAsync();
        }

        private async Task ShowDailyTipAsync()
        {
            if (_dailyTipShownToday) return; _dailyTipShownToday = true;
            await Task.Delay(2000);
            var t = await _aiService.GetDailyTipAsync();
            if (!string.IsNullOrEmpty(t)) { ShowChat(_config.NamePrefix + " " + t, Color.LightGoldenrodYellow); }
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsPlayerFree || e.Button != _config.ConfigKey) return;
            Helper.Input.Suppress(e.Button); OpenConfigMenu();
        }

        // ============================== GMCM ==============================
        private void SetupGMCM()
        {
            try
            {
                _gmcmApi = Helper.ModRegistry.GetApi("spacechase0.GenericModConfigMenu");
                if (_gmcmApi == null) { Monitor.Log("GMCM not installed.", LogLevel.Debug); return; }
                var t = _gmcmApi.GetType();

                GmcmCall(t, "Register", null, ModManifest,
                    new Action(() => _config = new ModConfig()),
                    new Action(() => { Helper.WriteConfig(_config); _aiService.UpdateConfig(_config); }),
                    false);

                GmcmCall(t, "AddSectionTitle", null, ModManifest, F("API 配置"), F(""));
                GmcmCall(t, "AddTextOption", null, ModManifest, F(() => _config.ApiKey), A<string>(v => _config.ApiKey = v), F("API 密钥"), F("AI 服务的 API 密钥"), null, null, "");
                GmcmCall(t, "AddTextOption", null, ModManifest, F(() => _config.ApiEndpoint), A<string>(v => _config.ApiEndpoint = v), F("API 端点"), F("API 基础 URL 地址"), null, null, "");
                GmcmCall(t, "AddTextOption", null, ModManifest, F(() => _config.Provider.ToString()), A<string>(v => { if (Enum.TryParse<AIProvider>(v, out var x)) _config.Provider = x; }), F("AI 提供商"), F("选择 AI 服务提供商"), new[] { "OpenAI", "Claude", "Gemini", "AzureOpenAI", "Custom" }, null, "");
                GmcmCall(t, "AddTextOption", null, ModManifest, F(() => _config.Model), A<string>(v => _config.Model = v), F("模型"), F("模型名称"), null, null, "");

                GmcmCall(t, "AddSectionTitle", null, ModManifest, F("语气风格"), F(""));
                GmcmCall(t, "AddTextOption", null, ModManifest, F(() => _config.Tone.ToString()), A<string>(v => { if (Enum.TryParse<AITone>(v, out var x)) _config.Tone = x; }), F("语气预设"), F("AI 回复的语气风格"), new[] { "Friendly", "Professional", "Humorous", "Warm", "Tsundere" }, null, "");
                GmcmCall(t, "AddBoolOption", null, ModManifest, F(() => _config.AutoTone), A<bool>(v => _config.AutoTone = v), F("自动语气"), F("根据消息内容自动切换语气"), "");

                GmcmCall(t, "AddSectionTitle", null, ModManifest, F("聊天行为"), F(""));
                GmcmCall(t, "AddBoolOption", null, ModManifest, F(() => _config.Enabled), A<bool>(v => _config.Enabled = v), F("启用 AI"), F("开关 AI 自动回复"), "");
                GmcmCall(t, "AddTextOption", null, ModManifest, F(() => _config.TriggerPrefix), A<string>(v => _config.TriggerPrefix = v), F("触发前缀"), F("以此前缀开头才触发 AI（留空=回复所有）"), null, null, "");
                GmcmCall(t, "AddTextOption", null, ModManifest, F(() => _config.NamePrefix), A<string>(v => _config.NamePrefix = v), F("AI 显示名称"), F("AI 在聊天中的前缀"), null, null, "");
                GmcmCall(t, "AddNumberOption", IntTypes, ModManifest, F(() => _config.HistoryLength), A<int>(v => _config.HistoryLength = v), F("对话记忆"), F("保留的对话历史条数"), 0, 100, 5, null, "");
                GmcmCall(t, "AddBoolOption", null, ModManifest, F(() => _config.InjectGameContext), A<bool>(v => _config.InjectGameContext = v), F("游戏上下文"), F("让 AI 知道当前游戏状态"), "");
                GmcmCall(t, "AddBoolOption", null, ModManifest, F(() => _config.DailyTips), A<bool>(v => _config.DailyTips = v), F("每日提示 & 日记"), F("每天清晨 AI 给出建议，睡前自动写日记"), "");

                GmcmCall(t, "AddSectionTitle", null, ModManifest, F("高级"), F(""));
                GmcmCall(t, "AddNumberOption", IntTypes, ModManifest, F(() => _config.MaxTokens), A<int>(v => _config.MaxTokens = v), F("最大 Token"), F("AI 回复最大长度（50-4096）"), 50, 4096, 50, null, "");
                GmcmCall(t, "AddNumberOption", FloatTypes, ModManifest, F(() => _config.Temperature), A<float>(v => _config.Temperature = v), F("温度"), F("0=严谨 2=创意"), 0f, 2f, 0.1f, null, "");
                GmcmCall(t, "AddKeybind", null, ModManifest, F(() => _config.ConfigKey), A<SButton>(v => _config.ConfigKey = v), F("快捷键"), F("打开配置的按键"), "");
                GmcmCall(t, "AddBoolOption", null, ModManifest, F(() => _config.DebugMode), A<bool>(v => _config.DebugMode = v), F("调试模式"), F("在控制台显示调试信息"), "");

                Monitor.Log("GMCM setup complete.", LogLevel.Info);
            }
            catch (Exception ex) { Monitor.Log("GMCM FAIL: " + ex, LogLevel.Error); _gmcmApi = null; }
        }

        private void OpenConfigMenu()
        {
            if (_gmcmApi != null)
            {
                try { _gmcmApi.GetType().GetMethod("OpenModMenu")?.Invoke(_gmcmApi, new object[] { ModManifest }); return; }
                catch { }
            }
            ShowChat(_config.NamePrefix + " 请安装 Generic Mod Config Menu 来使用配置界面", Color.Orange);
        }

        private static readonly Type[] IntTypes = { typeof(IManifest), typeof(Func<int>), typeof(Action<int>), typeof(Func<string>), typeof(Func<string>), typeof(int?), typeof(int?), typeof(int?), typeof(Func<int, string>), typeof(string) };
        private static readonly Type[] FloatTypes = { typeof(IManifest), typeof(Func<float>), typeof(Action<float>), typeof(Func<string>), typeof(Func<string>), typeof(float?), typeof(float?), typeof(float?), typeof(Func<float, string>), typeof(string) };

        private static Func<T> F<T>(Func<T> f) => f;
        private static Func<string> F(string s) => () => s;
        private static Action<T> A<T>(Action<T> a) => a;

        private void GmcmCall(Type apiType, string methodName, Type[]? paramTypes, params object?[] args)
        {
            try
            {
                MethodInfo? method;
                if (paramTypes != null) method = apiType.GetMethod(methodName, paramTypes);
                else
                {
                    var methods = apiType.GetMethods().Where(m => m.Name == methodName).ToList();
                    method = methods.OrderByDescending(m => m.GetParameters().Length).FirstOrDefault(m => m.GetParameters().Length >= args.Length);
                }
                if (method == null) return;

                var pars = method.GetParameters();
                var final = new object?[pars.Length];
                int n = Math.Min(args.Length, pars.Length);
                for (int i = 0; i < n; i++) final[i] = args[i];
                for (int i = n; i < pars.Length; i++)
                {
                    if (pars[i].HasDefaultValue) final[i] = pars[i].DefaultValue;
                    else if (pars[i].ParameterType == typeof(string)) final[i] = null;
                    else if (pars[i].ParameterType.IsValueType) final[i] = Activator.CreateInstance(pars[i].ParameterType);
                    else final[i] = null;
                }
                method.Invoke(_gmcmApi!, final);
            }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        public static void ShowChat(string msg, Color? c = null)
        {
            if (Game1.chatBox == null) return;
            try { Game1.chatBox.addMessage(msg, c ?? Color.White); } catch { }
        }
    }

    public class AIReplyMessage
    {
        public string Text { get; set; } = "";
        public long FromPlayerId { get; set; }
        public string FromPlayerName { get; set; } = "";
        public string NamePrefix { get; set; } = "[AI]";
    }
    internal class ConversationSaveData
    {
        public List<ChatMessageData> Messages { get; set; } = new();
    }
    internal class ChatMessageData
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = "";
        public string? PlayerName { get; set; }
    }
}
