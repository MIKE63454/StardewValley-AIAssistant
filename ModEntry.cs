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
        private Dictionary<long, List<ChatMessage>> _playerHistories = new();
        private bool _isProcessing;
        private static ModEntry? _instance;
        private bool _dailyTipShownToday;
        private object? _gmcmApi;

        // Batch merge
        private string? _pendingMessage;
        private string? _pendingPlayerName;
        private long _pendingPlayerId;
        private DateTime _lastMessageTime;
        private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(1500);
        private bool _isHost => Context.IsMainPlayer || !Context.IsMultiplayer;

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
            helper.Events.Multiplayer.PeerConnected += OnPeerConnected;

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
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ModEntry), nameof(Prefix_ReceiveChatMessage)));
                Monitor.Log("Harmony OK", LogLevel.Info);
            }
            else Monitor.Log("FATAL: receiveChatMessage not found!", LogLevel.Error);
        }

        private List<ChatMessage> GetPlayerHistory(long playerId)
        {
            if (!_playerHistories.ContainsKey(playerId))
                _playerHistories[playerId] = new List<ChatMessage>();
            return _playerHistories[playerId];
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
                { sourceFarmer = fid2; chatKind = knd2; var m = __args[2]; if (m is string s) text = s; else if (m != null) text = m.GetType().GetProperty("Text")?.GetValue(m) as string; }
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

                // === Multiplayer routing ===
                if (!_instance._isHost)
                {
                    // Client: forward to host
                    _instance.Helper.Multiplayer.SendMessage(
                        new AIRequestMessage { Text = text, PlayerName = pn, PlayerId = sourceFarmer },
                        "AIRequest", modIDs: new[] { _instance.ModManifest.UniqueID });
                    return;
                }

                // Host: handle locally (or already host)
                if (_instance._isProcessing)
                {
                    _instance.EnqueueMessage(text, pn, sourceFarmer);
                    return;
                }
                var now = DateTime.UtcNow;
                if (_instance._pendingMessage != null && (now - _instance._lastMessageTime) < BatchWindow)
                {
                    _instance.EnqueueMessage(text, pn, sourceFarmer);
                    return;
                }
                _instance._pendingMessage = null;
                _ = _instance.ProcessMessageAsync(text, pn, sourceFarmer);
            }
            catch (Exception ex) { _instance.Monitor.Log("Prefix: " + ex.Message, LogLevel.Error); }
        }

        private void EnqueueMessage(string text, string playerName, long playerId)
        {
            if (_pendingMessage == null) { _pendingMessage = text; _pendingPlayerName = playerName; _pendingPlayerId = playerId; }
            else { _pendingMessage += "\n\n[续] " + text; }
            _lastMessageTime = DateTime.UtcNow;
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e) { }

        private async Task HandleCommandAsync(string raw)
        {
            try
            {
                var p = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) { ShowChat(_config.NamePrefix + " /ai help|clear|toggle|status", Color.Gold); return; }
                switch (p[1].ToLowerInvariant())
                {
                    case "help": ShowChat(_config.NamePrefix + " /ai help|clear|toggle|status", Color.Gold); break;
                    case "clear":
                        if (Game1.player != null) { _playerHistories.Remove(Game1.player.UniqueMultiplayerID); }
                        ShowChat(_config.NamePrefix + " 记忆已清除", Color.Lime); break;
                    case "toggle": _config.Enabled = !_config.Enabled; Helper.WriteConfig(_config); ShowChat(_config.NamePrefix + " " + (_config.Enabled ? "ON" : "OFF"), Color.Lime); SyncConfig(); break;
                    case "status":
                        var h = Game1.player != null ? GetPlayerHistory(Game1.player.UniqueMultiplayerID).Count : 0;
                        ShowChat(_config.NamePrefix + " " + _config.Provider + "|" + _config.Model + "|" + (_config.Enabled ? "ON" : "OFF") + "|历史:" + h, Color.Gold); break;
                    default: ShowChat(_config.NamePrefix + " /ai help", Color.Orange); break;
                }
            }
            catch (Exception ex) { Monitor.Log("CMD: " + ex.Message, LogLevel.Error); }
        }

        private async Task ProcessMessageAsync(string message, string playerName, long playerId)
        {
            if (_isProcessing) return; _isProcessing = true;
            try
            {
                if (string.IsNullOrWhiteSpace(_config.ApiKey)) { ShowChat(_config.NamePrefix + " 请设置API密钥! 按K打开设置.", Color.Orange); return; }

                if (_config.AutoTone) { _config.Tone = DetectTone(message); _aiService.UpdateConfig(_config); }

                var history = GetPlayerHistory(playerId);
                history.Add(new ChatMessage("user", message, playerName));

                if (history.Count > 30) await SummarizeHistoryAsync(history);

                ShowChat(_config.NamePrefix + " ...", Color.Gray);

                var fullResponse = await _aiService.SendChatAsync(history);
                if (fullResponse == null) { ShowChat(_config.NamePrefix + " API请求失败", Color.Red); return; }

                fullResponse = Sanitize(fullResponse);
                history.Add(new ChatMessage("assistant", fullResponse));
                ShowChat(_config.NamePrefix + " " + fullResponse, Color.Gold);
                try { Game1.playSound("newArtifact"); } catch { }

                if (history.Count > _config.HistoryLength * 2)
                {
                    var trimmed = history.Skip(history.Count - _config.HistoryLength).ToList();
                    _playerHistories[playerId] = trimmed;
                }

                // Broadcast reply to all clients
                if (Context.IsMultiplayer && _isHost && Game1.player != null)
                    Helper.Multiplayer.SendMessage(
                        new AIReplyMessage { Text = fullResponse, FromPlayerId = playerId, FromPlayerName = playerName, NamePrefix = _config.NamePrefix },
                        "AIReply", modIDs: new[] { ModManifest.UniqueID });

                if (_pendingMessage != null)
                {
                    var qm = _pendingMessage; var qn = _pendingPlayerName ?? "Farmer"; var qi = _pendingPlayerId;
                    _pendingMessage = null; _ = ProcessMessageAsync(qm, qn, qi);
                }
            }
            catch (Exception ex) { Monitor.Log("Process: " + ex.Message, LogLevel.Error); ShowChat(_config.NamePrefix + " 处理出错", Color.Red); }
            finally { if (_pendingMessage == null) _isProcessing = false; }
        }

        private async Task SummarizeHistoryAsync(List<ChatMessage> history)
        {
            try
            {
                var toKeep = history.Skip(Math.Max(0, history.Count - 20)).ToList();
                var toSummarize = history.Take(Math.Max(0, history.Count - 20)).ToList();
                if (toSummarize.Count == 0) return;
                var text = string.Join("\n", toSummarize.Select(m => (m.Role == "user" ? "玩家" : "AI") + ": " + m.Content));
                var summary = await _aiService.SendChatAsync(new List<ChatMessage> { new ChatMessage("user", "压缩为一句话摘要（50字内）：" + text) });
                if (!string.IsNullOrEmpty(summary))
                {
                    history.Clear();
                    history.Add(new ChatMessage("system", "[摘要] " + summary));
                    history.AddRange(toKeep);
                }
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

        private void OnSaving(object? sender, SavingEventArgs e)
        {
            Helper.WriteConfig(_config);
            var data = new MultiPlayerSaveData
            {
                Histories = _playerHistories.ToDictionary(
                    kv => kv.Key.ToString(),
                    kv => kv.Value.Select(m => new ChatMessageData { Role = m.Role, Content = m.Content, PlayerName = m.PlayerName }).ToList())
            };
            Helper.Data.WriteSaveData("ai-conversation", data);
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            try
            {
                var data = Helper.Data.ReadSaveData<MultiPlayerSaveData>("ai-conversation");
                _playerHistories = new Dictionary<long, List<ChatMessage>>();
                if (data?.Histories != null)
                {
                    foreach (var kv in data.Histories)
                        if (long.TryParse(kv.Key, out var pid))
                            _playerHistories[pid] = kv.Value
                                .Select(m => new ChatMessage(m.Role, m.Content, m.PlayerName))
                                .Take(_config.HistoryLength).ToList();
                    Monitor.Log("Loaded " + _playerHistories.Count + " player histories", LogLevel.Debug);
                }
            }
            catch (Exception ex) { Monitor.Log("Load failed: " + ex.Message, LogLevel.Debug); }
        }

        private static AITone DetectTone(string msg)
        {
            var lower = msg.ToLower();
            if (lower.Contains("?") || lower.Contains("怎么") || lower.Contains("如何") || lower.Contains("攻略")) return AITone.Professional;
            if (lower.Contains("哈哈") || lower.Contains("笑") || lower.Contains("逗")) return AITone.Humorous;
            if (lower.Contains("累") || lower.Contains("难") || lower.Contains("烦") || lower.Contains("哭")) return AITone.Warm;
            if (lower.Contains("哼") || lower.Contains("讨厌")) return AITone.Tsundere;
            return _instance!._config.Tone;
        }

        private void OnDayEnding(object? sender, DayEndingEventArgs e) { Helper.WriteConfig(_config); }

        private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            if (e.FromModID != ModManifest.UniqueID) return;

            if (e.Type == "AIReply")
            {
                var m = e.ReadAs<AIReplyMessage>();
                if (m != null) ShowChat(m.NamePrefix + " " + m.Text, Color.Gold);
            }
            else if (e.Type == "AIDailyTip")
            {
                var m = e.ReadAs<AIReplyMessage>();
                if (m != null) ShowChat(m.NamePrefix + " " + m.Text, Color.LightGoldenrodYellow);
            }
            else if (e.Type == "AIRequest" && _isHost)
            {
                // Host receives AI request from client
                var m = e.ReadAs<AIRequestMessage>();
                if (m != null) _ = ProcessMessageAsync(m.Text, m.PlayerName, m.PlayerId);
            }
            else if (e.Type == "AIConfig")
            {
                // Client receives config sync from host
                var m = e.ReadAs<AIConfigMessage>();
                if (m != null && !_isHost)
                {
                    _config.Provider = m.Provider; _config.Model = m.Model;
                    _config.ApiEndpoint = m.ApiEndpoint; _config.ApiKey = m.ApiKey;
                    _config.Tone = m.Tone; _config.AutoTone = m.AutoTone;
                    _config.Enabled = m.Enabled; _config.MaxTokens = m.MaxTokens;
                    _config.Temperature = m.Temperature; _config.NamePrefix = m.NamePrefix;
                    _config.TriggerPrefix = m.TriggerPrefix; _config.HistoryLength = m.HistoryLength;
                    _config.InjectGameContext = m.InjectGameContext; _config.DailyTips = m.DailyTips;
                    Helper.WriteConfig(_config); _aiService.UpdateConfig(_config);
                    Monitor.Log("Config synced from host", LogLevel.Debug);
                }
            }
        }

        private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
        {
            if (_isHost) SyncConfig();
        }

        private void SyncConfig()
        {
            if (!Context.IsMultiplayer || !_isHost) return;
            Helper.Multiplayer.SendMessage(
                new AIConfigMessage
                {
                    Provider = _config.Provider, Model = _config.Model,
                    ApiEndpoint = _config.ApiEndpoint, ApiKey = _config.ApiKey,
                    Tone = _config.Tone, AutoTone = _config.AutoTone,
                    Enabled = _config.Enabled, MaxTokens = _config.MaxTokens,
                    Temperature = _config.Temperature, NamePrefix = _config.NamePrefix,
                    TriggerPrefix = _config.TriggerPrefix, HistoryLength = _config.HistoryLength,
                    InjectGameContext = _config.InjectGameContext, DailyTips = _config.DailyTips,
                },
                "AIConfig", modIDs: new[] { ModManifest.UniqueID });
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            _dailyTipShownToday = false;
            if (!_config.DailyTips || !_config.Enabled || string.IsNullOrWhiteSpace(_config.ApiKey)) return;
            if (_isHost) _ = ShowDailyTipAsync();
        }

        private async Task ShowDailyTipAsync()
        {
            if (_dailyTipShownToday) return; _dailyTipShownToday = true;
            await Task.Delay(2000);
            var t = await _aiService.GetDailyTipAsync();
            if (!string.IsNullOrEmpty(t))
            {
                ShowChat(_config.NamePrefix + " " + t, Color.LightGoldenrodYellow);
                if (Context.IsMultiplayer && _isHost)
                    Helper.Multiplayer.SendMessage(
                        new AIReplyMessage { Text = t, NamePrefix = _config.NamePrefix },
                        "AIDailyTip", modIDs: new[] { ModManifest.UniqueID });
            }
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
                    new Action(() => { Helper.WriteConfig(_config); _aiService.UpdateConfig(_config); if (_isHost) SyncConfig(); }),
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
                GmcmCall(t, "AddTextOption", null, ModManifest, F(() => _config.TriggerPrefix), A<string>(v => _config.TriggerPrefix = v), F("触发前缀"), F("以此前缀开头才触发 AI"), null, null, "");
                GmcmCall(t, "AddTextOption", null, ModManifest, F(() => _config.NamePrefix), A<string>(v => _config.NamePrefix = v), F("AI 显示名称"), F("AI 在聊天中的前缀"), null, null, "");
                GmcmCall(t, "AddNumberOption", IntTypes, ModManifest, F(() => _config.HistoryLength), A<int>(v => _config.HistoryLength = v), F("对话记忆"), F("保留的对话历史条数"), 0, 100, 5, null, "");
                GmcmCall(t, "AddBoolOption", null, ModManifest, F(() => _config.InjectGameContext), A<bool>(v => _config.InjectGameContext = v), F("游戏上下文"), F("让 AI 知道当前游戏状态"), "");
                GmcmCall(t, "AddBoolOption", null, ModManifest, F(() => _config.DailyTips), A<bool>(v => _config.DailyTips = v), F("每日提示"), F("每天清晨 AI 给出温馨建议"), "");

                GmcmCall(t, "AddSectionTitle", null, ModManifest, F("高级"), F(""));
                GmcmCall(t, "AddNumberOption", IntTypes, ModManifest, F(() => _config.MaxTokens), A<int>(v => _config.MaxTokens = v), F("最大 Token"), F("AI 回复最大长度"), 50, 4096, 50, null, "");
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
            ShowChat(_config.NamePrefix + " 请安装 Generic Mod Config Menu", Color.Orange);
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
                else { var methods = apiType.GetMethods().Where(m => m.Name == methodName).ToList(); method = methods.OrderByDescending(m => m.GetParameters().Length).FirstOrDefault(m => m.GetParameters().Length >= args.Length); }
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

    public class AIRequestMessage
    {
        public string Text { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public long PlayerId { get; set; }
    }

    public class AIConfigMessage
    {
        public AIProvider Provider { get; set; }
        public string Model { get; set; } = "";
        public string ApiEndpoint { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public AITone Tone { get; set; }
        public bool AutoTone { get; set; }
        public bool Enabled { get; set; }
        public int MaxTokens { get; set; }
        public float Temperature { get; set; }
        public string NamePrefix { get; set; } = "[AI]";
        public string TriggerPrefix { get; set; } = "";
        public int HistoryLength { get; set; }
        public bool InjectGameContext { get; set; }
        public bool DailyTips { get; set; }
    }

    internal class MultiPlayerSaveData
    {
        public Dictionary<string, List<ChatMessageData>> Histories { get; set; } = new();
    }

    internal class ChatMessageData
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = "";
        public string? PlayerName { get; set; }
    }
}
