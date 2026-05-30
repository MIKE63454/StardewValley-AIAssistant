using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace AIAssistant
{
    public class ChatHistoryEntry
    {
        public string Time { get; set; } = "";
        public string Sender { get; set; } = "";
        public string Text { get; set; } = "";
        public bool IsAI { get; set; }
        public ChatHistoryEntry() { }
        public ChatHistoryEntry(string text, bool isAI, string sender, string time)
        { Text = text; IsAI = isAI; Sender = sender; Time = time; }
    }

    public class ChatHistorySaveData
    {
        public List<ChatHistoryEntry> Entries { get; set; } = new();
    }

    public class ChatHistoryMenu : IClickableMenu
    {
        private readonly List<ChatHistoryEntry> _entries;
        private readonly List<int> _heights = new();
        private int _totalH, _scroll, _maxScroll;
        private float _fade;

        private const int W = 880;
        private const int H = 640;
        private const int M = 48;         // margin
        private const int TOP = 56;       // header height
        private const int LH = 20;
        private const int MH = 40;
        private const int GAP = 2;
        private const float SC = 0.88f;

        private static readonly Color Paper = new Color(248, 240, 225);
        private static readonly Color Border = new Color(80, 50, 30);
        private static readonly Color TimeGray = new Color(140, 120, 100);

        public ChatHistoryMenu(List<ChatHistoryEntry> entries) : base(
            (Game1.uiViewport.Width - W) / 2,
            (Game1.uiViewport.Height - H) / 2, W, H)
        {
            _entries = entries ?? new();
            Recalc();
            _scroll = Math.Max(0, _totalH - ContentH);
            _maxScroll = Math.Max(0, _totalH - ContentH);
        }

        private int CX => xPositionOnScreen + M;
        private int CY => yPositionOnScreen + M + TOP;
        private int CW => W - M * 2 - 12;  // -16 for scrollbar
        private int ContentH => H - M * 2 - TOP;

        private void Recalc()
        {
            _heights.Clear(); _totalH = 0;
            float tw = Game1.smallFont.MeasureString("00:00 ").X * 0.8f;
            int mw = CW - 8 - (int)tw - 4;
            foreach (var e in _entries)
            {
                int n = Wrap(e.Sender + ": " + e.Text, mw).Count;
                int h = MH + (n - 1) * LH;
                _heights.Add(h); _totalH += h + GAP;
            }
            if (_totalH > 0) _totalH -= GAP;
        }

        private static List<string> Wrap(string t, int mw)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(t)) { lines.Add(""); return lines; }
            var tokens = Toks(t);
            var cur = "";
            foreach (var tok in tokens)
            {
                if (tok == "\n") { lines.Add(cur); cur = ""; continue; }
                var test = cur.Length == 0 ? tok : cur + tok;
                if (Game1.smallFont.MeasureString(test).X * SC > mw && cur.Length > 0)
                { lines.Add(cur); cur = tok.TrimStart(); }
                else cur = test;
            }
            if (cur.Length > 0) lines.Add(cur);
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        private static List<string> Toks(string s)
        {
            var r = new List<string>(); var b = "";
            foreach (char c in s)
            {
                if (CJ(c)) { if (b.Length > 0) { r.Add(b); b = ""; } r.Add(c.ToString()); }
                else if (c == ' ') { if (b.Length > 0) { r.Add(b); b = ""; } r.Add(" "); }
                else if (c == '\n') { if (b.Length > 0) { r.Add(b); b = ""; } r.Add("\n"); }
                else b += c;
            }
            if (b.Length > 0) r.Add(b);
            return r;
        }
        private static bool CJ(char c) => (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x3000 && c <= 0x303F) || (c >= 0xFF00 && c <= 0xFFEF) || (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF) || (c >= 0xAC00 && c <= 0xD7AF);

        public override void receiveScrollWheelAction(int d)
        { _scroll = Math.Clamp(_scroll - d * 36, 0, _maxScroll); }

        public override void receiveKeyPress(Keys k)
        {
            if (k == Keys.Escape || k == Keys.C) { exitThisMenu(); return; }
            if (k == Keys.Down) _scroll = Math.Min(_maxScroll, _scroll + 36);
            if (k == Keys.Up) _scroll = Math.Max(0, _scroll - 36);
            if (k == Keys.PageDown) _scroll = Math.Min(_maxScroll, _scroll + ContentH);
            if (k == Keys.PageUp) _scroll = Math.Max(0, _scroll - ContentH);
            if (k == Keys.Home) _scroll = 0;
            if (k == Keys.End) _scroll = _maxScroll;
        }

        public override void update(GameTime t)
        { base.update(t); if (_fade < 1f) _fade += 0.08f; }

        public override void draw(SpriteBatch b)
        {
            int x = xPositionOnScreen, y = yPositionOnScreen;

            // Dim backdrop
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.55f * _fade);

            // Paper background
            DrawRect(b, x, y, W, H, Paper * _fade);

            // 1px border
            DrawLine(b, x, y, x + W, y, Border * 0.5f * _fade);
            DrawLine(b, x, y + H, x + W, y + H, Border * 0.5f * _fade);
            DrawLine(b, x, y, x, y + H, Border * 0.5f * _fade);
            DrawLine(b, x + W, y, x + W, y + H, Border * 0.5f * _fade);

            // Decorative line under header
            DrawLine(b, x + M, y + M + TOP - 12, x + W - M, y + M + TOP - 12, Border * 0.18f * _fade);

            // Title
            b.DrawString(Game1.dialogueFont, "聊天记录",
                new Vector2(x + M, y + 18), Border * 0.8f * _fade, 0f, Vector2.Zero, 0.95f, SpriteEffects.None, 0f);

            // Subtitle
            var sub = $"共 {_entries.Count} 条  |  滚轮/方向键翻阅  |  C/ESC 关闭";
            b.DrawString(Game1.smallFont, sub,
                new Vector2(x + M, y + 42), Border * 0.45f * _fade, 0f, Vector2.Zero, 0.9f, SpriteEffects.None, 0f);

            // Content area
            int cx = CX, cy = CY, cw = CW, ch = ContentH;

            // Scrollbar
            if (_maxScroll > 0)
            {
                var track = new Rectangle(cx + cw + 4, cy, 8, ch);
                b.Draw(Game1.staminaRect, track, Border * 0.12f * _fade);
                int th = Math.Max(24, (int)((float)ch * ch / (_totalH + ch)));
                int ty = track.Y + (int)((track.Height - th) * (float)_scroll / _maxScroll);
                b.Draw(Game1.staminaRect, new Rectangle(track.X + 1, ty, 6, th), Border * 0.4f * _fade);
            }

            // Scissor
            var os = b.GraphicsDevice.ScissorRectangle;
            b.End();
            b.GraphicsDevice.ScissorRectangle = new Rectangle(cx, cy, cw, ch);
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null,
                new RasterizerState { ScissorTestEnable = true });

            if (_entries.Count > 0 && _heights.Count == _entries.Count)
            {
                int dy = cy - _scroll;
                float tw = Game1.smallFont.MeasureString("00:00 ").X * 0.8f;
                int textW = cw - 8 - (int)tw - 4; // actual text width after time prefix

                for (int i = 0; i < _entries.Count; i++)
                {
                    int eh = _heights[i];
                    if (dy + eh > cy && dy < cy + ch)
                    {
                        var e = _entries[i];

                        // Row background
                        if (i % 2 == 0)
                            b.Draw(Game1.staminaRect, new Rectangle(cx + 2, dy + 1, cw - 4, eh - 2),
                                new Color(200, 180, 150) * 0.15f * _fade);

                        int by = dy + 6;

                        // Time
                        b.DrawString(Game1.smallFont, e.Time,
                            new Vector2(cx + 8, by), TimeGray * _fade, 0f, Vector2.Zero, 0.8f, SpriteEffects.None, 0f);

                        // Content
                        var lines = Wrap(e.Sender + ": " + e.Text, textW);
                        var color = e.IsAI ? Color.DarkGoldenrod : Border * 0.85f;
                        for (int li = 0; li < lines.Count; li++)
                            b.DrawString(Game1.smallFont, lines[li],
                                new Vector2(cx + 8 + tw, by + li * LH),
                                color * _fade, 0f, Vector2.Zero, SC, SpriteEffects.None, 0f);
                    }
                    dy += eh + GAP;
                }
            }

            b.End();
            b.GraphicsDevice.ScissorRectangle = os;
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null);

            // Empty
            if (_entries.Count == 0)
            {
                var et = "暂无聊天记录";
                var es = Game1.smallFont.MeasureString(et);
                b.DrawString(Game1.smallFont, et,
                    new Vector2(x + W / 2 - es.X / 2, cy + ch / 2 - 14),
                    TimeGray * _fade, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
            }

            drawMouse(b);
        }

        private static void DrawRect(SpriteBatch b, int x, int y, int w, int h, Color c)
        { b.Draw(Game1.staminaRect, new Rectangle(x, y, w, h), c); }

        private static void DrawLine(SpriteBatch b, int x1, int y1, int x2, int y2, Color c)
        { b.Draw(Game1.staminaRect, new Rectangle(x1, y1, x2 - x1, y2 - y1), c); }
    }
}