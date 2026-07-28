using System.Diagnostics;
using System.IO;
using System.Windows;
using AgentRelay.Windows;

namespace AgentRelay.App;

public partial class DiagnosticsWindow : Window
{
    private readonly RelayServices _services;

    public DiagnosticsWindow(RelayServices services)
    {
        _services = services;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _services.Codex.InstallOrRepairAsync();
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                exception.Message, "Agent Relay", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_services.Paths.DataRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", _services.Paths.DataRoot)
        {
            UseShellExecute = true
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

    private async Task RefreshAsync()
    {
        var doctor = await _services.Doctor.RunAsync();
        DoctorText.Text = string.Join(
            Environment.NewLine,
            doctor.Checks.Select(check => $"{(check.Ready ? "✓" : "✕")} {check.Name}: {check.Detail}"));

        var quota = await _services.Quota.ReadAsync();
        QuotaText.Text = quota.Freshness == QuotaFreshness.Fresh
            ? $"Свежий снимок: {quota.RemainingPercentage}% осталось.\n{quota.Detail}\nИсточник: {quota.Source}"
            : quota.RemainingPercentage is int lastKnown
                ? $"Нет свежих данных. Последний снимок: {lastKnown}% от {quota.ObservedAt:yyyy-MM-dd HH:mm} UTC.\n" +
                  $"{quota.Detail}\nИсточник: {quota.Source}"
                : $"Данные недоступны.\n{quota.Detail}\nИсточник: {quota.Source}";

        var invalid = 0;
        var incomplete = 0;
        foreach (var project in await _services.Projects.ListAsync())
        {
            var result = await _services.Runtime.ReadLogAsync(project.Id, 1);
            invalid += result.InvalidRecordCount;
            incomplete += result.HasIncompleteTail ? 1 : 0;
        }
        LogText.Text = invalid == 0 && incomplete == 0
            ? "Совместимые action logs прочитаны без ошибок."
            : $"Пропущено повреждённых записей: {invalid}; файлов с оборванным хвостом: {incomplete}. " +
              "Существующие валидные записи и protocol payloads сохранены.";
    }
}
