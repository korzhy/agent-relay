using System.Windows;
using AgentRelay.Core;

namespace AgentRelay.App;

public partial class AccountsWindow : Window
{
    private readonly RelayServices _services;
    private ManagedAccountRegistry _registry = ManagedAccountRegistry.Empty;

    public AccountsWindow(RelayServices services)
    {
        _services = services;
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _registry = await _services.Accounts.ListAsync();
        var settings = await _services.Accounts.GetSettingsAsync();
        ThresholdText.Text = settings.RotationThreshold.ToString();
        AccountsGrid.ItemsSource = _registry.Accounts.Select(account => new AccountRow(
            account.Id,
            account.Id == _registry.ActiveAccountId ? "АКТИВЕН" : account.Eligibility.ToString(),
            account.Label,
            account.Email ?? "—",
            account.Tier ?? "—",
            account.Quota is null ? "—" : $"{account.Quota.RemainingPercent}%",
            account.LastCheckedAt?.ToLocalTime().ToString("dd.MM HH:mm") ?? "—",
            account.Diagnostic ?? "—")).ToArray();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
        => await RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(LabelText.Text))
                throw new ArgumentException("Введите метку аккаунта.");
            StatusText.Text = "Откроется штатный OAuth agy…";
            await _services.Accounts.AddAsync(
                LabelText.Text, _services.Doctor.ResolveAgyPath(), _registry.Accounts.Count == 0);
            LabelText.Clear();
        });

    private async void Activate_Click(object sender, RoutedEventArgs e)
        => await RunAsync(async () => await _services.Accounts.ActivateAsync(
            SelectedId(), _services.Doctor.ResolveAgyPath()));

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await RunAsync(async () => await _services.Accounts.RefreshAsync(
            _services.Doctor.ResolveAgyPath(), SelectedId(), false));

    private async void Remove_Click(object sender, RoutedEventArgs e)
        => await RunAsync(async () =>
        {
            var id = SelectedId();
            if (System.Windows.MessageBox.Show("Удалить выбранный managed account из Agent Relay?",
                    "Agent Relay", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                await _services.Accounts.RemoveAsync(id);
            }
        });

    private async void Settings_Click(object sender, RoutedEventArgs e)
        => await RunAsync(async () =>
        {
            if (!int.TryParse(ThresholdText.Text, out var threshold))
                throw new ArgumentException("Порог должен быть целым числом 1–50.");
            await _services.Accounts.SetThresholdAsync(threshold);
        });

    private string SelectedId()
        => (AccountsGrid.SelectedItem as AccountRow)?.Id
           ?? throw new InvalidOperationException("Выберите аккаунт.");

    private async Task RunAsync(Func<Task> action)
    {
        IsEnabled = false;
        try
        {
            await action();
            StatusText.Text = "Готово.";
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private sealed record AccountRow(
        string Id,
        string Status,
        string Label,
        string Email,
        string Tier,
        string Quota,
        string Checked,
        string Diagnostic);
}
