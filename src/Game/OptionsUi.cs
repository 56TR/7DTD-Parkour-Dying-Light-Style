using System;
using UnityEngine.Scripting;

namespace ParkourMovement
{
    /// <summary>
    /// One On/Off row of Options > General > Parkour. Works like the vanilla game-pref rows (Apply / Back /
    /// Defaults, the "*" for a pending change) but reads and writes PkSettings. XML:
    /// option_controller="ParkourMovement.OptionEntryParkour, ParkourMovement" prefname="&lt;setting key&gt;".
    /// </summary>
    [Preserve]
    public class XUiC_OptionEntryParkour : XUiC_OptionEntryAbs
    {
        bool _hooked;

        XUiC_ComboBoxBool Combo => comboGeneric as XUiC_ComboBoxBool;

        bool Stored => PkSettings.Get(PrefName);

        public override bool IsChanged => Combo != null && Combo.Value != Stored;

        public override bool IsDefault => Combo == null || Combo.Value == PkSettings.DefaultOf(PrefName);

        public override void OnOpen()
        {
            if (!_hooked && Combo != null)
            {
                _hooked = true;
                Combo.OnValueChangedGeneric += _ =>
                {
                    if (ApplyImmediately) ApplySelection();
                    invokeValueChanged();
                    IsDirty = true;
                };
            }
            base.OnOpen();
        }

        public override void initCurrentValue()
        {
            if (Combo == null) return;
            if (!PkSettings.IsKnown(PrefName)) Log.Warning("[Parkour] options: unknown setting '" + PrefName + "'");
            Combo.Value = Stored;
            IsDirty = true;
        }

        public override void DiscardCurrentChange()
        {
            if (Combo == null) return;
            Combo.Value = Stored;
            IsDirty = true;
        }

        public override void ApplySelection()
        {
            if (Combo == null) return;
            PkSettings.Set(PrefName, Combo.Value);
            IsDirty = true;
        }

        public override void ResetToDefault()
        {
            if (Combo == null || !ApplyDefaults) return;
            Combo.Value = PkSettings.DefaultOf(PrefName);
            if (ApplyImmediately) ApplySelection();
            invokeValueChanged();
            IsDirty = true;
        }
    }
}
