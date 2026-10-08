using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ParkourMovement.Core;

namespace ParkourMovement
{
    /// <summary>
    /// Player settings (Options > General > Parkour): every move on/off, the Parkour perk integration and the vanilla
    /// perk tweaks. Saved per player in the user data folder, not in the mod folder (updates keep them).
    /// </summary>
    public static class PkSettings
    {
        public sealed class Def
        {
            public string Key;
            public bool Default;
            public PkFeature Feature;   // the move it switches (None for the general switches)
            public PkBonus Bonus;
        }

        // order = order in the options tab
        public static readonly Def[] Defs =
        {
            new Def { Key = "Enabled", Default = true },
            new Def { Key = "PerkIntegration", Default = true },
            new Def { Key = "ReplaceVanillaJump", Default = true },
            new Def { Key = "NoFallImmunity", Default = false },
            new Def { Key = "Mantle", Default = true, Feature = PkFeature.Mantle },
            new Def { Key = "Slide", Default = true, Feature = PkFeature.Slide },
            new Def { Key = "LowSlide", Default = true, Feature = PkFeature.LowSlide },
            new Def { Key = "LandingRoll", Default = true, Feature = PkFeature.LandingRoll },
            new Def { Key = "Vault", Default = true, Feature = PkFeature.Vault },
            new Def { Key = "Hang", Default = true, Feature = PkFeature.Hang },
            new Def { Key = "SlideJump", Default = true, Feature = PkFeature.SlideJump },
            new Def { Key = "WallClimb", Default = true, Feature = PkFeature.WallClimb },
            new Def { Key = "WallRun", Default = true, Feature = PkFeature.WallRun },
            new Def { Key = "WallJump", Default = true, Feature = PkFeature.WallJump },
            new Def { Key = "Tackle", Default = true, Feature = PkFeature.Tackle },
            new Def { Key = "Dropkick", Default = true, Feature = PkFeature.Dropkick },
            new Def { Key = "Stomp", Default = true, Feature = PkFeature.Stomp },
            new Def { Key = "VaultEnemy", Default = true, Feature = PkFeature.VaultEnemy },
            new Def { Key = "Efficient", Default = true, Bonus = PkBonus.Efficient },
            new Def { Key = "HangFree", Default = true, Bonus = PkBonus.HangFree },
            new Def { Key = "AutoRoll", Default = true, Bonus = PkBonus.AutoRoll },
            new Def { Key = "FpBody", Default = true },
            new Def { Key = "CameraEffects", Default = true },
            new Def { Key = "BodyAnims", Default = true },
        };

        static readonly Dictionary<string, bool> _values = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Def> _defs = new Dictionary<string, Def>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Bumped on every change (descriptions, cached masks).</summary>
        public static int Version { get; private set; }

        /// <summary>Self test: values set here are not written to disk.</summary>
        public static bool Transient;

        static PkSettings()
        {
            foreach (Def d in Defs)
            {
                _defs[d.Key] = d;
                _values[d.Key] = d.Default;
            }
        }

        public static bool IsKnown(string key) => key != null && _defs.ContainsKey(key);
        public static bool DefaultOf(string key) => _defs.TryGetValue(key, out Def d) && d.Default;

        public static bool Get(string key) => _values.TryGetValue(key, out bool v) ? v : DefaultOf(key);

        public static void Set(string key, bool value, bool save = true)
        {
            if (!IsKnown(key) || Get(key) == value) return;
            _values[key] = value;
            Version++;
            if (save && !Transient) Save();
        }

        public static void ResetAll()
        {
            foreach (Def d in Defs) _values[d.Key] = d.Default;
            Version++;
        }

        // shortcuts
        public static bool Enabled => Get("Enabled");
        public static bool PerkIntegration => Get("PerkIntegration");
        public static bool ReplaceVanillaJump => Get("ReplaceVanillaJump");
        public static bool NoFallImmunity => Get("NoFallImmunity");
        public static bool FpBody => Get("FpBody");
        public static bool CameraEffects => Get("CameraEffects");
        public static bool BodyAnims => Get("BodyAnims");

        // ---- Parkour perk tiers (Agility). Tier 0 = no perk.

        /// <summary>The tier that unlocks a move (perk integration on).</summary>
        public static int TierOf(PkFeature f)
        {
            switch (f)
            {
                case PkFeature.Mantle: return 0;
                case PkFeature.Slide:
                case PkFeature.LowSlide:
                case PkFeature.LandingRoll: return 1;
                case PkFeature.Vault:
                case PkFeature.Hang:
                case PkFeature.SlideJump: return 2;
                case PkFeature.WallClimb:
                case PkFeature.WallRun:
                case PkFeature.WallJump: return 3;
                case PkFeature.Tackle:
                case PkFeature.Dropkick:
                case PkFeature.Stomp:
                case PkFeature.VaultEnemy: return 4;
                default: return 0;
            }
        }

        /// <summary>Efficient / HangFree: Parkour tier 5. AutoRoll: Agility Mastery 4 (Specter's Grace).</summary>
        public static int TierOf(PkBonus x) => x == PkBonus.AutoRoll ? 4 : 5;

        /// <summary>What the motor may do. With the integration off every enabled move is there from the start.</summary>
        public static void Compute(int parkourTier, int masteryTier, out PkFeature blocked, out PkBonus bonus)
        {
            blocked = PkFeature.None;
            bonus = PkBonus.None;
            bool perks = PerkIntegration;
            foreach (Def d in Defs)
            {
                bool on = Get(d.Key);
                if (d.Feature != PkFeature.None)
                {
                    if (!on || (perks && parkourTier < TierOf(d.Feature))) blocked |= d.Feature;
                }
                else if (d.Bonus != PkBonus.None)
                {
                    int have = d.Bonus == PkBonus.AutoRoll ? masteryTier : parkourTier;
                    if (on && (!perks || have >= TierOf(d.Bonus))) bonus |= d.Bonus;
                }
            }
        }

        // ---- file

        public static string FilePath
        {
            get
            {
                string dir;
                try { dir = GameIO.GetUserGameDataDir(); }
                catch { dir = null; }
                if (string.IsNullOrEmpty(dir)) dir = PkRuntime.ModPath ?? ".";
                return Path.Combine(dir, "ParkourMovement_settings.txt");
            }
        }

        public static string Load()
        {
            try
            {
                string f = FilePath;
                if (!File.Exists(f)) return "settings: defaults (" + f + ")";
                foreach (string raw in File.ReadAllLines(f))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    if (IsKnown(k) && bool.TryParse(v, out bool b)) _values[k] = b;
                }
                Version++;
                return "settings loaded from " + f;
            }
            catch (Exception ex)
            {
                return "settings error: " + ex.Message;
            }
        }

        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Parkour Movement settings (Options > General > Parkour). true / false");
                foreach (Def d in Defs) sb.AppendLine(d.Key + "=" + (Get(d.Key) ? "true" : "false"));
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Warning("[Parkour] could not save settings: " + ex.Message);
            }
        }

        public static string Dump()
        {
            var sb = new StringBuilder();
            foreach (Def d in Defs) sb.Append(d.Key).Append('=').Append(Get(d.Key) ? "on" : "off").Append('\n');
            return sb.ToString();
        }
    }
}
