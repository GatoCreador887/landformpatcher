using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace LandformPatcher;

[HarmonyPatch]
public class LandformPatcherModSystem : ModSystem
{
    private Harmony harmony;

    public static ILogger Logger;

    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide == EnumAppSide.Server;
    }

    public override void Start(ICoreAPI api)
    {
        Logger = Mod.Logger;
        harmony = new Harmony(Mod.Info.ModID);
        harmony.PatchAll();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        api.Event.SaveGameLoaded += () =>
        {
            var data = api.WorldManager.SaveGame.GetData(Mod.Info.ModID);

            if (data != null)
            {
                var worldData = SerializerUtil.Deserialize<WorldData>(data);
                api.Logger.Event($"Loaded {Mod.Info.ModID} data of version {worldData.dataVersion}, converting to world config");
                api.World.Config.SetDouble("landformpatcherDefaultSeaLevel", worldData.defaultSeaLevel);
                var modeStr = worldData.heightPatchingMode.ToString().ToLowerInvariant();
                api.World.Config.SetString("landformpatcherHeightPatchingModeSurface", modeStr);
                api.World.Config.SetString("landformpatcherHeightPatchingModeSubmerged", modeStr);
                ((SaveGame)api.WorldManager.SaveGame).ModData.Remove(Mod.Info.ModID);
            }
        };
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(Mod.Info.ModID);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LandformVariant), nameof(LandformVariant.Init))]
    public static bool PatchLandforms(LandformVariant __instance, IWorldManagerAPI api, int index)
    {
        Logger.Debug($"Patching landform {__instance.Code}");
        Logger.Debug($"Original TerrainYKeyPositions: [{string.Join(", ", __instance.TerrainYKeyPositions)}]");

        // Prevent mutations without predefined TerrainYKeyPositions from being double-patched
        foreach (var mutation in __instance.Mutations)
        {
            mutation.TerrainYKeyPositions ??= __instance.TerrainYKeyPositions;
        }

        var worldConfig = api.SaveGame.WorldConfiguration;
        var defaultSeaLevel = worldConfig.GetDouble("landformpatcherDefaultSeaLevel", 22 / 51d);
        var heightPatchingModeSurface = Enum.TryParse<HeightPatchingMode>(worldConfig.GetAsString("landformpatcherHeightPatchingModeSurface", "none"), true, out var parsed) ? parsed : HeightPatchingMode.None;
        var heightPatchingModeSubmerged = Enum.TryParse(worldConfig.GetAsString("landformpatcherHeightPatchingModeSubmerged", "none"), true, out parsed) ? parsed : HeightPatchingMode.None;
        var seaLevel = TerraGenConfig.seaLevel / (float)api.MapSizeY;
        // Use y levels so landforms are adjusted according to the actual difference in blocks
        var seaLevelDifference = (TerraGenConfig.seaLevel - (int)(defaultSeaLevel * api.MapSizeY)) / (float)api.MapSizeY;
        var maxY = float.MinValue;
        var minY = float.MaxValue;

        // No need to get these values if stretch isn't being used
        if (heightPatchingModeSurface == HeightPatchingMode.Stretch || heightPatchingModeSubmerged == HeightPatchingMode.Stretch)
        {
            foreach (var yPos in __instance.TerrainYKeyPositions)
            {
                if (yPos > maxY)
                {
                    maxY = yPos;
                }

                if (yPos < minY)
                {
                    minY = yPos;
                }
            }
        }

        for (int i = 0; i < __instance.TerrainYKeyPositions.Length; ++i)
        {
            if (__instance.TerrainYKeyPositions[i] > defaultSeaLevel)
            {
                switch (heightPatchingModeSurface)
                {
                    case HeightPatchingMode.Scale:
                        __instance.TerrainYKeyPositions[i] = GameMath.Lerp(seaLevel, 1, (float)((__instance.TerrainYKeyPositions[i] - defaultSeaLevel) / (1 - defaultSeaLevel)));
                        break;
                    case HeightPatchingMode.Offset:
                        __instance.TerrainYKeyPositions[i] += seaLevelDifference;
                        break;
                    case HeightPatchingMode.Stretch:
                        __instance.TerrainYKeyPositions[i] = GameMath.Lerp(seaLevel, maxY, (float)((__instance.TerrainYKeyPositions[i] - defaultSeaLevel) / (maxY - defaultSeaLevel)));
                        break;
                }
            }
            else
            {
                switch (heightPatchingModeSubmerged)
                {
                    case HeightPatchingMode.Scale:
                        __instance.TerrainYKeyPositions[i] = seaLevel * (float)(__instance.TerrainYKeyPositions[i] / defaultSeaLevel);
                        break;
                    case HeightPatchingMode.Offset:
                        __instance.TerrainYKeyPositions[i] += seaLevelDifference;
                        break;
                    case HeightPatchingMode.Stretch:
                        __instance.TerrainYKeyPositions[i] = GameMath.Lerp(seaLevel, minY, (float)((defaultSeaLevel - __instance.TerrainYKeyPositions[i]) / (defaultSeaLevel - minY)));
                        break;
                }
            }
        }

        Logger.Debug(message: $"Patched TerrainYKeyPositions: [{string.Join(", ", __instance.TerrainYKeyPositions)}]");
        return true;
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(GenTerra), "generate")]
    public static IEnumerable<CodeInstruction> PatchOceanDepth(IEnumerable<CodeInstruction> instructions)
    {
        var patched = false;

        foreach (var instr in instructions)
        {
            if (instr.opcode == OpCodes.Stfld && ((FieldInfo)instr.operand).Name == "oceanicityFac")
            {
                patched = true;
                yield return new(OpCodes.Ldarg_0);
                yield return new(OpCodes.Ldfld, AccessTools.Field(typeof(GenTerra), "api"));
                yield return new(OpCodes.Call, AccessTools.Method(typeof(LandformPatcherModSystem), nameof(ModifyOceanityFac)));
            }

            yield return instr;
        }

        if (!patched)
        {
            Logger.Warning("Failed to patch ocean depth, oceans may generate too deep or shallow");
        }
    }

    public static float ModifyOceanityFac(float original, ICoreServerAPI api)
    {
        // The ocean map is from 0-255 and it is scaled by this to get the actual y level change
        return api.World.Config.GetAsBool("landformpatcherPatchOceanDepth", false) ? api.World.SeaLevel / 110f * 0.33333f : original;
    }

    // Included for compatibility with v1.0.0, which didn't use the world config system
    [ProtoContract]
    public class WorldData
    {
        [ProtoMember(1)]
        public int dataVersion;
        [ProtoMember(2)]
        public double defaultSeaLevel;
        [ProtoMember(3)]
        public HeightPatchingMode heightPatchingMode;
    }

    public enum HeightPatchingMode
    {
        None = -1, Scale = 0, Offset = 1, Stretch = 2
    }
}
