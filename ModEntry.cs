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

/// <summary>Makes the permanent grass on every farm map tillable, and shows tilled grass as plain dirt until the soil is gone.</summary>
internal sealed class ModEntry : Mod
{
    /// <summary>Tile property holding the grass a tilled tile showed before it became dirt.</summary>
    private const string GrassKey = "ChromaMods.TillableFarmGrass/Grass";

    /// <summary>The plain tillable dirt on the outdoors tile sheet.</summary>
    private const int DirtTile = 587;

    /// <summary>Half-grass edge tiles on the outdoors tile sheet, which the game doesn't type as grass.</summary>
    private static readonly HashSet<int> GrassEdges = new()
    {
        177, 178, 181, 182, 202, 204, 225, 228, 229, 230, 251, 252, 423, 449, 567, 568,
        569, 570, 592, 593, 594, 595, 619, 631, 632, 635, 655, 656, 679, 683, 684, 705, 706
    };

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
        foreach (var pair in e.Removed.Where(p => p.Value is HoeDirt))
            Untill(farm.Map, pair.Key);
        foreach (var pair in e.Added.Where(p => p.Value is HoeDirt))
            Till(farm.Map, pair.Key);
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

    /// <summary>Shows dirt under every hoed tile of the farm, and grass again where the soil is gone.</summary>
    private static void SyncDirt(Farm farm, Map map)
    {
        Layer back = map.GetLayer("Back");
        for (int x = 0; x < back.LayerWidth; x++)
        {
            for (int y = 0; y < back.LayerHeight; y++)
            {
                Vector2 tile = new(x, y);
                if (farm.terrainFeatures.TryGetValue(tile, out var feature) && feature is HoeDirt)
                    Till(map, tile);
                else
                    Untill(map, tile);
            }
        }
    }

    private static void Till(Map map, Vector2 tile)
    {
        if (BackTile(map, tile) is not StaticTile grass || !IsOutdoorsSheet(grass.TileSheet) || !IsGrass(grass.TileSheet, grass.TileIndex))
            return;

        // The map is never saved, so the tile itself is the simplest place to remember its grass.
        grass.Properties[GrassKey] = grass.TileIndex;
        grass.TileIndex = DirtTile;
    }

    private static void Untill(Map map, Vector2 tile)
    {
        if (BackTile(map, tile) is not StaticTile dirt || !dirt.Properties.TryGetValue(GrassKey, out PropertyValue? grass))
            return;

        dirt.TileIndex = grass;
        dirt.Properties.Remove(GrassKey);
    }

    private static Tile? BackTile(Map map, Vector2 tile)
    {
        Layer back = map.GetLayer("Back");
        if (tile.X < 0 || tile.Y < 0 || tile.X >= back.LayerWidth || tile.Y >= back.LayerHeight)
            return null;
        return back.Tiles[(int)tile.X, (int)tile.Y];
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
