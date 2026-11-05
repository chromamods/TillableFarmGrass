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

/// <summary>Makes the permanent grass on every farm map tillable, and turns tilled grass into plain dirt.</summary>
internal sealed class ModEntry : Mod
{
    private const string DirtKey = "ChromaMods.TillableFarmGrass/Dirt";

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
            {
                foreach (Vector2 tile in SavedDirt(farm))
                    ToDirt(map, tile);
            }
        }, AssetEditPriority.Late);
    }

    private static void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        Farm farm = Game1.getFarm();
        foreach (Vector2 tile in SavedDirt(farm))
            ToDirt(farm.Map, tile);
        foreach (var pair in farm.terrainFeatures.Pairs.Where(p => p.Value is HoeDirt))
            Till(farm, pair.Key);
    }

    private static void OnTerrainFeatureListChanged(object? sender, TerrainFeatureListChangedEventArgs e)
    {
        if (e.Location is not Farm farm)
            return;
        foreach (var pair in e.Added.Where(p => p.Value is HoeDirt))
            Till(farm, pair.Key);
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

    /// <summary>Turns a newly tilled grass tile into dirt, and remembers it so it stays dirt.</summary>
    private static void Till(Farm farm, Vector2 tile)
    {
        // The host's record is the one that's saved and shared; other players convert their own copy.
        if (ToDirt(farm.Map, tile) && Context.IsMainPlayer)
            farm.modData[DirtKey] = (farm.modData.TryGetValue(DirtKey, out string? saved) ? saved + " " : "") + $"{(int)tile.X},{(int)tile.Y}";
    }

    private static bool ToDirt(Map map, Vector2 tile)
    {
        Layer back = map.GetLayer("Back");
        if (tile.X < 0 || tile.Y < 0 || tile.X >= back.LayerWidth || tile.Y >= back.LayerHeight)
            return false;
        if (back.Tiles[(int)tile.X, (int)tile.Y] is not StaticTile grass || !IsOutdoorsSheet(grass.TileSheet) || !IsGrass(grass.TileSheet, grass.TileIndex))
            return false;

        grass.TileIndex = DirtTile;
        return true;
    }

    private static IEnumerable<Vector2> SavedDirt(Farm farm)
    {
        if (!farm.modData.TryGetValue(DirtKey, out string? saved))
            yield break;
        foreach (string entry in saved.Split(' '))
        {
            string[] xy = entry.Split(',');
            yield return new Vector2(int.Parse(xy[0]), int.Parse(xy[1]));
        }
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
