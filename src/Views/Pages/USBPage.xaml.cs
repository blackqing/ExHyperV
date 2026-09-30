using System.Windows.Controls;

namespace ExHyperV.Views
{
    public partial class USBPage : Page
    {
        public USBPage()
        {
            InitializeComponent();
            Loaded += (_, _) => (DataContext as ViewModels.USBPageViewModel)?.StartViewMonitoring();
            Unloaded += (_, _) => (DataContext as ViewModels.USBPageViewModel)?.StopViewMonitoring();
        }
    }
}
