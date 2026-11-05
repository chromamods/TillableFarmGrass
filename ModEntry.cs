using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using xTile;
using xTile.ObjectModel;
using xTile.Tiles;

namespace TillableFarmGrass;

/// <summary>Makes the permanent grass tiles on every farm map diggable.</summary>
internal sealed class ModEntry : Mod
{
    public override void Entry(IModHelper helper)
    {
        helper.Events.Content.AssetRequested += OnAssetRequested;
    }

    private static void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.StartsWith("Maps/") && FarmMapNames().Any(name => e.NameWithoutLocale.IsEquivalentTo("Maps/" + name)))
            e.Edit(asset => MakeGrassDiggable(asset.AsMap().Data), AssetEditPriority.Late);
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
                IPropertyCollection properties = sheet.TileIndexProperties[index];
                if (properties.TryGetValue("Type", out PropertyValue? type) && type.ToString() == "Grass")
                    properties.TryAdd("Diggable", "T");
            }
        }
    }
}
