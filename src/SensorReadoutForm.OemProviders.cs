using System.Collections.Generic;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private IEnumerable<SensorRow> GetOemProviderRows()
    {
        return GetOemProviderRows(false);
    }

    private IEnumerable<SensorRow> GetOemProviderRows(bool diagnosticsMode)
    {
        return GetPlugInRows(diagnosticsMode);
    }
}
