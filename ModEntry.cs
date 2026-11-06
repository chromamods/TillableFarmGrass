using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.TerrainFeatures;
using xTile;
using xTile.Layers;
using xTile.ObjectModel;
using xTile.Tiles;

namespace TillableFarmGrass;

/// <summary>Makes the permanent grass on every farm map tillable, and blends tilled grass into dirt patches until the soil is gone.</summary>
internal sealed class ModEntry : Mod
{
    /// <summary>Tile property holding the tile index a changed tile had on the original map.</summary>
    private const string GrassKey = "ChromaMods.TillableFarmGrass/Grass";

    /// <summary>The plain tillable dirt on the outdoors tile sheet.</summary>
    private const int DirtTile = 587;

    /// <summary>Half-grass edge tiles on the outdoors tile sheet, which the game doesn't type as grass.</summary>
    private static readonly HashSet<int> GrassEdges = new()
    {
        177, 178, 181, 182, 201, 202, 204, 225, 228, 229, 230, 251, 252, 423, 449, 567,
        568, 569, 570, 592, 593, 594, 595, 619, 631, 632, 635, 655, 656, 679, 683, 684, 705, 706
    };

    /// <summary>Plain light grass on the outdoors tile sheet, the only grass the blend pieces match.</summary>
    private static readonly HashSet<int> LightGrass = new() { 150, 151, 152, 175, 275 };

    /// <summary>Grass pieces on the outdoors tile sheet, indexed by which corners show dirt (NW 1, NE 2, SW 4, SE 8).</summary>
    /// <remarks>No piece has dirt in two opposite corners only, so those become plain dirt.</remarks>
    private static readonly int[] Pieces = { -1, 253, 250, 251, 203, 228, DirtTile, 178, 200, DirtTile, 225, 252, 201, 177, 202, DirtTile };

    public override void Entry(IModHelper helper)
    {
        helper.Events.Content.AssetRequested += OnAssetRequested;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.World.TerrainFeatureListChanged += OnTerrainFeatureListChanged;
    }

    private static void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.StartsWith("Maps/") || !FarmMapNames().Any(name => e.NameWithoutLocale.IsEquivalentTo("Maps/" + name)))
            return;

        e.Edit(asset =>
        {
            Map map = asset.AsMap().Data;
            MakeGrassDiggable(map);

            // Restores tilled grass when the map is reloaded mid-session.
            if (Game1.getFarm() is Farm farm)
                SyncDirt(farm, map);
        }, AssetEditPriority.Late);
    }

    private static void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        // Covers soil tilled before the save loaded, and soil that dried up overnight.
        Farm farm = Game1.getFarm();
        SyncDirt(farm, farm.Map);
    }

    private static void OnTerrainFeatureListChanged(object? sender, TerrainFeatureListChangedEventArgs e)
    {
        if (e.Location is not Farm farm)
            return;

        Layer back = farm.Map.GetLayer("Back");
        foreach (var pair in e.Added.Concat(e.Removed).Where(p => p.Value is HoeDirt))
        {
            for (int x = (int)pair.Key.X - 1; x <= pair.Key.X + 1; x++)
            {
                for (int y = (int)pair.Key.Y - 1; y <= pair.Key.Y + 1; y++)
                    Refresh(farm, back, x, y);
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

    private static void SyncDirt(Farm farm, Map map)
    {
        Layer back = map.GetLayer("Back");
        for (int x = 0; x < back.LayerWidth; x++)
        {
            for (int y = 0; y < back.LayerHeight; y++)
                Refresh(farm, back, x, y);
        }
    }

    /// <summary>Shows a grass tile as dirt if it's tilled, as a blend piece if it borders tilled grass, or as the original map otherwise.</summary>
    private static void Refresh(Farm farm, Layer back, int x, int y)
    {
        if (GrassTile(back, x, y) is not StaticTile tile)
            return;

        int original = Original(tile);
        int shown = IsTilled(farm, x, y) ? DirtTile : Blend(farm, back, x, y, original);
        if (shown == original)
            tile.Properties.Remove(GrassKey);
        else
            tile.Properties[GrassKey] = original;
        tile.TileIndex = shown;
    }

    private static int Blend(Farm farm, Layer back, int x, int y, int original)
    {
        int corners = Array.IndexOf(Pieces, original);
        if (corners < 0)
        {
            if (!LightGrass.Contains(original))
                return original;
            corners = 0;
        }

        for (int corner = 0; corner < 4; corner++)
        {
            int dx = corner % 2 == 0 ? -1 : 1;
            int dy = corner < 2 ? -1 : 1;
            if (IsTilledGrass(farm, back, x + dx, y) || IsTilledGrass(farm, back, x, y + dy) || IsTilledGrass(farm, back, x + dx, y + dy))
                corners |= 1 << corner;
        }
        return corners == 0 ? original : Pieces[corners];
    }

    private static bool IsTilledGrass(Farm farm, Layer back, int x, int y)
    {
        return GrassTile(back, x, y) != null && IsTilled(farm, x, y);
    }

    private static bool IsTilled(Farm farm, int x, int y)
    {
        return farm.terrainFeatures.TryGetValue(new Vector2(x, y), out var feature) && feature is HoeDirt;
    }

    /// <summary>Gets a Back tile that was grass on the original map, if any.</summary>
    private static StaticTile? GrassTile(Layer back, int x, int y)
    {
        if (x < 0 || y < 0 || x >= back.LayerWidth || y >= back.LayerHeight)
            return null;
        return back.Tiles[x, y] is StaticTile tile && IsOutdoorsSheet(tile.TileSheet) && IsGrass(tile.TileSheet, Original(tile)) ? tile : null;
    }

    private static int Original(StaticTile tile)
    {
        // The map is never saved, so the tile itself is the simplest place to remember what it was.
        return tile.Properties.TryGetValue(GrassKey, out PropertyValue? original) ? original : tile.TileIndex;
    }

    private static bool IsOutdoorsSheet(TileSheet sheet)
    {
        // The season prefix changes as the game swaps seasonal tile sheets.
        return sheet.ImageSource.EndsWith("_outdoorsTileSheet");
    }

    private static bool IsGrass(TileSheet sheet, int index)
    {
        return (IsOutdoorsSheet(sheet) && GrassEdges.Contains(index))
            || (sheet.TileIndexProperties[index].TryGetValue("Type", out PropertyValue? type) && type.ToString() == "Grass");
    }
}
