using System.Collections.Generic;
using System.Text;

namespace ParkourMovement
{
    /// <summary>Client console: pk | pk on|off | pk reload | pk set &lt;name&gt; &lt;value&gt; | pk verbose | pk trace | pk test [name]</summary>
    public class ConsoleCmdPk : ConsoleCmdAbstract
    {
        public override string[] getCommands() { return new[] { "pk", "parkour" }; }

        public override string getDescription() { return "Parkour Movement: status, tuning, self test"; }

        public override bool IsExecuteOnClient => true;

        public override string getHelp()
        {
            return "pk                    - motor state and last event\n"
                 + "pk on|off             - enable / disable parkour (vanilla movement when off)\n"
                 + "pk reload             - reload Config/parkour.xml\n"
                 + "pk set <name> <value> - change one tuning value live (pk dump lists names)\n"
                 + "pk dump               - all tuning values\n"
                 + "pk verbose            - toggle logging of every move transition\n"
                 + "pk trace [n]          - last n physics ticks (state, feet, velocity)\n"
                 + "pk test [name|all]    - build a test course in the sky and run scripted movement tests\n"
                 + "pk tp <section>       - playground: slide, wallrun, climb, vault, hang, coyote, lowslide, roll\n"
                 + "pk zombies [n]        - spawn n zombies 7 m in front of you (own game only)\n"
                 + "pk stamina            - toggle infinite stamina (testing; no god mode)\n"
                 + "pk input              - give the controls back if a self test left scripted input on\n"
                 + "pk opt [key on|off]   - list the Options > Parkour settings, or change one (saved)\n"
                 + "pk perk [t] [m]       - Parkour / Agility Mastery tiers in use; force them for testing (-1 = real)\n";
        }

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            string sub = _params.Count > 0 ? _params[0].ToLowerInvariant() : "";
            var m = PkRuntime.Motor;
            switch (sub)
            {
                case "":
                    Out($"enabled={PkRuntime.Tuning.Enabled} state={m.State} last={m.LastEvent} events={m.EventCount} ticks={m.Ticks} stamina={PkRuntime.Body.Stamina:0}");
                    break;
                case "on":
                case "off":
                    PkRuntime.Tuning.Enabled = sub == "on";
                    PkRuntime.Motor.Reset();
                    Out("parkour " + sub);
                    break;
                case "reload":
                    Out(PkRuntime.LoadConfig());
                    break;
                case "set":
                    if (_params.Count < 3) { Out("usage: pk set <name> <value>"); break; }
                    Out(PkRuntime.Tuning.Set(_params[1], _params[2]) ? $"{_params[1]} = {_params[2]}" : "unknown name or bad value");
                    break;
                case "dump":
                    Out(PkRuntime.Tuning.Dump().Replace(" ", "\n"));
                    break;
                case "verbose":
                    PkRuntime.Verbose = !PkRuntime.Verbose;
                    Out("verbose " + PkRuntime.Verbose);
                    break;
                case "trace":
                    int n = 20;
                    if (_params.Count > 1) int.TryParse(_params[1], out n);
                    var sb = new StringBuilder();
                    for (int k = n - 1; k >= 0; k--)
                    {
                        var r = PkRuntime.Trace[(PkRuntime.TraceHead - k + PkRuntime.Trace.Length * 4) % PkRuntime.Trace.Length];
                        sb.AppendLine($"{r.Time:0.00} {r.State,-9} g={(r.Grounded ? 1 : 0)} feet={r.Feet} v={r.Vel} |v|={r.Vel.magnitude:0.0}");
                    }
                    Out(sb.ToString());
                    break;
                case "stamina":
                    PkRuntime.InfiniteStamina = !PkRuntime.InfiniteStamina;
                    Out("infinite stamina " + (PkRuntime.InfiniteStamina ? "on" : "off") + " (no invulnerability, unlike 'buff god')");
                    break;
                case "opt":
                    if (_params.Count >= 3)
                    {
                        if (!PkSettings.IsKnown(_params[1])) { Out("unknown setting, pk opt lists them"); break; }
                        bool v = _params[2] == "on" || _params[2] == "true" || _params[2] == "1";
                        PkSettings.Set(_params[1], v);
                        Out($"{_params[1]} = {(v ? "on" : "off")}");
                    }
                    else Out(PkSettings.Dump() + "file: " + PkSettings.FilePath);
                    break;
                case "perk":
                    if (_params.Count >= 2 && int.TryParse(_params[1], out int pt)) PerkIntegration.ForceParkourTier = pt;
                    if (_params.Count >= 3 && int.TryParse(_params[2], out int mt)) PerkIntegration.ForceMasteryTier = mt;
                    Out($"parkour tier {PerkIntegration.ParkourTier} (forced {PerkIntegration.ForceParkourTier}), mastery tier {PerkIntegration.MasteryTier} (forced {PerkIntegration.ForceMasteryTier}) - takes effect within 0.5 s\n"
                        + $"blocked [{PerkIntegration.Blocked}]\nbonus [{PerkIntegration.Bonus}]\nreplaces jump {PerkIntegration.ReplacesJump}");
                    break;
                case "input":
                    ScriptedInput.Active = false;
                    ScriptedInput.Clear();
                    Out("controls are yours (scripted test input off)");
                    break;
                case "tp":
                    Out(AutoTest.TeleportTo(_params.Count > 1 ? _params[1].ToLowerInvariant() : "start"));
                    break;
                case "zombies":
                    int z = 3;
                    if (_params.Count > 1) int.TryParse(_params[1], out z);
                    Out(AutoTest.SpawnTargets(z < 1 ? 1 : z > 12 ? 12 : z));
                    break;
                case "test":
                    if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                    {
                        Out("pk test only works in a local / hosted game (it builds a course of blocks in the world)");
                        break;
                    }
                    AutoTest.StartSuite(_params.Count > 1 ? _params[1] : "all", quitWhenDone: false);
                    Out("test started, results in the log and in Parkour_results.txt next to the save");
                    break;
                default:
                    Out(getHelp());
                    break;
            }
        }

        static void Out(string s) { SingletonMonoBehaviour<SdtdConsole>.Instance.Output(s); }
    }
}
