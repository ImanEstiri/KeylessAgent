using System.Windows.Controls;

namespace ImanSoftware.KeylessAgent.Wpf.Views;

/// <summary>
/// View of the doctor page; all behavior lives in <c>DoctorViewModel</c> and
/// <c>ChromeViewModel</c>.
/// </summary>
public partial class DoctorView : UserControl
{
    /// <summary>Creates the doctor page view.</summary>
    public DoctorView()
    {
        InitializeComponent();
    }
}
