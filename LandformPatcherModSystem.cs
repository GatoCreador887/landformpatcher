using System;
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
    private static ModConfig config;
    private static WorldData worldData;

    private const int saveDataVersion = 0;

    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide == EnumAppSide.Server;
    }

    public override void Start(ICoreAPI api)
    {
        var configFile = $"{Mod.Info.ModID}.json";

        try
        {
            config = api.LoadModConfig<ModConfig>(configFile);
        }
        catch
        {
            config = null;
        }

        config ??= new();
        api.StoreModConfig(config, configFile);
        harmony = new Harmony(Mod.Info.ModID);
        harmony.PatchAll();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        api.Event.SaveGameCreated += () =>
        {
            worldData = new(saveDataVersion, config.DefaultSeaLevel, Enum.TryParse<HeightPatchingMode>(config.HeightPatchMode, true, out var parsedMode) ? parsedMode : HeightPatchingMode.Scale);
            api.WorldManager.SaveGame.StoreData($"{Mod.Info.ModID}", SerializerUtil.Serialize(worldData));
            api.Logger.Event($"Saved {Mod.Info.ModID} data for new save game");
        };
        api.Event.SaveGameLoaded += () =>
        {
            var data = api.WorldManager.SaveGame.GetData($"{Mod.Info.ModID}");

            if (data != null)
            {
                worldData = SerializerUtil.Deserialize<WorldData>(data);
                api.Logger.Event($"Loaded {Mod.Info.ModID} data of version {worldData.dataVersion}");
            }
            else
            {
                worldData = null;
                api.Logger.Notification($"Loaded save game does not have {Mod.Info.ModID} data");
            }
        };
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(Mod.Info.ModID);
        worldData = null;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LandformVariant), nameof(LandformVariant.Init))]
    public static bool PatchLandforms(LandformVariant __instance, IWorldManagerAPI api, int index)
    {
        switch (worldData.heightPatchingMode)
        {
            case HeightPatchingMode.Scale:
            {
                var seaLevel = TerraGenConfig.seaLevel / (float)api.MapSizeY;

                for (int i = 0; i < __instance.TerrainYKeyPositions.Length; ++i)
                {
                    if (__instance.TerrainYKeyPositions[i] > worldData.defaultSeaLevel)
                    {
                        __instance.TerrainYKeyPositions[i] = GameMath.Lerp(seaLevel, 1, (float)((__instance.TerrainYKeyPositions[i] - worldData.defaultSeaLevel) / (1 - worldData.defaultSeaLevel)));
                    }
                    else
                    {
                        __instance.TerrainYKeyPositions[i] = seaLevel * (float)(__instance.TerrainYKeyPositions[i] / worldData.defaultSeaLevel);
                    }
                }

                break;
            }
            case HeightPatchingMode.Offset:
            {
                var seaLevelDifference = (TerraGenConfig.seaLevel - (int)(worldData.defaultSeaLevel * api.MapSizeY)) / (float)api.MapSizeY;

                for (int i = 0; i < __instance.TerrainYKeyPositions.Length; ++i)
                {
                    __instance.TerrainYKeyPositions[i] += seaLevelDifference;
                }

                break;
            }
        }

        return true;
    }

    public class ModConfig
    {
        public double DefaultSeaLevel { get; set; } = 22 / 51d;
        public string HeightPatchMode { get; set; } = HeightPatchingMode.Scale.ToString().ToLowerInvariant();
    }

    [ProtoContract]
    public class WorldData
    {
        public WorldData() { }

        public WorldData(int version, double landformSeaLevel, HeightPatchingMode heightMode)
        {
            dataVersion = version;
            defaultSeaLevel = landformSeaLevel;
            heightPatchingMode = heightMode;
        }

        [ProtoMember(1)]
        public int dataVersion;
        [ProtoMember(2)]
        public double defaultSeaLevel;
        [ProtoMember(3)]
        public HeightPatchingMode heightPatchingMode;
    }

    public enum HeightPatchingMode
    {
        None = -1, Scale = 0, Offset = 1
    }
}
