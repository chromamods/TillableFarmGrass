using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using xTile;
using xTile.Layers;
using xTile.ObjectModel;
using xTile.Tiles;

namespace TillableFarmGrass;

/// <summary>Makes the permanent grass on every farm map tillable, and draws tilled grass as dirt that blends into the grass around it.</summary>
internal sealed class ModEntry : Mod
{
    /// <summary>The plain tillable dirt on the outdoors tile sheet.</summary>
    private const int DirtTile = 587;

    /// <summary>The farm mod data key listing the grass tiles cleared to dirt with a pickaxe, as "x,y" pairs.</summary>
    private const string ClearedKey = "ChromaMods.TillableFarmGrass/Cleared";

    /// <summary>Grass and half-grass tiles on the outdoors tile sheet that some farm maps don't type as grass.</summary>
    /// <remarks>152 is the second frame of the animated flower grass 151, which only types its first frame.</remarks>
    private static readonly HashSet<int> GrassTiles = new()
    {
        152, 177, 178, 179, 180, 181, 182, 200, 201, 202, 203, 204, 205, 225, 228, 229, 230, 250, 251, 252, 253,
        254, 255, 256, 261, 276, 277, 278, 286, 300, 304, 311, 328, 329, 332, 353, 354, 355, 356, 357, 378,
        379, 380, 381, 382, 401, 403, 404, 405, 406, 407, 423, 449, 567, 568, 569, 570, 592, 593, 594, 595,
        619, 631, 632, 635, 655, 656, 679, 683, 684, 705, 706, 1092, 1093, 1094, 1095, 1119, 1120, 1121
    };

    /// <summary>Grass and dirt pieces on the outdoors tile sheet, indexed by which corners show dirt (NW 1, NE 2, SW 4, SE 8).</summary>
    /// <remarks>No piece has dirt in two opposite corners only, so those combine two corner pieces.</remarks>
    private static readonly int[][] Pieces =
    {
        Array.Empty<int>(), new[] { 253 }, new[] { 250 }, new[] { 251 }, new[] { 203 }, new[] { 228 }, new[] { 250, 203 }, new[] { 178 },
        new[] { 200 }, new[] { 253, 200 }, new[] { 225 }, new[] { 252 }, new[] { 201 }, new[] { 177 }, new[] { 202 }, new[] { DirtTile }
    };

    /// <summary>Dirt drawn over a ground tile, by tile sheet, index and corners, so it blends with any grass shade.</summary>
    private static readonly Dictionary<(string Sheet, int Index, int Corners), Texture2D> blends = new();

    /// <summary>The pixels of each outdoors tile sheet on the farm, by tile sheet ID.</summary>
    private static readonly Dictionary<string, (Color[] Pixels, int Columns)> sheets = new();
    private static TileSheet? mainSheet;
    private static Map? loadedMap;
    private static string? loadedSeason;
    private static HashSet<Color> grassColors = new();
    private static HashSet<Color> dirtColors = new();

    /// <summary>Whether each colour seen on a ground tile counts as grass.</summary>
    private static readonly Dictionary<Color, bool> grassLike = new();

    private static string? clearedData;
    private static HashSet<Point> cleared = new();

    public override void Entry(IModHelper helper)
    {
        helper.Events.Content.AssetRequested += OnAssetRequested;
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        var harmony = new Harmony(ModManifest.UniqueID);
        harmony.Patch(
            AccessTools.Method(typeof(Pickaxe), nameof(Pickaxe.DoFunction)),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforePickaxe)));
        harmony.Patch(
            AccessTools.Method(typeof(GameLocation), nameof(GameLocation.drawFloorDecorations)),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforeFloorDecorations)));
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        var gmcm = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (gmcm == null)
            return;
        gmcm.Register(ModManifest, reset: () => { }, save: () => { });
        gmcm.AddComplexOption(
            ModManifest,
            name: () => "Restore cleared grass",
            draw: RestoreButton(),
            tooltip: () => "Turns all the grass you cleared with a pickaxe back into grass. Needs a save loaded.",
            height: () => 56);
    }

    /// <summary>GMCM has no button option, so a complex option draws one and polls the mouse itself; it fires on release when the press began on it.</summary>
    private static Action<SpriteBatch, Vector2> RestoreButton()
    {
        bool wasDown = false, pressed = false;
        return (b, position) =>
        {
            const string label = "Restore";
            Vector2 size = Game1.smallFont.MeasureString(label);
            Rectangle bounds = new((int)position.X, (int)position.Y, (int)size.X + 40, 56);
            bool enabled = Context.IsWorldReady && Game1.getFarm().modData.ContainsKey(ClearedKey);
            bool hovered = enabled && bounds.Contains(Game1.getMouseX(), Game1.getMouseY());
            bool down = Game1.input.GetMouseState().LeftButton == ButtonState.Pressed;
            if (down && !wasDown)
                pressed = hovered;
            else if (!down && wasDown && pressed && hovered)
            {
                Game1.getFarm().modData.Remove(ClearedKey);
                Game1.playSound("dirtyHit");
            }
            wasDown = down;

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), bounds.X, bounds.Y, bounds.Width, bounds.Height,
                (hovered ? Color.Wheat : Color.White) * (enabled ? 1f : 0.5f), 4f, false);
            Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.X + 20, bounds.Y + (bounds.Height - size.Y) / 2),
                Game1.textColor * (enabled ? 1f : 0.5f));
        };
    }

    /// <summary>Clears farm grass to dirt when a pickaxe hits it with nothing on top, or turns cleared dirt back into grass.</summary>
    private static void BeforePickaxe(GameLocation location, int x, int y, Farmer who)
    {
        Point tile = new(x / 64, y / 64);
        if (location is not Farm farm || !IsGrassTile(farm.Map.GetLayer("Back"), tile.X, tile.Y)
            || farm.IsTileOccupiedBy(tile.ToVector2(), CollisionMask.All & ~(CollisionMask.Characters | CollisionMask.Farmers)))
            return;

        // The pickaxe also hits an object up to 8 pixels into the next tile, which shouldn't clear the grass under the swing.
        int nudgeX = who.FacingDirection % 2 == 0 ? 8 : 0, nudgeY = 8 - nudgeX;
        if (farm.Objects.ContainsKey(new Vector2((x - nudgeX) / 64, (y - nudgeY) / 64)) || farm.Objects.ContainsKey(new Vector2((x + nudgeX) / 64, (y + nudgeY) / 64)))
            return;

        var tiles = new HashSet<Point>(Cleared(farm));
        if (!tiles.Remove(tile))
            tiles.Add(tile);
        farm.modData[ClearedKey] = string.Join(" ", tiles.Select(point => $"{point.X},{point.Y}"));
        farm.playSound("hoeHit", tile.ToVector2());
    }

    private static HashSet<Point> Cleared(Farm farm)
    {
        farm.modData.TryGetValue(ClearedKey, out string? data);
        if (data != clearedData)
        {
            clearedData = data;
            cleared = (data ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split(','))
                .Select(pair => new Point(int.Parse(pair[0]), int.Parse(pair[1])))
                .ToHashSet();
        }
        return cleared;
    }

    private static void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.StartsWith("Maps/") && FarmMapNames().Any(name => e.NameWithoutLocale.IsEquivalentTo("Maps/" + name)))
            e.Edit(asset => MakeGrassDiggable(asset.AsMap().Data), AssetEditPriority.Late);
    }

    /// <summary>Draws the dirt over the ground just before floors, so paths and stepping stones sit on top and the ground between them blends too.</summary>
    private static void BeforeFloorDecorations(GameLocation __instance, SpriteBatch b)
    {
        if (__instance is not Farm farm || farm.Map.TileSheets.FirstOrDefault(IsMainSheet) is not TileSheet main)
            return;

        if (loadedMap != farm.Map || loadedSeason != main.ImageSource)
            LoadSheets(farm.Map, main);

        Cleared(farm);
        Layer back = farm.Map.GetLayer("Back");
        int left = Game1.viewport.X / 64, top = Game1.viewport.Y / 64;
        for (int x = left; x <= left + Game1.viewport.Width / 64 + 1; x++)
        {
            for (int y = top; y <= top + Game1.viewport.Height / 64 + 1; y++)
            {
                int corners = Corners(farm, back, x, y);
                if (corners == 0)
                    continue;
                // Depth 0 sorts it under everything else in this front-to-back batch.
                b.Draw(Blend(OutdoorsTile(back, x, y)!, corners), Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64, y * 64)), null, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0f);
            }
        }
    }

    private static IEnumerable<string> FarmMapNames()
    {
        // Type 7 is a modded farm, whose map comes from Data/AdditionalFarms.
        for (int type = 0; type < 7; type++)
            yield return Farm.getMapNameFromTypeInt(type);
        foreach (var farm in DataLoader.AdditionalFarms(Game1.content))
            yield return farm.MapName;
    }

    private static void MakeGrassDiggable(Map map)
    {
        // The game hoes any Back tile whose tile sheet index has a Diggable property, so marking the
        // grass indices covers every grass tile in the map.
        foreach (TileSheet sheet in map.TileSheets)
        {
            for (int index = 0; index < sheet.TileCount; index++)
            {
                if (IsGrass(sheet, index))
                    sheet.TileIndexProperties[index].TryAdd("Diggable", "T");
            }
        }
    }

    /// <summary>Gets which corners of a tile show dirt: all of them if it's dirt grass, or the ones touching dirt grass.</summary>
    /// <remarks>Any ground tile can get corners, so grass around stepping stones and cliff tops blends too.</remarks>
    private static int Corners(Farm farm, Layer back, int x, int y)
    {
        if (OutdoorsTile(back, x, y) == null)
            return 0;
        if (IsDirtGrass(farm, back, x, y))
            return 15;

        int corners = 0;
        for (int corner = 0; corner < 4; corner++)
        {
            int dx = corner % 2 == 0 ? -1 : 1;
            int dy = corner < 2 ? -1 : 1;
            if (IsDirtGrass(farm, back, x + dx, y) || IsDirtGrass(farm, back, x, y + dy) || IsDirtGrass(farm, back, x + dx, y + dy))
                corners |= 1 << corner;
        }
        return corners;
    }

    /// <summary>Whether a grass tile shows as dirt: tilled, or cleared with a pickaxe.</summary>
    private static bool IsDirtGrass(Farm farm, Layer back, int x, int y)
    {
        return IsGrassTile(back, x, y) && (IsTilled(farm, x, y) || cleared.Contains(new Point(x, y)));
    }

    private static bool IsTilled(Farm farm, int x, int y)
    {
        return farm.terrainFeatures.TryGetValue(new Vector2(x, y), out var feature) && feature is HoeDirt;
    }

    private static bool IsGrassTile(Layer back, int x, int y)
    {
        return OutdoorsTile(back, x, y) is Tile tile && IsGrass(tile.TileSheet, tile.TileIndex);
    }

    /// <summary>Gets a ground tile on an outdoors sheet; an animated one reports its current frame's index.</summary>
    private static Tile? OutdoorsTile(Layer back, int x, int y)
    {
        if (x < 0 || y < 0 || x >= back.LayerWidth || y >= back.LayerHeight)
            return null;
        return back.Tiles[x, y] is Tile tile and (StaticTile or AnimatedTile) && IsOutdoorsSheet(tile.TileSheet) ? tile : null;
    }

    private static void LoadSheets(Map map, TileSheet main)
    {
        foreach (Texture2D blend in blends.Values)
            blend.Dispose();
        blends.Clear();
        sheets.Clear();
        foreach (TileSheet sheet in map.TileSheets.Where(IsOutdoorsSheet))
        {
            Texture2D texture = Game1.content.Load<Texture2D>(sheet.ImageSource);
            Color[] pixels = new Color[texture.Width * texture.Height];
            texture.GetData(pixels);
            sheets[sheet.Id] = (pixels, texture.Width / 16);
        }
        mainSheet = main;
        loadedMap = map;
        loadedSeason = main.ImageSource;

        // Pixel art reuses exact palette colours, so the colours of the all-grass tiles tell grass from anything else
        // in every season and under recolours, where judging by hue fails.
        dirtColors = new HashSet<Color>(new[] { DirtTile, 176, 226, 227 }.SelectMany(index => TilePixels(main, index)));
        grassColors = new HashSet<Color>();
        grassLike.Clear();
        foreach (TileSheet sheet in map.TileSheets.Where(IsOutdoorsSheet))
        {
            for (int index = 0; index < sheet.TileCount; index++)
            {
                if (IsGrass(sheet, index) && !TilePixels(sheet, index).Any(dirtColors.Contains))
                    grassColors.UnionWith(TilePixels(sheet, index));
            }
        }
    }

    /// <summary>Builds the dirt drawn over a ground tile for the given dirt corners.</summary>
    private static Texture2D Blend(Tile tile, int corners)
    {
        var key = (tile.TileSheet.Id, tile.TileIndex, corners);
        if (blends.TryGetValue(key, out Texture2D? texture))
            return texture;

        // A tile that's already a grass piece adds its own dirt corners, so its old edge is painted over. Any other
        // tile only gets dirt over its grass pixels, so piece outlines never land on dirt, paths or stones.
        int own = tile.TileSheet == mainSheet ? Array.FindIndex(Pieces, pieces => pieces.Length == 1 && pieces[0] == tile.TileIndex) : -1;
        int[] shape = Pieces[corners | Math.Max(own, 0)];
        Color[] pixels = new Color[256];
        for (int pixel = 0; pixel < 256; pixel++)
        {
            if (own < 0 && corners != 15 && !IsGrassColor(Pixel(tile.TileSheet, tile.TileIndex, pixel)))
                continue;
            foreach (int piece in shape)
            {
                Color color = Pixel(mainSheet!, piece, pixel);
                if (piece == DirtTile || !grassColors.Contains(color))
                {
                    pixels[pixel] = color;
                    break;
                }
            }
        }

        texture = new Texture2D(Game1.graphics.GraphicsDevice, 16, 16);
        texture.SetData(pixels);
        blends[key] = texture;
        return texture;
    }

    /// <summary>Whether a ground pixel is grass: a grass colour, or closer to one than to any dirt colour.</summary>
    /// <remarks>Recolours don't always reuse the main sheet's greens on the second sheet, so near matches count too.</remarks>
    private static bool IsGrassColor(Color color)
    {
        if (!grassLike.TryGetValue(color, out bool grass))
        {
            int toGrass = grassColors.Min(other => Distance(color, other));
            grass = toGrass <= 40 * 40 && toGrass < dirtColors.Min(other => Distance(color, other));
            grassLike[color] = grass;
        }
        return grass;
    }

    private static int Distance(Color a, Color b)
    {
        return (a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B);
    }

    private static Color Pixel(TileSheet sheet, int index, int pixel)
    {
        var (pixels, columns) = sheets[sheet.Id];
        return pixels[((index / columns) * 16 + pixel / 16) * columns * 16 + (index % columns) * 16 + pixel % 16];
    }

    private static IEnumerable<Color> TilePixels(TileSheet sheet, int index)
    {
        return Enumerable.Range(0, 256).Select(pixel => Pixel(sheet, index, pixel));
    }

    private static bool IsMainSheet(TileSheet sheet)
    {
        // The season prefix changes as the game swaps seasonal tile sheets.
        return sheet.ImageSource.EndsWith("_outdoorsTileSheet");
    }

    /// <summary>Whether a tile sheet is the main outdoors sheet or the second one 1.6 added, which farm maps also use for grass.</summary>
    private static bool IsOutdoorsSheet(TileSheet sheet)
    {
        return IsMainSheet(sheet) || sheet.ImageSource.EndsWith("_outdoorsTileSheet2");
    }

    private static bool IsGrass(TileSheet sheet, int index)
    {
        return (IsMainSheet(sheet) && GrassTiles.Contains(index))
            || (sheet.TileIndexProperties[index].TryGetValue("Type", out PropertyValue? type) && type.ToString() == "Grass");
    }
}
