using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using ParkourMovement.Core;
using UnityEngine;

namespace ParkourMovement
{
    /// <summary>
    /// Ties the moves to the vanilla Parkour perk (Agility): reads the tiers, unlocks the moves per tier, optionally
    /// removes the perk's vanilla jump bonus and the tier 5 fall immunity, and rewrites the perk descriptions so they
    /// list exactly what the current settings give. Everything is done from code, so it also works on servers
    /// that do not have the mod (their XML is not touched).
    /// </summary>
    public static class PerkIntegration
    {
        public const string ParkourPerk = "perkParkour";
        public const string MasteryPerk = "perkAgilityMastery";

        /// <summary>Tiers that unlock the moves (forced ones in a self test).</summary>
        public static int ParkourTier { get; private set; }
        public static int MasteryTier { get; private set; }
        /// <summary>The player's real Parkour tier: what the vanilla perk effects (jump, fall) come from.</summary>
        public static int RealParkourTier { get; private set; }
        public static PkFeature Blocked { get; private set; }
        public static PkBonus Bonus { get; private set; }

        /// <summary>Self test: forced tiers instead of the player's progression (-1 = read the player).</summary>
        public static int ForceParkourTier = -1, ForceMasteryTier = -1;

        static float _nextRead;
        static int _maskVersion = -1, _maskParkour = -1, _maskMastery = -1;

        /// <summary>Every physics tick from PkRuntime: tiers (a few times a second), move mask, vanilla perk tweaks.</summary>
        public static void Tick(EntityPlayerLocal p)
        {
            if (Time.time >= _nextRead)
            {
                _nextRead = Time.time + 0.5f;
                RealParkourTier = Level(p, ParkourPerk);
                ParkourTier = ForceParkourTier >= 0 ? ForceParkourTier : RealParkourTier;
                MasteryTier = ForceMasteryTier >= 0 ? ForceMasteryTier : Level(p, MasteryPerk);
                Descriptions.Refresh();
            }
            if (_maskVersion != PkSettings.Version || _maskParkour != ParkourTier || _maskMastery != MasteryTier)
            {
                _maskVersion = PkSettings.Version;
                _maskParkour = ParkourTier;
                _maskMastery = MasteryTier;
                PkSettings.Compute(ParkourTier, MasteryTier, out PkFeature blocked, out PkBonus bonus);
                Blocked = blocked;
                Bonus = bonus;
            }
        }

        static int Level(EntityPlayerLocal p, string perk)
        {
            try
            {
                ProgressionValue v = p.Progression?.GetProgressionValue(perk);
                return v == null ? 0 : v.CalculatedLevel(p);
            }
            catch
            {
                return 0;
            }
        }

        // ---- tier 5 fall immunity. Vanilla: $parkourBonus = .9 at tier 5, subtracted from the impact speed, so the
        // fall damage buff (buffs.xml buffPlayerFallingDamage: (impact - bonus - .189) * 300 health) never hits.
        // The game re-sets that cvar every second, so instead of fighting it the option applies the damage the fall
        // would do with 5 m of safe fall (.01414 per metre, one more than tier 4), right after the vanilla impact.

        public const float FallBonusNoImmunity = 0.0707f;
        static readonly string[] NoFallDamageBuffs = { "buffDrugOhShitzDrops", "buffTwitchDontBreakLeg", "buffDontBreakMyLeg", "god" };

        public static bool NoFallImmunityActive => PkSettings.Enabled && PkSettings.NoFallImmunity && RealParkourTier >= 5;

        /// <summary>Health the tier 5 player loses for a fall of this (vanilla, already modified) impact speed.</summary>
        public static int NoImmunityDamage(float fallSpeed) => (int)((fallSpeed - FallBonusNoImmunity - 0.189f) * 300f);

        /// <summary>From the FallImpact postfix: vanilla has written _fallSpeed; tier 5 took no damage, add it.</summary>
        public static void AfterFallImpact(EntityPlayerLocal p)
        {
            if (p?.Buffs == null) return;
            float fs = p.Buffs.GetCustomVar("_fallSpeed");
            if (PkRuntime.Verbose || AutoTest.Running)
                Log.Out($"[Parkour] fall impact {fs:0.000}, tier {RealParkourTier}, no-immunity {NoFallImmunityActive}, god {p.Buffs.HasBuff("god")}");
            if (!NoFallImmunityActive || p.IsDead()) return;
            if (fs < 0.08f) return;   // vanilla's own threshold for the fall damage buff
            foreach (string b in NoFallDamageBuffs)
                if (p.Buffs.HasBuff(b)) return;
            int dmg = NoImmunityDamage(fs);
            if (dmg < 1) return;
            LastNoImmunityDamage = dmg;
            p.Stats.Health.Value = Mathf.Max(0f, p.Stats.Health.Value - dmg);
            p.PlayOneShot(dmg >= 10 ? "player#painlg" : "player#painsm");
        }

        public static int LastNoImmunityDamage;

        // ---- vanilla perk jump bonus (tiers 2-3: +39 %, 4-5: +75 % JumpStrength)

        public static float VanillaJumpBonus(int tier) => tier >= 4 ? 0.75f : tier >= 2 ? 0.39f : 0f;

        public static bool ReplacesJump => PkSettings.Enabled && PkSettings.ReplaceVanillaJump && VanillaJumpBonus(RealParkourTier) > 0f;

        /// <summary>
        /// EntityPlayerLocal.OnUpdateLive sets MotorJumpForce from JumpStrength (base * (1 + all perc_add)) and starts
        /// the jump in the same call; the jump's take-off speed is read in OnStart_Jump. Recompute the force there
        /// without the perk's share.
        /// </summary>
        [HarmonyPatch(typeof(vp_FPController), nameof(vp_FPController.OnStart_Jump))]
        public static class JumpPatch
        {
            public static void Prefix(vp_FPController __instance)
            {
                try
                {
                    if (!ReplacesJump) return;
                    vp_FPController c = __instance;
                    EntityPlayerLocal p = c.localPlayer;
                    if (p == null) return;
                    float orig = c.originalMotorJumpForce;
                    if (orig <= 0f) return;
                    var tags = p.CurrentStanceTag | p.CurrentMovementTag;
                    float full = Mathf.Max(EffectManager.GetValue(PassiveEffects.JumpStrength, null, orig, p, null, tags), 0f);
                    float want = Mathf.Max(full - orig * VanillaJumpBonus(RealParkourTier), 0f)
                                 * global::SandboxOptions.SandboxOptionManager.GetFloat(global::SandboxOptions.SandboxOptions.JumpStrength);
                    c.MotorJumpForce = want;
                    if (want > 0f)
                        c.MotorJumpForceHold = want / Mathf.Lerp(90f, 180f, Mathf.Clamp01(1f - orig / want)) * Time.timeScale;
                }
                catch (Exception ex)
                {
                    Log.Warning("[Parkour] jump: " + ex.Message);
                }
            }
        }

        // ---- perk descriptions

        public static class Descriptions
        {
            static int _version = -1, _lang = -2;
            static Dictionary<string, string[]> _dict;
            static readonly Dictionary<string, string[]> _original = new Dictionary<string, string[]>();
            static readonly Dictionary<string, string[]> _written = new Dictionary<string, string[]>();

            static readonly string[] Keys =
            {
                "perkParkourDesc",
                "perkParkourRank1LongDesc", "perkParkourRank2LongDesc", "perkParkourRank3LongDesc",
                "perkParkourRank4LongDesc", "perkParkourRank5LongDesc", "perkAgilityMasteryRank4LongDesc",
            };

            /// <summary>
            /// Our own text in one language column of the loaded localization (Config/Localization.csv), English when
            /// that language has none. The perk texts are built per column, so each language gets its own lines.
            /// </summary>
            static string L(string key, int col)
            {
                Dictionary<string, string[]> dict = Localization.mDictionary;
                if (dict != null && dict.TryGetValue(key, out string[] row) && row != null)
                {
                    if (col >= 0 && col < row.Length && !string.IsNullOrEmpty(row[col])) return row[col];
                    int def = Localization.defaultLanguageIndex;
                    if (def >= 0 && def < row.Length && !string.IsNullOrEmpty(row[def])) return row[def];
                }
                return key;
            }

            static string Pct(float scale) => Math.Round((1f - scale) * 100f).ToString(System.Globalization.CultureInfo.InvariantCulture);

            /// <summary>Text of each move, as listed in the perk (localization key pkPerk + setting key).</summary>
            public static string LineOf(PkFeature f, Tuning t, int col)
            {
                if (f == PkFeature.None) return null;
                string text = L("pkPerk" + f, col);
                return f == PkFeature.LandingRoll ? text.Replace("{0}", Pct(t.RollDamageScale)) : text;
            }

            public static string LineOf(PkBonus x, Tuning t, int col)
            {
                if (x == PkBonus.None) return null;
                string text = L("pkPerk" + x, col);
                return x == PkBonus.Efficient ? text.Replace("{0}", Pct(t.EfficientStaminaScale)) : text;
            }

            /// <summary>Brand name, not translated; the colour marks the mod's lines in the perk window.</summary>
            public const string Header = "[E8B13A]Parkour Movement:[-]";

            /// <summary>The mod lines a tier adds with the current settings (empty when the integration is off).</summary>
            public static List<string> ModLines(int tier, int col, bool mastery = false)
            {
                var lines = new List<string>();
                if (!PkSettings.Enabled || !PkSettings.PerkIntegration) return lines;
                Tuning t = PkRuntime.Tuning;
                foreach (PkSettings.Def d in PkSettings.Defs)
                {
                    if (!PkSettings.Get(d.Key)) continue;
                    if (d.Feature != PkFeature.None && !mastery && PkSettings.TierOf(d.Feature) == tier) lines.Add(LineOf(d.Feature, t, col));
                    else if (d.Bonus != PkBonus.None)
                    {
                        bool isMastery = d.Bonus == PkBonus.AutoRoll;
                        if (isMastery != mastery || PkSettings.TierOf(d.Bonus) != tier) continue;
                        if (d.Bonus == PkBonus.AutoRoll && !PkSettings.Get("LandingRoll")) continue;
                        if (d.Bonus == PkBonus.HangFree && !PkSettings.Get("Hang")) continue;
                        lines.Add(LineOf(d.Bonus, t, col));
                    }
                }
                return lines;
            }

            /// <summary>Rebuild the texts when the settings or the loaded localization changed.</summary>
            public static void Refresh(bool force = false)
            {
                try
                {
                    Dictionary<string, string[]> dict = Localization.mDictionary;
                    if (dict == null) return;
                    int lang = Localization.currentLanguageIndex;
                    if (!force && _version == PkSettings.Version && _lang == lang && ReferenceEquals(_dict, dict) && Untouched(dict)) return;
                    _version = PkSettings.Version;
                    _lang = lang;
                    _dict = dict;
                    foreach (string key in Keys) Apply(dict, key, lang);
                }
                catch (Exception ex)
                {
                    Log.Warning("[Parkour] perk descriptions: " + ex.Message);
                }
            }

            /// <summary>False when something (a server's localization patch) replaced our rows.</summary>
            static bool Untouched(Dictionary<string, string[]> dict)
            {
                foreach (var kv in _written)
                    if (!dict.TryGetValue(kv.Key, out string[] row) || !ReferenceEquals(row, kv.Value)) return false;
                return true;
            }

            static void Apply(Dictionary<string, string[]> dict, string key, int lang)
            {
                if (!dict.TryGetValue(key, out string[] row) || row == null) return;
                // a row we did not write ourselves is the original (first time, or the game reloaded it)
                if (!_written.TryGetValue(key, out string[] mine) || !ReferenceEquals(mine, row)) _original[key] = (string[])row.Clone();
                string[] orig = _original[key];
                string[] next = (string[])orig.Clone();
                int def = Localization.defaultLanguageIndex;
                foreach (int col in new[] { lang, def })
                {
                    if (col < 0 || col >= next.Length) continue;
                    string src = orig[col];
                    if (string.IsNullOrEmpty(src)) src = def >= 0 && def < orig.Length ? orig[def] : null;
                    if (string.IsNullOrEmpty(src)) continue;
                    next[col] = Build(key, src, col);
                }
                dict[key] = next;
                if (Localization.mDictionaryCaseInsensitive != null) Localization.mDictionaryCaseInsensitive[key] = next;
                _written[key] = next;
            }

            /// <summary>Vanilla text with the replaced lines changed (by line index, so any language works) plus our lines.</summary>
            public static string Build(string key, string vanilla, int col)
            {
                bool literal = vanilla.Contains("\\n");
                string sep = literal ? "\\n" : "\n";
                var lines = new List<string>(vanilla.Split(new[] { sep }, StringSplitOptions.None));
                bool on = PkSettings.Enabled;
                var add = new List<string>();
                switch (key)
                {
                    case "perkParkourDesc":
                        if (on && PkSettings.PerkIntegration && PkSettings.Get("Mantle"))
                            add.Add(L("pkPerkWithoutPerk", col).Replace("{0}", LineOf(PkFeature.Mantle, PkRuntime.Tuning, col)));
                        break;
                    case "perkParkourRank1LongDesc": add.AddRange(ModLines(1, col)); break;
                    case "perkParkourRank2LongDesc":
                        // flavour, stamina, safe fall, jump 1 m higher
                        if (on && PkSettings.ReplaceVanillaJump && lines.Count == 4) lines.RemoveAt(3);
                        add.AddRange(ModLines(2, col));
                        break;
                    case "perkParkourRank3LongDesc": add.AddRange(ModLines(3, col)); break;
                    case "perkParkourRank4LongDesc":
                        // flavour, stamina, safe fall, jump 2 m higher, no leg injuries
                        if (on && PkSettings.ReplaceVanillaJump && lines.Count == 5) lines.RemoveAt(3);
                        add.AddRange(ModLines(4, col));
                        break;
                    case "perkParkourRank5LongDesc":
                        // flavour, stamina, never take fall damage
                        if (on && PkSettings.NoFallImmunity && lines.Count == 3) lines[2] = L("pkPerkSafeFall5", col);
                        add.AddRange(ModLines(5, col));
                        break;
                    case "perkAgilityMasteryRank4LongDesc": add.AddRange(ModLines(4, col, mastery: true)); break;
                }
                var sb = new StringBuilder(string.Join(sep, lines));
                if (add.Count > 0)
                {
                    if (key != "perkParkourDesc") sb.Append(sep).Append(Header);
                    foreach (string l in add) sb.Append(sep).Append(key == "perkParkourDesc" ? l : "- " + l);
                }
                return sb.ToString();
            }
        }
    }
}
