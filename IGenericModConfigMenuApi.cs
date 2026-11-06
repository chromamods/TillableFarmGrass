using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;

namespace TillableFarmGrass;

/// <summary>The subset of Generic Mod Config Menu's API this mod uses (SMAPI maps it by signature).</summary>
public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);

    void AddComplexOption(IManifest mod, Func<string> name, Action<SpriteBatch, Vector2> draw, Func<string>? tooltip = null, Action? beforeMenuOpened = null, Action? beforeSave = null, Action? afterSave = null, Action? beforeReset = null, Action? afterReset = null, Action? beforeMenuClosed = null, Func<int>? height = null, string? fieldId = null);
}
