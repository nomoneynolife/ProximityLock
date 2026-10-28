using System.Windows;

namespace ProximityLock;

public partial class PasswordDialog : Window
{
    public string UserName => UserBox.Text.Trim();
    public string Domain => DomainBox.Text.Trim();
    public string Password => PwdBox.Password;

    public PasswordDialog()
    {
        InitializeComponent();
        // 默认当前登录用户 + 本机名 (本地账号), 一般不用改
        UserBox.Text = Environment.UserName;
        DomainBox.Text = Environment.MachineName;
        Loaded += (_, _) => PwdBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (UserName.Length == 0)
        {
            System.Windows.MessageBox.Show("用户名不能为空。", "ProximityLock",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Password.Length == 0)
        {
            System.Windows.MessageBox.Show("密码不能为空。", "ProximityLock",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
