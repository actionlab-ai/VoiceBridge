using System.Windows.Forms;

namespace VoiceBridge.Desktop;

internal static class ControlInvokeExtensions
{
    public static IAsyncResult BeginInvoke(this Control control, Action action)
    {
        return control.BeginInvoke((Delegate)action);
    }
}
