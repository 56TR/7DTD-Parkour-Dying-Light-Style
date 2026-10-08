using System;
using HarmonyLib;

namespace ParkourMovement
{
    public sealed class ModApi : IModApi
    {
        public const string Version = "1.0.0";
        public const string HarmonyId = "pz.parkourmovement";

        public void InitMod(Mod modInstance)
        {
            try
            {
                PkRuntime.ModPath = modInstance.Path;
                string cfg = PkRuntime.LoadConfig() + ", " + BodyAnimLibrary.Load(modInstance.Path) + ", " + PkSettings.Load();
                var harmony = new Harmony(HarmonyId);
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PARKOUR_NOPATCH")))
                    harmony.PatchAll();
                else
                {
                    // control runs: only the autotest helpers, the game itself stays vanilla
                    harmony.CreateClassProcessor(typeof(Patches.NewsScreenSkipPatch)).Patch();
                    harmony.CreateClassProcessor(typeof(Patches.SpawnWindowSkipPatch)).Patch();
                }
                int patched = 0;
                foreach (var _ in harmony.GetPatchedMethods()) patched++;
                Log.Out($"[Parkour] v{Version} active, patched {patched} methods, {cfg}, enabled={PkRuntime.Tuning.Enabled}");
                AutoTest.Init();
            }
            catch (Exception ex)
            {
                Log.Error($"[Parkour] Failed to initialize: {ex}");
            }
        }
    }
}
