using System.Collections.Generic;

public sealed partial class SensorReadoutForm
{
    internal static IEnumerable<string> ControlHelpLabelKeys(string label)
    {
        return activeLanguage.ControlLabelKeys(label);
    }
}
