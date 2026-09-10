namespace Loupedeck.NeonDeckPlugin
{
    using System;

    /// <summary>
    /// Boot splash: Clawd strolls across the whole 3x3 panel (head and eyes in the middle row, feet in the bottom
    /// row, so the bezel cuts through his body and the eye completes it), the floor lights up behind him and the
    /// deck name fades in on the top row. Then the keys fade in from the dark.
    /// </summary>
    public sealed class SplashScene : Panel.Scene
    {
        private const Double Walk = 3.0;
        private const Double Reveal = 0.7;
        private const Int32 ClawdSize = 300;          // Clawd's 96-unit art scaled up: body ~160 px tall
        private const Int32 ClawdY = 159;             // body 221..384: straddles the gap between rows 1 and 2
        private const Int32 FloorY = ClawdY + ClawdSize * 72 / 96 + 3;

        public override Double Duration => Walk + Reveal;

        public override Int32 Fps => 10;

        public override Boolean DrawFrame(BitmapBuilder b, Double t)
        {
            if (t >= Walk)
            {
                return false;
            }
            var p = t / Walk;
            var x = (Int32)(-ClawdSize + p * (Panel.Width + 2 * ClawdSize));   // off-screen left -> off-screen right
            var lit = Math.Clamp(x + ClawdSize / 2, 0, Panel.Width);           // the floor he has already walked

            // floor: dim ahead of him, bright behind him, with a soft glow under the bright part
            b.FillRectangle(0, FloorY, Panel.Width, 2, Neon.WithAlpha(Neon.Cyan, 60));
            b.FillRectangle(0, FloorY - 2, lit, 6, Neon.WithAlpha(Neon.Cyan, 40));
            b.FillRectangle(0, FloorY, lit, 2, Neon.Cyan);

            // deck name on the top row, one word per key so the bezels fall between the words, fading in as he walks
            var alpha = (Int32)(255 * Math.Clamp(p * 1.6, 0, 1));
            var textColor = Neon.WithAlpha(Neon.Text, alpha);
            var lineColor = Neon.WithAlpha(Neon.Magenta, alpha);
            for (var i = 0; i < 2; i++)
            {
                var cx = Panel.CellX(i);
                b.DrawText(i == 0 ? "NEON" : "DECK", cx, Panel.OriginY + 26, Panel.TileSize, 62, textColor, 40, 48, 8, null);
                b.FillRectangle(cx + 14, Panel.OriginY + 92, Panel.TileSize - 28, 2, lineColor);
            }
            b.DrawText(DateTime.Now.ToString("HH:mm"), Panel.CellX(2), Panel.OriginY + 34, Panel.TileSize, 50, Neon.WithAlpha(Neon.Dim, alpha), 30, 36, 8, null);

            // Clawd: 8 walking frames, cycling 8 times a second
            var frame = (Int32)(t * 8) % 8;
            b.DrawImage(Neon.Icon($"clawd_f{frame}.svg", ClawdSize), x, ClawdY, BitmapRotation.None);
            return true;
        }

        public override BitmapColor Overlay(Double t)
        {
            if (t < Walk)
            {
                return BitmapColor.Transparent;
            }
            var a = 1 - Math.Clamp((t - Walk) / Reveal, 0, 1);
            return Neon.WithAlpha(Neon.Bg, (Int32)(255 * a));
        }
    }

    /// <summary>Every key pulses in one colour on top of its face: the whole panel "breathes" red when a quota hits 100 %.</summary>
    public sealed class AlertScene : Panel.Scene
    {
        private readonly BitmapColor _color;
        private readonly Int32 _pulses;
        private readonly Double _period;

        public AlertScene(BitmapColor color, Int32 pulses = 3, Double period = 0.6)
        {
            this._color = color;
            this._pulses = pulses;
            this._period = period;
        }

        public override Double Duration => this._pulses * this._period;

        public override Int32 Fps => 12;

        public override Boolean DrawFrame(BitmapBuilder b, Double t) => false;

        public override BitmapColor Overlay(Double t)
        {
            var phase = (t % this._period) / this._period;
            var a = Math.Sin(phase * Math.PI);
            return Neon.WithAlpha(this._color, (Int32)(a * 185));
        }
    }
}
