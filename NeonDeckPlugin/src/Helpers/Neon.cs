namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;

    /// <summary>
    /// Neon key-face renderer. Every Neon Deck key is drawn through here so the 3x3 grid reads as one system:
    /// near-black tile, accent glow band, vector icon, title, optional live subtitle / progress ring.
    /// </summary>
    public static class Neon
    {
        public static readonly BitmapColor Bg = new BitmapColor(10, 14, 23);
        public static readonly BitmapColor Panel = new BitmapColor(16, 22, 36);
        public static readonly BitmapColor Text = new BitmapColor(236, 240, 248);
        public static readonly BitmapColor Dim = new BitmapColor(120, 132, 156);

        public static readonly BitmapColor Cyan = new BitmapColor(0, 240, 255);
        public static readonly BitmapColor Magenta = new BitmapColor(255, 43, 214);
        public static readonly BitmapColor Lime = new BitmapColor(182, 255, 0);
        public static readonly BitmapColor Amber = new BitmapColor(255, 184, 0);
        public static readonly BitmapColor Red = new BitmapColor(255, 70, 90);
        public static readonly BitmapColor Coral = new BitmapColor(255, 122, 89);   // Clawd orange
        public static readonly BitmapColor Mint = new BitmapColor(0, 230, 160);     // Codex green
        public static readonly BitmapColor Blurple = new BitmapColor(123, 134, 255); // Discord

        private static readonly ConcurrentDictionary<String, BitmapImage> IconCache = new();

        public static BitmapColor WithAlpha(BitmapColor c, Int32 alpha) => new BitmapColor(c, alpha);

        /// <summary>Rasterise an embedded SVG (Resources\*.svg) to the requested square size. Cached.</summary>
        public static BitmapImage Icon(String fileName, Int32 size)
        {
            return IconCache.GetOrAdd($"{fileName}@{size}", _ =>
            {
                var svg = PluginResources.ReadTextFile(fileName);
                using var vector = BitmapImage.FromSvg(svg);
                return vector.VectorToBitmap(size, size);
            });
        }

        public sealed class Face
        {
            public String Icon;              // e.g. "pomodoro.svg"
            public BitmapColor Accent = Cyan;
            public String Title;             // bottom label
            public String ShortTitle;        // used instead of Title on tiles narrower than 100 px (optional)
            public String Subtitle;          // live value under/over the icon (optional)
            public Double? Progress;         // 0..1 ring around the icon (optional)
            public Boolean Active;           // brighter glow band + border when true
            public Boolean Busy;             // solid dot top-right while working
            public Int32? Heartbeat;         // 0..3 phase of a breathing "alive" dot top-right (optional)
            public String Corner;            // small text in the top glow band, left side (e.g. "↻ 2h 05m")
            public String Corner2;           // optional second small text, right side of the band (e.g. "GPU 31°")
        }

        /// <summary>Compact "resets in" label for a window end time: "↻ 2h 05m", "↻ 45m", "↻ 3d 4h" or "↻ now".</summary>
        public static String ResetsLabel(DateTimeOffset? resetsAt)
        {
            if (resetsAt == null)
            {
                return null;
            }
            var left = resetsAt.Value - DateTimeOffset.UtcNow;
            if (left.TotalMinutes < 1)
            {
                return "↻ now";
            }
            if (left.TotalHours >= 24)
            {
                return $"↻ {(Int32)left.TotalDays}d {left.Hours}h";
            }
            return left.TotalHours >= 1 ? $"↻ {(Int32)left.TotalHours}h {left.Minutes:00}m" : $"↻ {left.Minutes}m";
        }

        /// <summary>Draw a face for the given device image size.</summary>
        public static BitmapImage Draw(PluginImageSize imageSize, Face f)
        {
            using var b = new BitmapBuilder(imageSize);
            var w = b.Width;
            var h = b.Height;
            var pad = Math.Max(2, w / 24);

            b.Clear(Bg);

            // accent hairline at the top (the translucent glow band was dropped: it collided with the rings)
            b.FillRectangle(0, 0, w, Math.Max(2, h / 22), f.Active ? f.Accent : WithAlpha(f.Accent, 170));
            if (f.Active)
            {
                b.DrawRectangle(0, 0, w - 1, h - 1, WithAlpha(f.Accent, 200));
            }

            var hasSub = !String.IsNullOrEmpty(f.Subtitle);
            var hasCorner = !String.IsNullOrEmpty(f.Corner);
            // with a corner label the text rows get a little tighter and the ring/icon starts fully below the label
            var titleH = hasCorner ? (Int32)(h * 0.17) : h / 5;
            var subH = hasSub ? (hasCorner ? (Int32)(h * 0.17) : h / 5) : 0;
            var bandTop = Math.Max(2, h / 22);
            var bandH = h / 5;
            var iconTop = hasCorner ? bandTop + bandH + pad / 2 : pad; // ring starts fully below the glow band
            var iconArea = h - titleH - subH - iconTop - pad;
            var iconSize = Math.Max(12, (Int32)(iconArea * (hasSub ? 0.78 : 0.72)));
            var iconX = (w - iconSize) / 2;
            var iconY = iconTop + (iconArea - iconSize) / 2;

            if (f.Progress.HasValue)
            {
                var r = Math.Min(w, iconArea) / 2 - pad / 2;
                var cx = w / 2;
                var cy = iconTop + iconArea / 2;
                var stroke = Math.Max(2f, w / 26f);
                b.DrawArc(cx, cy, r, -90, 360, WithAlpha(f.Accent, 60), stroke);
                var sweep = (Single)(Math.Clamp(f.Progress.Value, 0, 1) * 360);
                if (sweep > 0.5f)
                {
                    b.DrawArc(cx, cy, r, -90, sweep, f.Accent, stroke);
                }
                iconSize = (Int32)(iconSize * 0.62);
                iconX = (w - iconSize) / 2;
                iconY = cy - iconSize / 2;
            }

            if (!String.IsNullOrEmpty(f.Icon))
            {
                try
                {
                    b.DrawImage(Icon(f.Icon, iconSize), iconX, iconY, BitmapRotation.None);
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"icon draw failed: {f.Icon}");
                }
            }

            if (f.Busy)
            {
                var r = Math.Max(3f, w / 18f);
                b.FillCircle(w - pad - r, pad + r + h / 22, r, f.Accent);
            }
            else if (f.Heartbeat.HasValue)
            {
                // breathing dot: bright -> dim -> faint -> dim, plus a "ping" ring on the bright beat
                var phase = ((f.Heartbeat.Value % 4) + 4) % 4;
                var alpha = phase == 0 ? 255 : phase == 2 ? 60 : 140;
                var r = Math.Max(2.5f, w / 22f);
                var cx = w - pad - r - 1;
                var cy = pad + r + h / 22 + 1;
                b.FillCircle(cx, cy, r, WithAlpha(f.Accent, alpha));
                if (phase == 0)
                {
                    b.DrawCircle(cx, cy, r + Math.Max(2f, w / 40f), WithAlpha(f.Accent, 110));
                }
            }

            var (fontSize, lineHeight, spaceHeight) = BitmapBuilder.GetDefaultFontMetrics(imageSize);
            if (hasCorner)
            {
                // sits in the glow band, left of the heartbeat dot
                var cornerFont = Math.Max(8, fontSize - 3);
                var cornerH = (Int32)(bandH * 0.75);
                if (!String.IsNullOrEmpty(f.Corner2))
                {
                    // two values on one centred line ("CPU 59° · GPU 31°"), slightly smaller so it never wraps
                    b.DrawText($"{f.Corner} · {f.Corner2}", 0, bandTop - 1, w, cornerH, WithAlpha(Text, 235), Math.Max(8, fontSize - 5), lineHeight, spaceHeight, null);
                }
                else
                {
                    // single label: centred on the key (the heartbeat dot hugs the right edge and is short enough not to collide)
                    b.DrawText(f.Corner, 0, bandTop - 1, w, cornerH, WithAlpha(Text, 235), cornerFont, lineHeight, spaceHeight, null);
                }
            }
            if (hasSub)
            {
                var subY = iconTop + iconArea;
                b.DrawText(f.Subtitle, 0, subY, w, subH, f.Accent, fontSize + 2, lineHeight, spaceHeight, null);
            }
            var title = w < 100 && !String.IsNullOrEmpty(f.ShortTitle) ? f.ShortTitle : f.Title;
            if (!String.IsNullOrEmpty(title))
            {
                // Long titles ("Claude Code") must stay on one line even on the 80 px tiles.
                var titleFont = title.Length > 10 ? Math.Max(8, fontSize - 3) : title.Length > 8 ? Math.Max(8, fontSize - 1) : fontSize;
                b.DrawText(title, pad / 2, h - titleH - pad / 2, w - pad, titleH, Text, titleFont, lineHeight, spaceHeight, null);
            }

            var img = b.ToImage();
            Snapshot(f, imageSize, img);
            return img;
        }

        private static void Snapshot(Face f, PluginImageSize size, BitmapImage img)
        {
            if (!DeckConfig.Current.DebugSnapshots || NeonDeckPlugin.Instance == null || String.IsNullOrEmpty(f.Icon))
            {
                return;
            }
            try
            {
                var name = Path.GetFileNameWithoutExtension(f.Icon);
                var path = Path.Combine(NeonDeckPlugin.Instance.SnapshotDirectory, $"{name}_{size}.png");
                img.SaveToFile(path);
            }
            catch
            {
                // debug aid only
            }
        }
    }
}
