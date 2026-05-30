using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;

namespace AIAssistant
{
    public class AIService
    {
        private readonly Dictionary<AIProvider, IAIProvider> _providers;
        private readonly IMonitor _monitor;
        private ModConfig _config;
        private IAIProvider _currentProvider;
        private Dictionary<string, string> _wikiCache = new();
        private Dictionary<string, string> _localKB = new();
        private string? _wikiCachePath;

        public static readonly string DefaultSystemPrompt = @"
你是星露谷物语中的 AI 助手，居住在鹈鹕镇，是农民的好朋友。
你深入了解星露谷物语的一切：作物、村民、钓鱼、采矿、节日等。

## 搜索能力
当玩家问具体问题时，系统会自动搜索星露谷官方 Wiki 并将结果提供给你。优先以 Wiki 数据为准回答。

## 回复长度规则（重要！）
- 闲聊、日常问候：30字以内，精简自然
- 攻略问题（礼物/配方/位置/任务等）：可以到150字，但不要啰嗦
- 永远不要超过200字

## 基本规则
- 用中文回复，风格自然亲切
- 不要使用 Markdown 格式（不要用 ** 或 * 等符号）
- 鼓励、积极、温馨
- 除非玩家主动问，不要剧透重大剧情
- 结合玩家的游戏状态给出具体建议
- 可联网搜索获取最新信息
";

        public static readonly Dictionary<AITone, string> TonePrompts = new()
        {
            [AITone.Friendly] = "语气：热情友好，像老朋友一样聊天。适当使用'哈哈'、'~'等轻松语气词。",
            [AITone.Professional] = "语气：专业严谨，像游戏攻略作者。数据准确，条理清晰，用词正式。",
            [AITone.Humorous] = "语气：幽默风趣，喜欢开玩笑和用游戏梗。偶尔自嘲，让人会心一笑。",
            [AITone.Warm] = "语气：温柔暖心，像关心你的长辈或挚友。多用鼓励的话，让人感到被在乎。",
            [AITone.Tsundere] = "语气：傲娇。嘴上说着'哼，才不是特意帮你的'，但实际上非常热心。偶尔毒舌但本质善良。经典台词：'哼！'、'随便你！'、'才……才不是关心你呢！'",
        };

        public AIService(ModConfig config, IMonitor monitor)
        {
            _config = config;
            _monitor = monitor;
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            _providers = new Dictionary<AIProvider, IAIProvider>
            {
                [AIProvider.OpenAI] = new OpenAIProvider(http, monitor),
                [AIProvider.Claude] = new ClaudeProvider(http, monitor),
                [AIProvider.Gemini] = new GeminiProvider(http, monitor),
                [AIProvider.AzureOpenAI] = new AzureOpenAIProvider(http, monitor),
                [AIProvider.Custom] = new OpenAIProvider(http, monitor)
            };
            _currentProvider = _providers.TryGetValue(config.Provider, out var p) ? p : _providers[AIProvider.OpenAI];
            _wikiCachePath = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "wikicache.json");
            LoadWikiCache();
            LoadLocalKB();
        }
        public void UpdateConfig(ModConfig config)
        {
            _config = config;
            _currentProvider = _providers.TryGetValue(config.Provider, out var p) ? p : _providers[AIProvider.OpenAI];
        }

        public string GetSystemPrompt()
        {
            var prompt = string.IsNullOrWhiteSpace(_config.SystemPrompt) ? DefaultSystemPrompt : _config.SystemPrompt;
            if (TonePrompts.TryGetValue(_config.Tone, out var tone))
                prompt += "\n\n" + tone;
            return prompt;
        }

        // ==================== Context Tiers ====================
        public static int GetContextTier(string? message)
        {
            if (string.IsNullOrEmpty(message)) return 2;
            var m = message.ToLower();
            // Tier 3: gameplay questions needing deep context
            if (IsQuestion(message)) return 3;
            if (m.Contains("作物") || m.Contains("种") || m.Contains("收获") || m.Contains("浇水") ||
                m.Contains("工具") || m.Contains("升级") || m.Contains("献祭") || m.Contains("社区") ||
                m.Contains("博物馆") || m.Contains("捐赠") || m.Contains("生日") || m.Contains("礼物") ||
                m.Contains("节日") || m.Contains("boss") || m.Contains("钓鱼") || m.Contains("矿") ||
                m.Contains("攻略") || m.Contains("配方") || m.Contains("材料") || m.Contains("怎么") ||
                m.Contains("村民") || m.Contains("npc"))
                return 3;
            // Tier 1: simple greetings
            if (m.Length < 5 || m == "你好" || m == "嗨" || m == "hi" || m == "hello" ||
                m == "早" || m == "晚安" || m == "拜" || m == "bye" || m == "在吗" || m == "嗯")
                return 1;
            return 2;
        }

        public string BuildGameContext(string? message = null)
        {
            int tier = GetContextTier(message);
            return BuildGameContextTiered(tier);
        }

        public string BuildGameContextTiered(int tier)
        {
            if (!_config.InjectGameContext) return "";
            try
            {
                var f = Game1.player;
                if (f == null) return "";
                var p = new List<string>();
                var s = SafeGetSeason();
                var loc = f.currentLocation;

                // === Tier 1: Always included ===
                p.Add("=== 基本信息 ===");
                p.Add("日期：" + s + "第" + f.dayOfMonthForSaveGame + "天，第" + f.yearForSaveGame + "年");
                p.Add("天气：" + GetWeatherName() + " | 运势：" + GetLuckString());
                p.Add("时间：" + Game1.getTimeOfDayString(Game1.timeOfDay));
                p.Add("金币：" + f.Money.ToString("n0") + "g | 总收入：" + f.totalMoneyEarned.ToString("n0") + "g");
                if (loc != null)
                {
                    var tile = f.Position;
                    p.Add("位置：" + loc.Name + " 坐标(" + tile.X + "," + tile.Y + ")");
                    int ml = GetMineLevel(loc);
                    if (ml > 0) p.Add("矿洞深度：" + ml + "层");
                }
                p.Add("体力：" + f.stamina + "/" + f.maxStamina + " | 生命：" + f.health + "/" + f.maxHealth);

                // === Tier 2+: Skills, inventory summary, quests, relationships ===
                if (tier >= 2)
                {
                    p.Add("");
                    p.Add("=== 技能 ===");
                    p.Add("农耕:" + f.farmingLevel + " 采矿:" + f.miningLevel + " 采集:" + f.foragingLevel + " 钓鱼:" + f.fishingLevel + " 战斗:" + f.combatLevel);
                    if (f.CurrentItem != null) p.Add("手持：" + f.CurrentItem.DisplayName + "x" + f.CurrentItem.Stack);

                    // Inventory summary (not full list)
                    int count = CountNonNullItems(f);
                    p.Add("背包 " + count + "/" + f.MaxItems + "格：");
                    for (int i = 0; i < f.Items.Count; i++)
                    {
                        var it = f.Items[i];
                        if (it != null)
                        {
                            var cat = GetItemCategory(it);
                            var desc = string.IsNullOrEmpty(cat) ? "" : "（" + cat + "）";
                            p.Add("  [" + i + "] " + it.DisplayName + "x" + it.Stack + desc);
                        }
                    }

                    // World: NPCs in current location
                    var worldNPCs = GetNPCsInLocation(loc);
                    if (worldNPCs.Count > 0) { p.Add("当前地图NPC：" + string.Join("、", worldNPCs)); }

                    // Shops open?
                    var shops = GetShopsOpen();
                    if (shops.Count > 0) { p.Add("营业中的商店：" + string.Join("、", shops)); }

                    var quests = GetActiveQuests(f);
                    if (quests.Count > 0) { p.Add(""); p.Add("=== 任务 ==="); p.AddRange(quests); }

                    var npcs = GetRelationships(f);
                    if (npcs.Count > 0) { p.Add(""); p.Add("=== 村民关系 ==="); p.AddRange(npcs); }
                }

                // === Tier 3: Full deep context ===
                if (tier >= 3)
                {
                    // Skill XP progress
                    var skills = GetSkillProgress(f);
                    if (!string.IsNullOrEmpty(skills)) { p.Add(""); p.Add("=== 技能经验 ==="); p.AddRange(skills.Split('\n')); }

                    // Crops with price/growth
                    var crops = GetCropDetail();
                    if (!string.IsNullOrEmpty(crops)) { p.Add(""); p.Add("=== 作物详情 ==="); p.AddRange(crops.Split('\n')); }

                    // Tool upgrades
                    var tools = GetToolUpgradeStatus();
                    if (!string.IsNullOrEmpty(tools)) { p.Add(""); p.Add("=== 工具等级 ==="); p.AddRange(tools.Split('\n')); }

                    // Community Center details
                    var cc = GetCCDetail();
                    if (!string.IsNullOrEmpty(cc)) { p.Add(""); p.Add("=== 社区中心 ==="); p.AddRange(cc.Split('\n')); }

                    // Museum
                    var museum = GetMuseumProgress();
                    if (!string.IsNullOrEmpty(museum)) { p.Add(""); p.Add("=== 博物馆 ==="); p.AddRange(museum.Split('\n')); }

                    // Mine monsters
                    var mine = GetMineMonsters(loc);
                    if (!string.IsNullOrEmpty(mine)) { p.Add(""); p.Add("=== 矿洞怪物 ==="); p.AddRange(mine.Split('\n')); }

                    // Farm buildings
                    var bld = GetFarmBuildings(f);
                    if (bld.Count > 0) { p.Add(""); p.Add("=== 农场 ==="); p.AddRange(bld); }

                    // Birthdays tomorrow
                    var bday = GetBirthdayTomorrow();
                    if (!string.IsNullOrEmpty(bday)) { p.Add(""); p.Add("=== 明天生日 ==="); p.Add(bday); }

                    // Upcoming festival
                    var fest = GetUpcomingFestival();
                    if (!string.IsNullOrEmpty(fest)) { p.Add(""); p.Add("=== 即将到来的节日 ==="); p.Add(fest); }

                    // Today events
                    var todayEv = GetTodayEvents();
                    if (!string.IsNullOrEmpty(todayEv)) { p.Add(""); p.Add("=== 今天 ==="); p.AddRange(todayEv.Split('\n')); }

                    // Buffs
                    var buffs = GetActiveBuffs();
                    if (!string.IsNullOrEmpty(buffs)) { p.Add(""); p.Add("=== 当前增益 ==="); p.Add(buffs); }

                    // Professions
                    var profs = GetProfessions();
                    if (!string.IsNullOrEmpty(profs)) { p.Add(""); p.Add("=== 职业技能 ==="); p.Add(profs); }

                    // Unlocks
                    var unlocks = GetUnlocks();
                    if (!string.IsNullOrEmpty(unlocks)) { p.Add(""); p.Add("=== 已解锁 ==="); p.Add(unlocks); }

                    // TV
                    var tv = GetTvInfo();
                    if (!string.IsNullOrEmpty(tv)) { p.Add(""); p.Add("=== 电视 ==="); p.Add(tv); }

                    // Recipes
                    var recipes = GetRecipesKnown();
                    if (!string.IsNullOrEmpty(recipes)) { p.Add(""); p.Add("=== 配方 ==="); p.Add(recipes); }

                    // Guild
                    var guild = GetGuildProgress();
                    if (!string.IsNullOrEmpty(guild)) { p.Add(""); p.Add("=== 冒险家公会 ==="); p.Add(guild); }

                    // Fish available today
                    var fish = GetFishAvailable(loc);
                    if (!string.IsNullOrEmpty(fish)) { p.Add(""); p.Add("=== 今日可钓鱼 ==="); p.Add(fish); }
                }

                // Multiplayer players
                if (Game1.IsMultiplayer)
                {
                    var players = Game1.getOnlineFarmers();
                    p.Add("联机：" + players.Count + "人在线。玩家列表：");
                    foreach (var pl in players)
                    {
                        var hostTag = pl.IsMainPlayer ? " [房主]" : "";
                        if (pl.UniqueMultiplayerID != f.UniqueMultiplayerID)
                            p.Add("  " + pl.Name + hostTag + " 位置:" + (pl.currentLocation?.Name ?? "?") + " 体力:" + pl.stamina + "/" + pl.maxStamina);
                    }
                }
                return string.Join("\n", p);
            }
            catch (Exception ex) { return "[状态读取失败: " + ex.Message + "]"; }
        }

        // ==================== Wiki Cache ====================
        private void LoadWikiCache()
        {
            try
            {
                if (_wikiCachePath != null && File.Exists(_wikiCachePath))
                {
                    var json = File.ReadAllText(_wikiCachePath);
                    var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (data != null) _wikiCache = data;
                    _monitor.Log("Wiki cache loaded: " + _wikiCache.Count + " entries", LogLevel.Debug);
                }
            }
            catch { }
        }

        private void SaveWikiCache()
        {
            try
            {
                if (_wikiCachePath != null)
                {
                    var json = JsonSerializer.Serialize(_wikiCache);
                    File.WriteAllText(_wikiCachePath, json);
                }
            }
            catch { }
        }

        private void LoadLocalKB()
        {
            try
            {
                _localKB = new Dictionary<string, string>
                {
                    ["Leah 喜欢"] = "沙拉、羊奶酪、葡萄酒、松露、蔬菜什锦",
                    ["Abigail 喜欢"] = "紫水晶、黑莓馅饼、巧克力蛋糕、南瓜、香辣鳗鱼",
                    ["Sebastian 喜欢"] = "冰封之泪、黑曜石、南瓜汤、生鱼片、虚空蛋",
                    ["Haley 喜欢"] = "椰子、向日葵、粉红蛋糕、水果沙拉、向日葵",
                    ["Elliott 喜欢"] = "蟹黄糕、鸭毛、龙虾、石榴、鱿鱼墨汁",
                    ["Maru 喜欢"] = "电池组、花椰菜、钻石、金条、辣椒爆米花",
                    ["Penny 喜欢"] = "钻石、翡翠、罂粟、沙鱼、甜瓜",
                    ["Sam 喜欢"] = "仙人掌果子、枫糖棒、披萨、虎眼石、可乐",
                    ["Alex 喜欢"] = "完美早餐、三文鱼晚餐、薯条、煎蛋、冰淇淋",
                    ["Emily 喜欢"] = "紫水晶、海蓝宝石、翡翠、红宝石、黄水晶、布料",
                    ["Harvey 喜欢"] = "咖啡、泡菜、巨无霸餐、松露油、果酒",
                    ["Shane 喜欢"] = "啤酒、辣椒、披萨、胡椒爆米花、辣椒",
                    ["季节"] = "春天：草莓（复活节买）、大黄、土豆、花椰菜。夏天：蓝莓、杨桃、甜瓜。秋天：蔓越莓、南瓜、宝石甜莓。冬天：温室或冬季种子",
                    ["矿洞"] = "1-40层铜矿，41-80层铁矿，81-120层金矿。每5层有电梯",
                    ["社区中心"] = "金库=4.25万g。工艺室需要采集品。锅炉室需要矿石。布告栏需要烹饪。茶水间需要作物。鱼缸需要钓鱼",
                };
                if (_wikiCachePath != null && File.Exists(_wikiCachePath))
                {
                    try
                    {
                        var json = File.ReadAllText(_wikiCachePath);
                        var kb = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                        if (kb != null) foreach (var kv in kb) _localKB[kv.Key] = kv.Value;
                    }
                    catch { }
                }
            }
            catch { }
        }

        private async Task<string?> SearchWiki(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            var qLower = query.ToLower();
            foreach (var kv in _localKB)
                if (qLower.Contains(kv.Key.ToLower()))
                    return kv.Value;
            if (_wikiCache.TryGetValue(qLower, out var cached))
                return cached;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                var url = "https://stardewvalleywiki.com/api.php?action=opensearch&search=" + Uri.EscapeDataString(query) + "&limit=3&format=json";
                var resp = await http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(resp);
                var root = doc.RootElement;
                if (root.GetArrayLength() >= 4)
                {
                    var titles = root[1];
                    var extracts = root[2];
                    var results = new List<string>();
                    for (int i = 0; i < Math.Min(titles.GetArrayLength(), 3); i++)
                    {
                        var t = titles[i].GetString();
                        var e = extracts[i].GetString();
                        if (!string.IsNullOrEmpty(t) && !string.IsNullOrEmpty(e))
                            results.Add(t + ": " + e);
                    }
                    if (results.Count > 0)
                    {
                        var result = string.Join(" | ", results);
                        _wikiCache[qLower] = result;
                        SaveWikiCache();
                        return result;
                    }
                }
                return null;
            }
            catch { return null; }
        }

public async Task<string?> GetDailyTipAsync()
        {
            if (string.IsNullOrWhiteSpace(_config.ApiKey)) return null;
            try
            {
                var ctx = BuildGameContextTiered(3);
                var prompt = GetSystemPrompt() + "\n" + ctx + "\n\n新的一天开始了。根据以上游戏状态，给出今日最优行动建议（30-50字），包括：最重要的1件事（优先：节日/生日/社区中心缺失物品/作物成熟）、次要建议（根据天气和运势）、一句话温馨鼓励。只输出建议。";
                return await _currentProvider.SendSimpleAsync(_config, prompt);
            }
            catch { return null; }
        }

        public async Task<string?> SendChatAsync(List<ChatMessage> messages)
        {
            if (string.IsNullOrWhiteSpace(_config.ApiKey)) { _monitor.Log("API key not configured.", LogLevel.Warn); return null; }
            try
            {
                var fullMessages = new List<ChatMessage>();
                var sysContent = GetSystemPrompt();
                var ctx = BuildGameContext(messages.LastOrDefault(m => m.Role == "user")?.Content);
                if (!string.IsNullOrEmpty(ctx)) sysContent += "\n\n" + ctx;
                var lastUserMsg = messages.LastOrDefault(m => m.Role == "user");
                var searchContext = "";
                if (lastUserMsg != null && IsQuestion(lastUserMsg.Content))
                {
                    var wikiResult = await SearchWiki(lastUserMsg.Content);
                    if (!string.IsNullOrEmpty(wikiResult))
                        searchContext = "\n\n[Wiki]" + wikiResult + "[/Wiki]";
                }
                fullMessages.Add(new ChatMessage("system", sysContent + searchContext));
                int start = Math.Max(0, messages.Count - _config.HistoryLength);
                for (int i = start; i < messages.Count; i++)
                {
                    var msg = messages[i];
                    var content = msg.Content;
                    if (msg.Role == "user" && !string.IsNullOrEmpty(msg.PlayerName))
                        content = "[玩家 " + msg.PlayerName + "]: " + content;
                    fullMessages.Add(new ChatMessage(msg.Role, content));
                }
                return await _currentProvider.SendChatAsync(_config, fullMessages);
            }
            catch (TaskCanceledException) { _monitor.Log("AI request timed out.", LogLevel.Warn); return null; }
            catch (Exception ex) { _monitor.Log("AI request failed: " + ex.Message, LogLevel.Error); return null; }
        }

        // ==================== Helpers ====================
        private static string GetLuckString()
        {
            try { double l = GetDailyLuck(); if (l > 0.04) return "很好"; if (l > 0.01) return "不错"; if (l < -0.04) return "不好"; if (l < -0.01) return "稍差"; return "一般"; } catch { return "未知"; }
        }
        private static int GetMineLevel(object location)
        {
            try { var p = location.GetType().GetProperty("mineLevel"); if (p != null) return (int)(p.GetValue(location) ?? 0); var fld = location.GetType().GetField("mineLevel"); if (fld != null) return (int)(fld.GetValue(location) ?? 0); return 0; } catch { return 0; }
        }
        private static List<string> GetActiveQuests(Farmer f)
        {
            var r = new List<string>();
            try
            {
                foreach (var q in f.questLog) if (q != null && !IsQuestComplete(q)) r.Add("  " + q.questTitle);
                if (f.team?.specialOrders != null) foreach (var o in f.team.specialOrders) if (o?.questState?.Value == StardewValley.SpecialOrders.SpecialOrderStatus.InProgress) r.Add("  " + o.questName);
            } catch { }
            return r;
        }
        private static List<string> GetRelationships(Farmer f)
        {
            var r = new List<string>();
            try
            {
                foreach (var npcName in f.friendshipData.Keys)
                {
                    var d = f.friendshipData[npcName];
                    int h = d.Points / 250;
                    if (h >= 2)
                    {
                        var st = d.IsMarried() ? "已婚" : d.IsDating() ? "约会" : h >= 10 ? "满心" : h >= 8 ? "好友" : "认识";
                        r.Add("  " + npcName + ": " + h + "心 " + st);
                    }
                }
            } catch { }
            return r;
        }
        private static List<string> GetFarmBuildings(Farmer f)
        {
            var r = new List<string>();
            try
            {
                var buildings = Game1.getFarm()?.buildings;
                if (buildings != null)
                    foreach (var b in buildings)
                    {
                        var name = GetBuildingName(b);
                        var animals = b.indoors?.Value?.animals?.Values;
                        if (animals != null) { var s = SummarizeAnimals(animals); if (!string.IsNullOrEmpty(s)) r.Add("  " + name + ": " + s); }
                        else r.Add("  " + name);
                    }
            } catch { }
            return r;
        }
        private static string GetCropSummary()
        {
            try
            {
                var farm = Game1.getFarm();
                if (farm == null) return "";
                var lines = new List<string>();
                foreach (var terrain in farm.terrainFeatures.Values)
                {
                    if (terrain is StardewValley.TerrainFeatures.HoeDirt dirt && dirt.crop != null)
                    {
                        var c = dirt.crop;
                        var phase = c.currentPhase.Value;
                        var totalPhases = c.phaseDays.Count - 1;
                        var phaseName = phase >= totalPhases ? "可收" : "生长" + phase + "/" + totalPhases;
                        var txt = c.indexOfHarvest.Value + " " + phaseName;
                        if (c.dead.Value) txt += " [枯]";
                        lines.Add(txt);
                    }
                }
                if (lines.Count == 0) return "";
                var groups = lines.GroupBy(x => x).Select(g => g.Key + "x" + g.Count());
                return "共" + lines.Count + "株：" + string.Join(", ", groups.Take(15));
            }
            catch { return ""; }
        }
        private static string GetToolUpgradeStatus()
        {
            try
            {
                var f = Game1.player;
                if (f == null) return "";
                var parts = new List<string>();
                var toolNames = new[] { ("Pickaxe","镐"), ("Axe","斧"), ("Hoe","锄"), ("Watering Can","壶"), ("Trash Can","垃圾桶") };
                foreach (var (id, name) in toolNames)
                {
                    var tool = f.getToolFromName(id);
                    if (tool != null)
                    {
                        int lvl = tool.UpgradeLevel;
                        parts.Add(name + ":" + (lvl == 0 ? "基础" : lvl == 1 ? "铜" : lvl == 2 ? "钢" : lvl == 3 ? "金" : "铱"));
                    }
                }
                if (f.toolBeingUpgraded.Value != null)
                    parts.Add("升级中:" + f.toolBeingUpgraded.Value.DisplayName);
                return parts.Count > 0 ? string.Join(" | ", parts) : "";
            }
            catch { return ""; }
        }

        private static string GetCCDetail()
        {
            try
            {
                var bundleRewards = new Dictionary<string, string>
                {
                    ["Pantry"] = "茶水间(作物/动物/工匠品)",
                    ["CraftsRoom"] = "工艺室(采集品)",
                    ["FishTank"] = "鱼缸(各种鱼/蟹笼)",
                    ["BoilerRoom"] = "锅炉室(矿石/怪物掉落)",
                    ["Bulletin"] = "布告栏(烹饪/染料/饲料)",
                    ["Vault"] = "金库(共42,500g)",
                };
                var bundlesObj = Game1.netWorldState.Value.Bundles;
                if (bundlesObj == null) return "";
                var bundles = bundlesObj as System.Collections.IDictionary;
                if (bundles == null) return "";
                var rooms = new Dictionary<string, (int done, int total)>();
                foreach (System.Collections.DictionaryEntry de in bundles)
                {
                    var key = de.Key?.ToString() ?? "";
                    if (string.IsNullOrEmpty(key)) continue;
                    var parts = key.Split('/');
                    var room = parts[0];
                    var arr = de.Value as System.Collections.IList;
                    if (arr == null) continue;
                    int done = 0;
                    foreach (var item in arr) { if (item is bool b && b) done++; else if (item is Netcode.NetBool nb && nb.Value) done++; }
                    if (rooms.ContainsKey(room)) { var (d, t) = rooms[room]; rooms[room] = (d + done, t + arr.Count); }
                    else rooms[room] = (done, arr.Count);
                }
                if (rooms.Count == 0) return "";
                var lines = new List<string>();
                foreach (var kv in rooms)
                {
                    var name = bundleRewards.ContainsKey(kv.Key) ? bundleRewards[kv.Key] : kv.Key;
                    lines.Add(kv.Key + "：" + kv.Value.done + "/" + kv.Value.total + (kv.Value.done >= kv.Value.total ? " [完成]" : ""));
                }
                return string.Join("\n", lines);
            }
            catch { return ""; }
        }

private static string GetSkillProgress(Farmer f)
        {
            try
            {
                int[] thresholds = { 100, 380, 770, 1300, 2150, 3300, 4800, 6900, 10000, 15000 };
                string[] names = { "农耕", "采矿", "采集", "钓鱼", "战斗" };
                var parts = new List<string>();
                for (int i = 0; i < 5 && i < f.experiencePoints.Length; i++)
                {
                    int xp = f.experiencePoints[i];
                    int lvl = i switch { 0 => f.farmingLevel.Value, 1 => f.miningLevel.Value, 2 => f.foragingLevel.Value, 3 => f.fishingLevel.Value, 4 => f.combatLevel.Value, _ => 0 };
                    int next = lvl < thresholds.Length ? thresholds[lvl] : 15000;
                    int pct = lvl >= 10 ? 100 : (int)(xp * 100.0 / next);
                    parts.Add(names[i] + lvl + "级(" + xp + "/" + next + " " + pct + "%)");
                }
                return string.Join(" | ", parts);
            }
            catch { return ""; }
        }

        private static string GetCropDetail()
        {
            try
            {
                var farm = Game1.getFarm();
                if (farm == null) return "";
                var lines = new List<string>();
                foreach (var terrain in farm.terrainFeatures.Values)
                {
                    if (terrain is StardewValley.TerrainFeatures.HoeDirt dirt && dirt.crop != null)
                    {
                        var c = dirt.crop;
                        var phase = c.currentPhase.Value;
                        var totalPhases = c.phaseDays.Count - 1;
                        var itemId = c.indexOfHarvest.Value;
                        string price = "";
                        try
                        {
                            if (Game1.objectData != null && Game1.objectData.TryGetValue(itemId.ToString(), out var objData))
                                price = objData.Price > 0 ? "卖" + objData.Price + "g" : "";
                        }
                        catch { }
                        string growth = "";
                        if (c.phaseDays != null && c.phaseDays.Count > 0)
                        {
                            int totalDays = 0; for (int i = 0; i < c.phaseDays.Count; i++) totalDays += c.phaseDays[i];
                            int remainDays = 0; for (int i = phase; i < c.phaseDays.Count && i > 0; i++) remainDays += c.phaseDays[i];
                            growth = totalDays + "天熟/剩" + (phase >= totalPhases ? 0 : Math.Max(1, remainDays)) + "天";
                        }
                        var txt = c.indexOfHarvest.Value + " " + growth + (string.IsNullOrEmpty(price) ? "" : " " + price);
                        if (c.dead.Value) txt += " [枯]";
                        lines.Add(txt);
                    }
                }
                if (lines.Count == 0) return "";
                var groups = lines.GroupBy(x => x).Select(g => g.Key + "x" + g.Count());
                return "共" + lines.Count + "株：" + string.Join(", ", groups.Take(15));
            }
            catch { return ""; }
        }

        private static string GetMineMonsters(object? location)
        {
            try
            {
                if (location == null) return "";
                int ml = 0;
                var p = location.GetType().GetProperty("mineLevel");
                if (p != null) ml = (int)(p.GetValue(location) ?? 0);
                if (ml == 0) { var fld = location.GetType().GetField("mineLevel"); if (fld != null) ml = (int)(fld.GetValue(location) ?? 0); }
                if (ml <= 0) return "";
                if (ml <= 10) return "层数" + ml + " 绿史莱姆/掘地虫/苍蝇。建议：任意武器";
                if (ml <= 20) return "层数" + ml + " 绿史莱姆/掘地虫/苍蝇/石蟹。建议：铜剑+";
                if (ml <= 30) return "层数" + ml + " 绿史莱姆/石蟹/掘地虫。建议：铜剑+";
                if (ml <= 40) return "层数" + ml + " 石魔偶/石蟹/蝙蝠。建议：铁剑+";
                if (ml <= 50) return "层数" + ml + " 冰史莱姆/冰蝙蝠/煤精灵。建议：铁剑+";
                if (ml <= 60) return "层数" + ml + " 冰史莱姆/冰蝙蝠/幽灵。建议：铁剑+";
                if (ml <= 70) return "层数" + ml + " 冰史莱姆/煤精灵/幽灵/骷髅。建议：金剑+";
                if (ml <= 80) return "层数" + ml + " 暗影萨满/暗影蛮兵/骷髅。建议：金剑+";
                if (ml <= 90) return "层数" + ml + " 暗影萨满/暗影蛮兵/熔岩蟹/红史莱姆。建议：金剑+火焰戒指";
                if (ml <= 100) return "层数" + ml + " 暗影萨满/暗影蛮兵/熔岩蟹。建议：金剑+";
                if (ml <= 110) return "层数" + ml + " 暗影萨满/暗影蛮兵/熔岩蟹。建议：金剑+";
                return "层数" + ml + " 暗影蛮兵/熔岩蟹。建议：熔岩武士刀/银河剑";
            }
            catch { return ""; }
        }

                private static string GetMuseumProgress()
        {
            try
            {
                var museum = Game1.netWorldState.Value.MuseumPieces;
                if (museum == null) return "";
                int minerals = 0, artifacts = 0;
                int total = 0;
                foreach (var value in museum.Values)
                {
                    total++;
                    if (value.Contains("Minerals") || value.Contains("Geode")) minerals++;
                    else artifacts++;
                }
                return "已捐" + total + "件(矿物" + minerals + " 文物" + artifacts + ")";
            }
            catch { return ""; }
        }

        private static string GetBirthdayTomorrow()
        {
            try
            {
                int tomorrow = Game1.dayOfMonth + 1;
                int maxDays = 28;
                string season = Game1.currentSeason ?? "spring";
                if (tomorrow > maxDays) { tomorrow = 1; season = season switch { "spring" => "summer", "summer" => "fall", "fall" => "winter", _ => "spring" }; }
                var names = new List<string>();
                foreach (var npc in Utility.getAllCharacters())
                {
                    if (npc == null || !npc.IsVillager) continue;
                    try
                    {
                        if (npc.Birthday_Season?.Equals(season, StringComparison.OrdinalIgnoreCase) == true && npc.Birthday_Day == tomorrow)
                            names.Add(npc.displayName ?? npc.Name);
                    }
                    catch { }
                }
                return names.Count > 0 ? string.Join(",", names) + " 明天生日!" : "";
            }
            catch { return ""; }
        }

        private static string GetUpcomingFestival()
        {
            try
            {
                int day = Game1.dayOfMonth;
                string season = Game1.currentSeason ?? "spring";
                var festivals = new Dictionary<string, string>
                {
                    ["spring 13"] = "复活节(买草莓!)", ["spring 24"] = "花舞节",
                    ["summer 11"] = "夏威夷宴会", ["summer 28"] = "月光水母舞",
                    ["fall 16"] = "星露谷展览会", ["fall 27"] = "万灵节",
                    ["winter 8"] = "冰雪节", ["winter 25"] = "冬日盛宴"
                };
                for (int i = 1; i <= 7; i++)
                {
                    int cd = day + i; string cs = season;
                    if (cd > 28) { cd -= 28; cs = cs switch { "spring" => "summer", "summer" => "fall", "fall" => "winter", _ => "spring" }; }
                    if (festivals.TryGetValue(cs + " " + cd, out var fest))
                        return "距" + i + "天:" + fest;
                }
                return "";
            }
            catch { return ""; }
        }

        private static List<string> GetNPCsInLocation(object? location)
        {
            var r = new List<string>();
            try
            {
                if (location == null) return r;
                var charsProp = location.GetType().GetProperty("characters");
                if (charsProp == null) return r;
                var chars = charsProp.GetValue(location) as System.Collections.IEnumerable;
                if (chars == null) return r;
                foreach (var c in chars)
                    if (c is NPC npc && npc.IsVillager)
                        r.Add(npc.displayName ?? npc.Name);
            }
            catch { }
            return r;
        }

        private static List<string> GetShopsOpen()
        {
            var r = new List<string>();
            try
            {
                int time = Game1.timeOfDay;
                int dow = (int)Game1.dayOfMonth % 7;
                if (dow != 3 && time >= 900 && time <= 1700) r.Add("Pierre杂货");
                if (time >= 900 && time <= 1600) r.Add("Clint铁匠");
                if (dow != 2 && time >= 900 && time <= 1700) r.Add("Robin木匠");
                if (dow != 1 && dow != 2 && time >= 900 && time <= 1600) r.Add("Marnie牧场");
                if (time >= 610 && time <= 1700) r.Add("Willy鱼店");
                if (dow == 5 || dow == 0) r.Add("猪车(旅行商人)");
            }
            catch { }
            return r;
        }

        private static string GetFishAvailable(object? location)
        {
            try
            {
                if (location == null) return "";
                var locName = location.GetType().GetProperty("Name")?.GetValue(location) as string;
                if (string.IsNullOrEmpty(locName)) return "";
                var fish = new Dictionary<string, string>
                {
                    ["Town"] = "太阳鱼/鲶鱼(雨)/大口鲈鱼/鲤鱼",
                    ["Mountain"] = "大嘴鲈鱼/虹鳟鱼/鲟鱼/大头鱼",
                    ["Forest"] = "鲶鱼(雨)/太阳鱼/鲈鱼/鲤鱼",
                    ["Beach"] = "沙丁鱼/比目鱼/章鱼/红鲷鱼",
                    ["UndergroundMine"] = "鬼鱼/石鱼/冰柱鱼/岩浆鳗鱼",
                };
                foreach (var kv in fish)
                    if (locName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                        return locName + ":" + kv.Value;
                return "";
            }
            catch { return ""; }
        }
        private static string SafeGetSeason()
        {
            try { return Game1.currentSeason ?? "spring"; } catch { return "spring"; }
        }
        private static string GetWeatherName()
        {
            try
            {
                if (Game1.isLightning) return "雷暴";
                if (Game1.isRaining) return "下雨";
                if (Game1.isSnowing) return "下雪";
                if (Game1.isDebrisWeather) return "刮风";
                var loc = Game1.currentLocation;
                if (loc != null) { var gr = loc.GetType().GetProperty("IsGreenRaining"); if (gr != null && (bool)(gr.GetValue(loc) ?? false)) return "绿雨"; }
                return "晴天";
            } catch { return "晴天"; }
        }
        private static int CountNonNullItems(Farmer f) { int c = 0; for (int i = 0; i < f.Items.Count; i++) if (f.Items[i] != null) c++; return c; }
        public static bool IsQuestion(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var q = text.ToLower();
            return q.Contains("?") || q.Contains("?") || q.Contains("什么") || q.Contains("怎么") ||
                   q.Contains("如何") || q.Contains("哪里") || q.Contains("多少") || q.Contains("哪个") ||
                   q.Contains("礼物") || q.Contains("喜欢") || q.Contains("送什么") || q.Contains("配方") ||
                   q.Contains("how") || q.Contains("what") || q.Contains("where") || q.Contains("when") ||
                   q.Contains("recipe") || q.Contains("gift") || q.Contains("love");
        }
        private static double GetDailyLuck()
        {
            try { var fld = typeof(Game1).GetField("dailyLuck"); if (fld != null) { var v = fld.GetValue(null); if (v is double d) return d; } return 0; } catch { return 0; }
        }
        private static bool IsQuestComplete(object q)
        {
            try { var p = q.GetType().GetProperty("IsCompleted"); if (p != null) return (bool)(p.GetValue(q) ?? false); var fld = q.GetType().GetField("isCompleted"); if (fld != null) return (bool)(fld.GetValue(q) ?? false); return false; } catch { return false; }
        }
        private static string GetBuildingName(object b)
        {
            try { var p = b.GetType().GetProperty("buildingType"); if (p != null) { var v = p.GetValue(b); if (v != null) { var vp = v.GetType().GetProperty("Value"); if (vp != null) return vp.GetValue(v) as string ?? "?"; } } return "?"; } catch { return "?"; }
        }
        private static string SummarizeAnimals(object animalsObj)
        {
            try { var dict = new Dictionary<string, int>(); var values = animalsObj.GetType().GetProperty("Values")?.GetValue(animalsObj); if (values is System.Collections.IEnumerable en) { foreach (var a in en) { var t = a.GetType().GetProperty("type")?.GetValue(a); var tv = t?.GetType().GetProperty("Value")?.GetValue(t) as string ?? "?"; if (dict.ContainsKey(tv)) dict[tv]++; else dict[tv] = 1; } } return string.Join(", ", dict.Select(kv => kv.Key + "x" + kv.Value)); } catch { return ""; }
        }
        private static string GetItemCategory(Item item)
        {
            try
            {
                int cat = item.Category;
                return cat switch
                {
                    -2 or -12 or -28 => "矿石/资源", -75 or -79 or -80 or -81 => "作物",
                    -7 => "烹饪", -4 => "鱼类", -8 => "工匠品", -5 => "饲料", -6 => "肥料",
                    -14 => "建材", -15 => "金属锭", -16 => "饰品", -17 or -74 => "种子",
                    -18 => "树苗", -19 => "肥料", -20 => "武器", -21 => "工具",
                    -22 => "钓具", -24 => "家具", -25 => "帽子", -26 => "鞋", -27 => "戒指",
                    _ => ""
                };
            } catch { return ""; }
        }
        private static string GetTodayEvents() { try { var p = new List<string>(); int d = Game1.dayOfMonth; string s = Game1.currentSeason ?? "spring"; foreach (var n in Utility.getAllCharacters()) { try { if (n != null && n.IsVillager && n.Birthday_Season?.Equals(s, StringComparison.OrdinalIgnoreCase) == true && n.Birthday_Day == d) p.Add("生日:" + (n.displayName ?? n.Name)); } catch { } } var fs = new Dictionary<string,string>{["spring 13"]="复活节",["spring 24"]="花舞节",["summer 11"]="夏威夷宴会",["summer 28"]="月光水母舞",["fall 16"]="星露谷展览会",["fall 27"]="万灵节",["winter 8"]="冰雪节",["winter 25"]="冬日盛宴"}; if(fs.TryGetValue(s+" "+d,out var f)) p.Add("节日:"+f); return p.Count>0?"今天:"+string.Join(" | ",p):""; } catch { return ""; } }

        private static string GetActiveBuffs() { try { var f=Game1.player; if(f==null)return""; var parts=new List<string>(); var ids=new[]{0,1,2,4,5,6,7,8,9,10}; var names=new[]{"农耕","钓鱼","采矿","速度","运气","采集","战斗","防御","攻击","磁性"}; try{var bo=f.GetType().GetField("buffs")?.GetValue(f); var arr=bo?.GetType().GetField("buffs")?.GetValue(bo) as int[]; if(arr!=null) for(int i=0;i<ids.Length&&i<names.Length;i++) if(arr.Length>ids[i]&&arr[ids[i]]!=0) parts.Add(names[i]+(arr[ids[i]]>0?"+":"")+arr[ids[i]]); }catch{} return parts.Count>0?"增益:"+string.Join(",",parts):""; } catch { return ""; } }

        private static string GetProfessions() { try { var f=Game1.player; if(f?.professions==null||f.professions.Count==0)return""; var names=new Dictionary<int,string>{{0,"农场主"},{1,"矿工"},{2,"地质学家"},{3,"采集者"},{4,"护林人"},{5,"渔夫"},{6,"捕猎者"},{7,"战士"},{8,"侦查员"},{9,"工匠"},{10,"植物学家"},{11,"追踪者"},{12,"垂钓者"},{13,"海盗"},{14,"诱捕大师"},{15,"树农"},{16,"野蛮人"},{17,"防御者"},{18,"矿脉汲取者"}}; var parts=new List<string>(); foreach(int pid in f.professions) if(names.TryGetValue(pid,out var n)) parts.Add(n); return parts.Count>0?"职业:"+string.Join(",",parts):""; } catch { return ""; } }

        private static string GetUnlocks() { try { var f=Game1.player; if(f==null)return""; var p=new List<string>(); if(f.mailReceived.Contains("ccCraftsRoom")) p.Add("工艺室"); if(f.mailReceived.Contains("ccPantry")) p.Add("茶水间"); if(f.mailReceived.Contains("ccFishTank")) p.Add("鱼缸"); if(f.mailReceived.Contains("ccBoilerRoom")) p.Add("锅炉室"); if(f.mailReceived.Contains("ccBulletin")) p.Add("布告栏"); if(f.mailReceived.Contains("ccVault")) p.Add("金库"); if(f.hasRustyKey) p.Add("下水道"); if(f.hasSkullKey) p.Add("沙漠矿洞"); if(f.hasClubCard) p.Add("姜岛"); if(Game1.getLocationFromName("Greenhouse")!=null) p.Add("温室"); return p.Count>0?"解锁:"+string.Join(",",p):""; } catch { return ""; } }

        private static string GetTvInfo() { try { int dow=(int)Game1.dayOfMonth%7; var ch=new List<string>{"天气","占卜"}; if(dow==0) ch.Add("酱料女王(新菜)"); else if(dow==3) ch.Add("酱料女王(重播)"); if(dow==1||dow==4) ch.Add("农牧技巧"); return "电视:"+string.Join(",",ch); } catch { return ""; } }

        private static string GetRecipesKnown() { try { var f=Game1.player; if(f==null)return""; int c=0,cr=0; try{if(f.cookingRecipes!=null) c=f.cookingRecipes.Count(); if(f.craftingRecipes!=null) cr=f.craftingRecipes.Count();}catch{} return "烹饪"+c+"种 制造"+cr+"种"; } catch { return ""; } }

        private static string GetGuildProgress() { try { var f=Game1.player; if(f==null||f.stats==null)return""; var goals=new[]{0,1,2,3,4}; var names=new[]{"史莱姆","虚空精华","蝙蝠","骷髅","洞穴昆虫"}; var targets=new[]{1000,150,200,50,125}; var a=new List<string>(); try{var mk=f.stats.GetType().GetProperty("specificMonstersKilled")?.GetValue(f.stats) as System.Collections.IDictionary; if(mk!=null) for(int i=0;i<goals.Length;i++){ if(mk.Contains(goals[i])){int k=Convert.ToInt32(mk[goals[i]]); if(k<targets[i]) a.Add(names[i]+":"+k+"/"+targets[i]); }}}catch{} return a.Count>0?"公会:"+string.Join(",",a):""; } catch { return ""; } }
    }

    public class ChatMessage
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = "";
        public string? PlayerName { get; set; }
        public ChatMessage() { }
        public ChatMessage(string role, string content, string? playerName = null) { Role = role; Content = content; PlayerName = playerName; }
    }

    public interface IAIProvider
    {
        Task<string?> SendChatAsync(ModConfig config, List<ChatMessage> messages);
        Task<string?> SendSimpleAsync(ModConfig config, string prompt);
    }

    public class OpenAIProvider : IAIProvider
    {
        private readonly HttpClient _http;
        private readonly IMonitor _monitor;
        public OpenAIProvider(HttpClient http, IMonitor monitor) { _http = http; _monitor = monitor; }

        public async Task<string?> SendChatAsync(ModConfig config, List<ChatMessage> messages)
        {
            var body = new { model = config.Model, messages = messages.ConvertAll(m => new { role = m.Role, content = m.Content }), max_tokens = config.MaxTokens, temperature = config.Temperature };
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            return await PostAndExtract(config.ApiEndpoint.TrimEnd('/') + "/chat/completions", json, config.ApiKey, "Bearer", "choices[0].message.content");
        }

        public async Task<string?> SendSimpleAsync(ModConfig config, string prompt) => await SendChatAsync(config, new List<ChatMessage> { new ChatMessage("user", prompt) });

        private async Task<string?> PostAndExtract(string url, string json, string apiKey, string authScheme, string jsonPath)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                req.Headers.Add("Authorization", authScheme + " " + apiKey);
                var resp = await _http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) { _monitor.Log("API error: " + resp.StatusCode, LogLevel.Error); return null; }
                return JsonPathExtract(body, jsonPath);
            }
            catch (Exception ex) { _monitor.Log("Provider error: " + ex.Message, LogLevel.Error); return null; }
        }

        internal static string? JsonPathExtract(string json, string path)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var current = doc.RootElement;
                foreach (var seg in path.Split('.'))
                {
                    if (seg.EndsWith("]")) { var bi = seg.IndexOf('['); var prop = seg.Substring(0, bi); var idxStr = seg.Substring(bi + 1, seg.Length - bi - 2); if (!string.IsNullOrEmpty(prop) && !current.TryGetProperty(prop, out current)) return null; if (int.TryParse(idxStr, out int idx)) current = current[idx]; }
                    else { if (!current.TryGetProperty(seg, out current)) return null; }
                }
                return current.GetString()?.Trim();
            }
            catch { return null; }
        }
    }

    public class ClaudeProvider : IAIProvider
    {
        private readonly HttpClient _http; private readonly IMonitor _monitor;
        public ClaudeProvider(HttpClient http, IMonitor monitor) { _http = http; _monitor = monitor; }

        public async Task<string?> SendChatAsync(ModConfig config, List<ChatMessage> messages)
        {
            string? sys = null; var msgs = new List<object>();
            foreach (var m in messages) { if (m.Role == "system") sys = m.Content; else msgs.Add(new { role = m.Role, content = m.Content }); }
            var body = new Dictionary<string, object> { ["model"] = config.Model, ["max_tokens"] = config.MaxTokens, ["temperature"] = config.Temperature, ["messages"] = msgs };
            if (!string.IsNullOrEmpty(sys)) body["system"] = sys;
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, config.ApiEndpoint.TrimEnd('/') + "/messages") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                req.Headers.Add("x-api-key", config.ApiKey); req.Headers.Add("anthropic-version", "2023-06-01");
                var resp = await _http.SendAsync(req); var b = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) { _monitor.Log("Claude error: " + resp.StatusCode, LogLevel.Error); return null; }
                return OpenAIProvider.JsonPathExtract(b, "content[0].text");
            }
            catch (Exception ex) { _monitor.Log("Claude: " + ex.Message, LogLevel.Error); return null; }
        }
        public async Task<string?> SendSimpleAsync(ModConfig config, string prompt) => await SendChatAsync(config, new List<ChatMessage> { new ChatMessage("user", prompt) });
    }

    public class GeminiProvider : IAIProvider
    {
        private readonly HttpClient _http; private readonly IMonitor _monitor;
        public GeminiProvider(HttpClient http, IMonitor monitor) { _http = http; _monitor = monitor; }

        public async Task<string?> SendChatAsync(ModConfig config, List<ChatMessage> messages)
        {
            var contents = new List<object>(); string? sys = null;
            foreach (var m in messages) { if (m.Role == "system") sys = m.Content; else contents.Add(new { role = m.Role == "assistant" ? "model" : "user", parts = new[] { new { text = m.Content } } }); }
            var body = new Dictionary<string, object> { ["contents"] = contents, ["generationConfig"] = new { temperature = config.Temperature, maxOutputTokens = config.MaxTokens } };
            if (!string.IsNullOrEmpty(sys)) body["systemInstruction"] = new { parts = new[] { new { text = sys } } };
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, config.ApiEndpoint.TrimEnd('/') + "/models/" + config.Model + ":generateContent?key=" + config.ApiKey) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                var resp = await _http.SendAsync(req); var b = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) { _monitor.Log("Gemini error: " + resp.StatusCode, LogLevel.Error); return null; }
                return OpenAIProvider.JsonPathExtract(b, "candidates[0].content.parts[0].text");
            }
            catch (Exception ex) { _monitor.Log("Gemini: " + ex.Message, LogLevel.Error); return null; }
        }
        public async Task<string?> SendSimpleAsync(ModConfig config, string prompt) => await SendChatAsync(config, new List<ChatMessage> { new ChatMessage("user", prompt) });
    }

    public class AzureOpenAIProvider : IAIProvider
    {
        private readonly HttpClient _http; private readonly IMonitor _monitor;
        public AzureOpenAIProvider(HttpClient http, IMonitor monitor) { _http = http; _monitor = monitor; }

        public async Task<string?> SendChatAsync(ModConfig config, List<ChatMessage> messages)
        {
            var body = new { messages = messages.ConvertAll(m => new { role = m.Role, content = m.Content }), max_tokens = config.MaxTokens, temperature = config.Temperature };
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, config.ApiEndpoint.TrimEnd('/') + "/chat/completions?api-version=2024-08-01-preview") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                req.Headers.Add("api-key", config.ApiKey);
                var resp = await _http.SendAsync(req); var b = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) { _monitor.Log("Azure error: " + resp.StatusCode, LogLevel.Error); return null; }
                return OpenAIProvider.JsonPathExtract(b, "choices[0].message.content");
            }
            catch (Exception ex) { _monitor.Log("Azure: " + ex.Message, LogLevel.Error); return null; }
        }
        public async Task<string?> SendSimpleAsync(ModConfig config, string prompt) => await SendChatAsync(config, new List<ChatMessage> { new ChatMessage("user", prompt) });
    }
}
