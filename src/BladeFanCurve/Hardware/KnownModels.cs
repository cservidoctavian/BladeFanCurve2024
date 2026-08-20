namespace BladeFanCurve.Hardware;

public sealed record KnownModel(int ProductId, string Name, string MarketingNumber, byte SetRpmArg0);

/// <summary>
/// Friendly names for the Razer laptop product ids that are publicly documented.
/// Discovery does not depend on this list — the control interface is found by
/// probing — but a recognised model gives better log lines and a better starting
/// guess for the firmware-dependent "set fan rpm" argument layout.
/// </summary>
public static class KnownModels
{
    private static readonly KnownModel[] Models =
    {
        new(0x0270, "Razer Blade 15 Advanced (2020)", "RZ09-0330", 0x00),
        new(0x026F, "Razer Blade 15 Advanced (2021)", "RZ09-0409", 0x00),
        new(0x0287, "Razer Blade 14 (2021)",          "RZ09-0370", 0x00),
        new(0x028C, "Razer Blade 14 (2022)",          "RZ09-0427", 0x00),
        new(0x028A, "Razer Blade 15 (2022)",          "RZ09-0421", 0x00),
        new(0x029D, "Razer Blade 14 (2023)",          "RZ09-0482", 0x00),
        new(0x029F, "Razer Blade 16 (2023)",          "RZ09-0483", 0x00),
        new(0x02A0, "Razer Blade 18 (2023)",          "RZ09-0484", 0x00),

        // 2024 family. Both use argument layout 0x01 for the set-rpm command.
        new(0x02B6, "Razer Blade 14 (2024)",          "RZ09-0508", 0x01),
        new(0x02B7, "Razer Blade 16 (2024)",          "RZ09-0509", 0x01),
    };

    public static KnownModel? Find(int productId) =>
        Models.FirstOrDefault(m => m.ProductId == productId);

    public static string Describe(int productId)
    {
        var model = Find(productId);
        return model == null
            ? $"Unrecognised Razer laptop (1532:{productId:X4})"
            : $"{model.Name} ({model.MarketingNumber}, 1532:{productId:X4})";
    }
}
