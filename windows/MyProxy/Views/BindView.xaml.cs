
namespace MyProxy.Views;

public partial class BindView : System.Windows.Controls.UserControl
{
    public BindView()
    {
        InitializeComponent();
        _ = new PairingCodeInput(PairingCodeBox);
    }
}
