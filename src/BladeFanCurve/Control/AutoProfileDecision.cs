using BladeFanCurve.Config;

namespace BladeFanCurve.Control;

public enum AutoProfileAction
{
    /// <summary>Leave the profile where it is.</summary>
    None,

    /// <summary>Move to the battery profile, remembering where we came from.</summary>
    SwitchToBattery,

    /// <summary>Put back whatever was active before the charger came out.</summary>
    RestorePrevious,

    /// <summary>Forget the remembered profile without changing anything.</summary>
    ForgetPrevious,
}

public readonly record struct AutoProfileOutcome(AutoProfileAction Action, string Target, string Reason)
{
    public static readonly AutoProfileOutcome Nothing = new(AutoProfileAction.None, "", "");
}

/// <summary>
/// Decides what should happen to the active profile when the charger goes in or comes
/// out. Pulled out of the control loop because it is a small state machine with
/// several ways to be subtly wrong — overriding a choice the user made by hand,
/// firing on the first reading at startup, or restoring a profile that no longer
/// exists — and none of those are things a laptop can be asked to reproduce on demand.
/// </summary>
public static class AutoProfileDecision
{
    public static AutoProfileOutcome Decide(
        bool firstObservation,
        bool onBattery,
        AutomationSettings settings,
        string activeProfile)
    {
        if (!settings.SwitchProfileOnBattery) return AutoProfileOutcome.Nothing;
        if (string.IsNullOrWhiteSpace(settings.BatteryProfile)) return AutoProfileOutcome.Nothing;

        var alreadyOnBatteryProfile =
            activeProfile.Equals(settings.BatteryProfile, StringComparison.OrdinalIgnoreCase);

        if (onBattery)
        {
            // Nothing to do if it is already the battery profile — and importantly,
            // nothing to remember either, or plugging back in would "restore" the
            // battery profile onto itself.
            if (alreadyOnBatteryProfile) return AutoProfileOutcome.Nothing;

            return new AutoProfileOutcome(AutoProfileAction.SwitchToBattery, settings.BatteryProfile,
                firstObservation ? "started on battery" : "charger unplugged");
        }

        // Back on mains.
        if (firstObservation) return AutoProfileOutcome.Nothing;
        if (!settings.RestoreProfileOnAc) return AutoProfileOutcome.Nothing;
        if (string.IsNullOrWhiteSpace(settings.ProfileBeforeBattery)) return AutoProfileOutcome.Nothing;

        // If the profile is no longer the one we switched to, the user changed it by
        // hand while on battery. That is a deliberate choice and outranks us.
        if (!alreadyOnBatteryProfile)
            return new AutoProfileOutcome(AutoProfileAction.ForgetPrevious, "",
                "profile was chosen by hand while on battery");

        return new AutoProfileOutcome(AutoProfileAction.RestorePrevious,
            settings.ProfileBeforeBattery, "charger plugged in");
    }
}
