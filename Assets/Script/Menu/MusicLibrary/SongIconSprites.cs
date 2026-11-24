using System;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;
using YARG.Core.Logging;

namespace YARG.Menu.MusicLibrary
{
    /// <summary>
    /// A runtime-built TMP sprite asset with small inline icons for song names:
    /// a video glyph (screen with a play triangle) and a custom stems glyph (music note with sparkles).
    /// Icons are white and tinted with the text colour.
    /// </summary>
    public static class SongIconSprites
    {
        public const string VideoTag = "<sprite name=\"video\" tint=1>";
        public const string CustomStemsTag = "<sprite name=\"stems\" tint=1>";

        private const int Cell = 128;
        private const int Supersample = 4;

        private delegate bool InsideTest(float x, float y);

        private static readonly (string Name, InsideTest Shape)[] Icons =
        {
            ("video", InsideVideo),
            ("stems", InsideStems),
        };

        private static TMP_SpriteAsset _asset;

        public static TMP_SpriteAsset Asset => _asset ??= Create();

        /// <summary>
        /// Lets the given text resolve these icons. Sprites missing from this asset
        /// (e.g. instrument icons) still resolve through TMP's default sprite asset.
        /// </summary>
        public static void Register(TMP_Text text)
        {
            if (text == null)
            {
                return;
            }

            var current = text.spriteAsset;
            if (current == null || current == TMP_Settings.defaultSpriteAsset)
            {
                text.spriteAsset = Asset;
            }
            // A text with its own custom sprite asset is left alone: adding a runtime fallback
            // to a project asset would leak into the asset file in the editor.
        }

        private static TMP_SpriteAsset Create()
        {
            var texture = DrawAtlas();

            var asset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
            asset.name = "Song Icon Sprite Asset";
            asset.hideFlags = HideFlags.DontSave;
            asset.spriteSheet = texture;

            var defaultAsset = TMP_Settings.defaultSpriteAsset;
            var material = defaultAsset != null && defaultAsset.material != null
                ? new Material(defaultAsset.material)
                : new Material(Shader.Find("TextMeshPro/Sprite"));
            material.name = "Song Icon Sprite Material";
            material.hideFlags = HideFlags.DontSave;
            material.SetTexture(ShaderUtilities.ID_MainTex, texture);
            asset.material = material;

            // TMP treats a sprite asset with an empty version as legacy and "upgrades" it inside
            // UpdateLookupTables, which wipes the glyph/character tables built below. The setter
            // is internal, so mark it as current through reflection.
            MarkAsCurrentVersion(asset);

            for (int i = 0; i < Icons.Length; i++)
            {
                // The drawings fill roughly the middle of each cell; this places them around cap height.
                var metrics = new GlyphMetrics(Cell, Cell, 0f, Cell * 0.95f, Cell * 1.08f);
                var glyph = new TMP_SpriteGlyph((uint) i, metrics, new GlyphRect(i * Cell, 0, Cell, Cell), 1f, 0);
                // Private-use code points; the sprites are looked up by name anyway.
                var character = new TMP_SpriteCharacter(0xE100u + (uint) i, asset, glyph)
                {
                    name = Icons[i].Name,
                };

                asset.spriteGlyphTable.Add(glyph);
                asset.spriteCharacterTable.Add(character);
            }

            asset.UpdateLookupTables();
            return asset;
        }

        private static void MarkAsCurrentVersion(TMP_SpriteAsset asset)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var property = typeof(TMP_Asset).GetProperty("version", flags);
            var setter = property?.GetSetMethod(true);
            if (setter != null)
            {
                setter.Invoke(asset, new object[] { "1.1.0" });
                return;
            }

            var field = typeof(TMP_Asset).GetField("m_Version", flags);
            if (field != null)
            {
                field.SetValue(asset, "1.1.0");
                return;
            }

            YargLogger.LogWarning("Could not set TMP sprite asset version; song icons may not display");
        }

        /// <summary>
        /// Draws every icon side by side into one atlas, white with supersampled (anti-aliased) alpha.
        /// Shape coordinates are per cell, 0..128 with y pointing up.
        /// </summary>
        private static Texture2D DrawAtlas()
        {
            int width = Cell * Icons.Length;
            var texture = new Texture2D(width, Cell, TextureFormat.RGBA32, true)
            {
                name = "Song Icons",
                hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
            };

            var pixels = new Color32[width * Cell];
            const float step = 1f / Supersample;
            const int samples = Supersample * Supersample;

            for (int i = 0; i < Icons.Length; i++)
            {
                var shape = Icons[i].Shape;
                for (int y = 0; y < Cell; y++)
                {
                    for (int x = 0; x < Cell; x++)
                    {
                        int hits = 0;
                        for (int sy = 0; sy < Supersample; sy++)
                        {
                            for (int sx = 0; sx < Supersample; sx++)
                            {
                                if (shape(x + (sx + 0.5f) * step, y + (sy + 0.5f) * step))
                                {
                                    hits++;
                                }
                            }
                        }

                        byte alpha = (byte) (hits * 255 / samples);
                        pixels[y * width + i * Cell + x] = new Color32(255, 255, 255, alpha);
                    }
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        // ---- Video: rounded screen outline with a play triangle ----

        private static bool InsideVideo(float x, float y)
        {
            const float stroke = 10f;
            float rect = RoundedRectDistance(x, y, 64f, 64f, 56f, 43.5f, 18f);
            if (rect <= 0f && rect >= -stroke)
            {
                return true;
            }

            // Counter-clockwise triangle pointing right
            return EdgeDistance(x, y, 50f, 41f, 88f, 64f) <= 0f
                && EdgeDistance(x, y, 88f, 64f, 50f, 87f) <= 0f
                && EdgeDistance(x, y, 50f, 87f, 50f, 41f) <= 0f;
        }

        // ---- Custom stems: eighth note with sparkles ----

        private static bool InsideStems(float x, float y)
        {
            return InEllipse(x, y, 40f, 34f, 19f, 13.5f, 25f * Mathf.Deg2Rad) // note head
                || (x >= 51f && x <= 59f && y >= 36f && y <= 104f)          // stem
                || InCapsule(x, y, 55f, 101f, 80f, 80f, 5.5f)               // flag
                || InSparkle(x, y, 97f, 97f, 25f)
                || InSparkle(x, y, 105f, 56f, 14f)
                || InSparkle(x, y, 22f, 90f, 10f);
        }

        // ---- Shape helpers ----

        private static float RoundedRectDistance(float x, float y, float cx, float cy, float hx, float hy, float radius)
        {
            float qx = Math.Abs(x - cx) - (hx - radius);
            float qy = Math.Abs(y - cy) - (hy - radius);
            float outside = Mathf.Sqrt(Math.Max(qx, 0f) * Math.Max(qx, 0f) + Math.Max(qy, 0f) * Math.Max(qy, 0f));
            float inside = Math.Min(Math.Max(qx, qy), 0f);
            return outside + inside - radius;
        }

        // Signed distance to the line from -> to; positive on the right-hand side (outside for CCW order).
        private static float EdgeDistance(float x, float y, float fx, float fy, float tx, float ty)
        {
            float ex = tx - fx;
            float ey = ty - fy;
            float length = Mathf.Sqrt(ex * ex + ey * ey);
            return ((x - fx) * ey - (y - fy) * ex) / length;
        }

        private static bool InEllipse(float x, float y, float cx, float cy, float a, float b, float angle)
        {
            float c = Mathf.Cos(angle);
            float s = Mathf.Sin(angle);
            float dx = x - cx;
            float dy = y - cy;
            float u = (dx * c + dy * s) / a;
            float v = (-dx * s + dy * c) / b;
            return u * u + v * v <= 1f;
        }

        private static bool InCapsule(float x, float y, float ax, float ay, float bx, float by, float radius)
        {
            float px = x - ax;
            float py = y - ay;
            float ex = bx - ax;
            float ey = by - ay;
            float t = Mathf.Clamp01((px * ex + py * ey) / (ex * ex + ey * ey));
            float dx = px - ex * t;
            float dy = py - ey * t;
            return dx * dx + dy * dy <= radius * radius;
        }

        // Four-pointed star with concave sides: sqrt(|dx|) + sqrt(|dy|) <= sqrt(r)
        private static bool InSparkle(float x, float y, float cx, float cy, float radius)
        {
            return Mathf.Sqrt(Math.Abs(x - cx) / radius) + Mathf.Sqrt(Math.Abs(y - cy) / radius) <= 1f;
        }
    }
}
