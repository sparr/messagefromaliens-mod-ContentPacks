using System;
using System.Collections.Generic;
using HarmonyLib;
using Shapes2D;
using UnityEngine;
using UnityEngine.Rendering;
using Shape = Shapes2D.Shape;

namespace ContentPacks
{
    // Draws sprites for words whose shape the game has no sprite for.
    //
    // The game draws each glyph on its pages from a sprite baked from the word's strokes when the
    // game was built. A word that packs added has none, and a word whose shape packs changed has
    // one of its old shape. So does every word built from such a word, at any depth. Once the game
    // has loaded, each of those words is drawn with Shapes2D the way the baked sprites were: with
    // Draw.DrawPath1, into a 100 by 100 pixel target at 100 pixels per unit. The alpha is read
    // back as an Alpha8 texture, which is lossless and as small as the game's compressed sprites.
    //
    // Alpha8 reads as black on the GPU, so slots showing these sprites use the UI/Default shader
    // with white added to the texture color, which draws them exactly as the game's sprite shader
    // draws a white sprite.
    internal static class GlyphRenderer
    {
        // The word table before any pack changed it. Set by PackLoader.
        internal static string OriginalTable;

        private const float PixelsPerUnit = 100f;
        private const int RenderLayer = 31;

        private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();
        // Words shown with one of the game's own sprites, which use the game's material.
        private static readonly Dictionary<string, Sprite> stockSprites = new Dictionary<string, Sprite>();
        private const string SolidCode = "solid";
        private const string SpritePath = "Shapes2D Sprites/";
        private static Material alphaMaterial;
        private static Material spriteMaterial;
        private static bool started;

        // Called every frame until the game has loaded and the glyphs are drawn.
        internal static void Tick()
        {
            if (started)
            {
                return;
            }
            var m = Global.manager;
            if (m == null || Global.alien_text == null || Global.alien_text.Count == 0 || m.alien_slots == null || m.alien_slots[0, 0] == null)
            {
                return;
            }
            started = true;
            try
            {
                Begin(m);
            }
            catch (Exception e)
            {
                Log.Error($"Glyph rendering failed: {e}");
            }
        }

        private static void Begin(Draw m)
        {
            if (!Plugin.LoadContentPacks.Value || OriginalTable == null
                || !Global.textfiles.TryGetValue(WordTableMerge.Target, out var table) || table == OriginalTable)
            {
                return;
            }
            var before = Rows(OriginalTable);
            var after = Rows(table);
            if (before == null || after == null)
            {
                Log.Error($"Glyphs: the word table has no {WordTableColumns.Struct}, {WordTableColumns.Part1}, {WordTableColumns.Part2}, or {WordTableColumns.Modifier} column; no glyphs drawn");
                return;
            }
            var words = WordsToRender(before, after, out var added, out var changed);
            if (words.Count == 0)
            {
                return;
            }
            var total = words.Count;
            spriteMaterial = m.alien_slots[0, 0].sp.sharedMaterial;
            // The game's one solid word has a sprite of a half-transparent filled cell. Every solid
            // word is shown with it, with the game's own material.
            var cursor = SolidSprite(before);
            var solids = 0;
            if (cursor != null)
            {
                solids = words.RemoveAll(w =>
                {
                    if (!IsSolid(after[w]))
                    {
                        return false;
                    }
                    stockSprites[w] = cursor;
                    return true;
                });
            }
            Log.Info($"Glyphs: new sprites needed for {added} new, {changed} changed, and {total - added - changed} built from a changed glyph; shared solid sprite: {solids}; to draw: {words.Count}");
            if (words.Count == 0)
            {
                Global.manager?.UpdateText();
                return;
            }
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                Log.Error($"Glyphs: this system can't read rendered glyphs back, so {words.Count} words keep the game's image for them");
                return;
            }
            var shader = Shader.Find("UI/Default");
            if (shader == null)
            {
                Log.Error($"Glyphs: the game has no UI/Default shader, so {words.Count} words keep the game's image for them");
                return;
            }
            alphaMaterial = new Material(shader) { name = "ContentPacks glyph" };
            alphaMaterial.SetColor("_TextureSampleAdd", new Color(1f, 1f, 1f, 0f));

            var camObject = new GameObject("ContentPacks glyph camera");
            var cam = camObject.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.cullingMask = 1 << RenderLayer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(1f, 1f, 1f, 0f);
            var failed = 0;
            try
            {
                foreach (var word in words)
                {
                    if (!Render(m, cam, word, IsSolid(after[word])))
                    {
                        failed++;
                    }
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(camObject);
            }
            // The page may have been drawn before the sprites.
            Log.Info(failed == 0 ? $"Glyphs: {sprites.Count} drawn" : $"Glyphs: {sprites.Count} drawn, {failed} kept the game's image");
            Global.manager?.UpdateText();
        }

        // As the game reads the struct column in Global.Init: case-sensitive, as written.
        private static bool IsSolid(ShapeColumns shape) => shape.Struct == SolidCode;

        // The stock sprite of a solid word in the game's own table, or null if there is none.
        private static Sprite SolidSprite(Dictionary<string, ShapeColumns> original)
        {
            foreach (var kv in original)
            {
                if (IsSolid(kv.Value))
                {
                    var sprite = Resources.Load<Sprite>(SpritePath + kv.Key);
                    if (sprite != null)
                    {
                        return sprite;
                    }
                }
            }
            return null;
        }

        // Words that are new, whose shape columns differ from the game's table, or that are built
        // from such a word at any depth.
        private static List<string> WordsToRender(Dictionary<string, ShapeColumns> before, Dictionary<string, ShapeColumns> after,
            out int added, out int changed)
        {
            added = 0;
            changed = 0;
            var users = new Dictionary<string, List<string>>();
            foreach (var kv in after)
            {
                foreach (var part in DrawnFrom(kv.Key, kv.Value))
                {
                    if (part.Length > 0 && part != kv.Key && after.ContainsKey(part))
                    {
                        if (!users.TryGetValue(part, out var list))
                        {
                            users[part] = list = new List<string>();
                        }
                        list.Add(kv.Key);
                    }
                }
            }
            var result = new List<string>();
            var seen = new HashSet<string>();
            var queue = new Queue<string>();
            foreach (var kv in after)
            {
                var isNew = !before.TryGetValue(kv.Key, out var old);
                if (isNew || !SameShape(old, kv.Value))
                {
                    if (isNew) added++; else changed++;
                    seen.Add(kv.Key);
                    queue.Enqueue(kv.Key);
                }
            }
            while (queue.Count > 0)
            {
                var word = queue.Dequeue();
                if (Global.alien_text.ContainsKey(word))
                {
                    result.Add(word);
                }
                if (users.TryGetValue(word, out var list))
                {
                    foreach (var user in list)
                    {
                        if (seen.Add(user))
                        {
                            queue.Enqueue(user);
                        }
                    }
                }
            }
            return result;
        }

        // The words a word's glyph is drawn from: its components, or for a number word, the word
        // named by the part before its e, as Global.StringToCharacter draws it.
        private static IEnumerable<string> DrawnFrom(string word, ShapeColumns shape)
        {
            if (shape.Struct == NumberCode)
            {
                var halves = word.Split('e');
                if (halves.Length == 2 && int.TryParse(halves[1], out _))
                {
                    yield return halves[0];
                }
                yield break;
            }
            yield return shape.Part1;
            yield return shape.Part2;
        }

        private const string NumberCode = "exp";

        // The columns that set a word's shape.
        private struct ShapeColumns
        {
            public string Struct, Part1, Part2, Modifier;
        }

        private static bool SameShape(ShapeColumns a, ShapeColumns b) =>
            a.Struct == b.Struct && a.Part1 == b.Part1 && a.Part2 == b.Part2 && a.Modifier == b.Modifier;

        // The table's shape columns by word, or null if it can't be parsed. Later rows win, as in
        // the game.
        private static Dictionary<string, ShapeColumns> Rows(string text)
        {
            var table = WordTable.Parse(text, out _);
            if (table == null)
            {
                return null;
            }
            var cols = table.Columns;
            var rows = new Dictionary<string, ShapeColumns>();
            foreach (var cells in table.Rows)
            {
                var word = cells[cols.WordCol];
                if (word.Length == 0)
                {
                    continue;
                }
                rows[word] = new ShapeColumns
                {
                    Struct = cells[cols.StructCol],
                    Part1 = cells[cols.Part1Col],
                    Part2 = cells[cols.Part2Col],
                    Modifier = cells[cols.ModifierCol],
                };
            }
            return rows;
        }

        // Draws one word and makes its sprite, or logs why it has none. Returns whether it has one.
        private static bool Render(Draw m, Camera cam, string word, bool solid)
        {
            var problem = DrawWord(m, cam, word, solid, out var error);
            if (problem == null)
            {
                return true;
            }
            var message = $"{Origin(word)}: {problem}, so it keeps the game's image for it";
            if (error == null)
            {
                Log.Warning(message);
            }
            else
            {
                Log.Error(message);
                Log.Debug(error.ToString());
            }
            return false;
        }

        // Draws one word with the game's glyph drawing code, reads each piece back as it is
        // drawn, and makes the word's sprite. Returns what went wrong, or null.
        private static string DrawWord(Draw m, Camera cam, string word, bool solid, out Exception error)
        {
            error = null;
            var shapes = new List<Shape>();
            RenderTexture rt = null;
            try
            {
                if (solid)
                {
                    // Only when the game has no solid sprite to share: the game's one solid word
                    // was baked as a filled rectangle over the whole cell, in palette color -2,
                    // half-transparent white, not from its strokes.
                    var rect = m.DrawRect(1f, 1f, Far, -2, fill: true);
                    // Palette color -2 has alpha 0.5, which this render rounds to 128 of 255; the
                    // baked sprite has 127, so use that.
                    rect.settings.outlineColor = new Color(1f, 1f, 1f, 127f / 255f);
                    shapes.Add(rect);
                }
                else
                {
                    var strokes = Global.StringToCharacter(word);
                    // An empty stroke draws nothing, but DrawPath1 draws nothing at all when the
                    // first one is empty.
                    strokes.RemoveAll(path => path.Count == 0);
                    var count = CountSegments(strokes);
                    if (count == 0)
                    {
                        return "its strokes give no line segments";
                    }
                    if (count <= Shape.MaxPathSegments && count != BrokenSegments)
                    {
                        shapes.Add(m.DrawPath1(strokes, Far, 0, -1f, default, true, false));
                    }
                    else
                    {
                        Log.Debug($"{Origin(word)}: its glyph has {count} line segments, which Shapes2D cannot draw as one path, so it is drawn in pieces");
                        var extent = Extent(strokes);
                        for (var start = 0; start < count; start += PieceSegments)
                        {
                            shapes.Add(m.DrawPath1(PieceStrokes(strokes, extent, start), Far, 0, -1f, default, true, false));
                        }
                    }
                }
                foreach (var shape in shapes)
                {
                    // Shapes2D sets a shape up in Start, a frame after it's made; this is needed now.
                    shape.Configure();
                    if (shape.settings.shapeType == ShapeType.Path)
                    {
                        shape.settings.outlineColor = Color.white;
                    }
                }
                // Every piece has the same quad, so they share the framing.
                var bounds = shapes[0].GetShapeBounds();
                var pixels = shapes[0].GetShapePixelSize(PixelsPerUnit);
                var w = (int)pixels.x;
                var h = (int)pixels.y;
                var pivot = shapes[0].GetPivot();
                var alpha = new byte[w * h];
                cam.transform.position = new Vector3(bounds.center.x, bounds.center.y, bounds.center.z - 10f);
                cam.orthographicSize = bounds.size.y / 2f;
                cam.aspect = (float)w / h;

                foreach (var shape in shapes)
                {
                    // A pooled target without a depth buffer: the shapes are flat and drawn alone.
                    rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                    cam.targetTexture = rt;
                    // As Shape.DrawToTexture2D does: draw the shape alone, without blending, so the
                    // target holds its exact alpha.
                    var layer = shape.gameObject.layer;
                    shape.gameObject.layer = RenderLayer;
                    shape.ComputeAndApply(disableBlending: true);
                    cam.Render();
                    shape.gameObject.layer = layer;
                    cam.targetTexture = null;
                    try
                    {
                        var request = AsyncGPUReadback.Request(rt, 0, TextureFormat.Alpha8);
                        request.WaitForCompletion();
                        if (request.hasError)
                        {
                            return "reading it back from the GPU failed";
                        }
                        // A piece's alpha falls off with distance from its nearest segment, so the
                        // highest alpha over the pieces is the whole path's.
                        var data = request.GetData<byte>();
                        for (var i = 0; i < alpha.Length; i++)
                        {
                            if (data[i] > alpha[i]) alpha[i] = data[i];
                        }
                    }
                    catch (Exception e)
                    {
                        error = e;
                        return $"reading it back failed: {e.Message}";
                    }
                    RenderTexture.ReleaseTemporary(rt);
                    rt = null;
                }
                return MakeSprite(word, alpha, w, h, pivot, out error);
            }
            catch (Exception e)
            {
                error = e;
                return $"drawing it failed: {e.Message}";
            }
            finally
            {
                cam.targetTexture = null;
                if (rt != null)
                {
                    RenderTexture.ReleaseTemporary(rt);
                }
                foreach (var shape in shapes)
                {
                    if (shape != null)
                    {
                        UnityEngine.Object.DestroyImmediate(shape.gameObject);
                    }
                }
            }
        }

        // Makes a word's sprite from its alpha. Returns what went wrong, or null.
        private static string MakeSprite(string word, byte[] alpha, int w, int h, Vector2 pivot, out Exception error)
        {
            error = null;
            try
            {
                var tex = new Texture2D(w, h, TextureFormat.Alpha8, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    name = "ContentPacks glyph",
                };
                tex.LoadRawTextureData(alpha);
                tex.Apply(false, true);
                sprites[word] = Sprite.Create(tex, new Rect(0, 0, w, h), pivot, PixelsPerUnit);
                return null;
            }
            catch (Exception e)
            {
                error = e;
                return $"making its sprite failed: {e.Message}";
            }
        }

        // Where a word came from, for log messages, without naming it.
        private static string Origin(string word) =>
            WordTableMerge.Origins.TryGetValue(word, out var origin)
                ? origin
                : "Glyphs: a word built from a changed glyph, or changed by a replacement or patch";

        private static readonly Vector3 Far = new Vector3(1000f, 1000f, 0f);

        // For a path of exactly this many segments, Shapes2D turns on both its 24- and its
        // 32-segment shader variants (Shape.ApplyShaderPropertiesToMaterial), and the 24-segment
        // one draws, leaving out the last segment. Pieces are cut one segment shorter.
        private const int BrokenSegments = 25;
        private const int PieceSegments = BrokenSegments - 1;

        // Line segments in a glyph's strokes, as Draw.DrawPath1 builds them. Its width markers,
        // one-point strokes with y -100, give none.
        private static int CountSegments(List<List<Vector2>> strokes)
        {
            var count = 0;
            foreach (var path in strokes)
            {
                count += Math.Max(path.Count - 1, 0);
            }
            return count;
        }

        private static bool IsWidthMarker(List<Vector2> path) => path.Count == 1 && path[0].y == -100f;

        // The largest distance of a point from the center in x and in y, over the strokes that are
        // not width markers, as Draw.DrawPath1 finds it to scale the glyph.
        private static Vector2 Extent(List<List<Vector2>> strokes)
        {
            float maxX = 0f, maxY = 0f;
            foreach (var path in strokes)
            {
                if (IsWidthMarker(path))
                {
                    continue;
                }
                foreach (var p in path)
                {
                    maxX = Mathf.Max(maxX, Mathf.Abs(p.x));
                    maxY = Mathf.Max(maxY, Mathf.Abs(p.y));
                }
            }
            return new Vector2(maxX, maxY);
        }

        // Strokes for Draw.DrawPath1 that draw the glyph's line segments from startIndex, at most
        // PieceSegments of them, scaled and placed as the whole glyph would be: its width markers,
        // the parts of its strokes holding those segments, and a one-point stroke at its extent,
        // which draws nothing but gives DrawPath1 the whole glyph's size.
        private static List<List<Vector2>> PieceStrokes(List<List<Vector2>> strokes, Vector2 extent, int startIndex)
        {
            var piece = new List<List<Vector2>>();
            var index = 0;
            foreach (var path in strokes)
            {
                if (IsWidthMarker(path))
                {
                    piece.Add(path);
                    continue;
                }
                var segments = Math.Max(path.Count - 1, 0);
                var from = Math.Max(startIndex - index, 0);
                var to = Math.Min(startIndex + PieceSegments - index, segments);
                if (from < to)
                {
                    piece.Add(path.GetRange(from, to - from + 1));
                }
                index += segments;
            }
            piece.Add(new List<Vector2> { extent });
            return piece;
        }

        // Shows a drawn sprite in a slot the game just filled, or puts back the game's material
        // in a slot that showed one before.
        internal static void ApplyToSlot(AlienCharacter slot, string word)
        {
            var sr = slot?.sp;
            if (sr == null || spriteMaterial == null)
            {
                return;
            }
            if (word != null && stockSprites.TryGetValue(word, out var stock))
            {
                sr.sprite = stock;
                sr.sharedMaterial = spriteMaterial;
            }
            else if (word != null && alphaMaterial != null && sprites.TryGetValue(word, out var sprite))
            {
                sr.sprite = sprite;
                sr.sharedMaterial = alphaMaterial;
            }
            else if (alphaMaterial != null && sr.sharedMaterial == alphaMaterial)
            {
                sr.sharedMaterial = spriteMaterial;
            }
        }

        [HarmonyPatch(typeof(Draw), nameof(Draw.DrawACToSlot))]
        private static class SlotPatch
        {
            private static void Postfix(Draw __instance, int x, int y, string s) =>
                ApplyToSlot(__instance.alien_slots[x, y], s);
        }
    }
}
