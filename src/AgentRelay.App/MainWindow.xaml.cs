using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AgentRelay.Core;
using Forms = System.Windows.Forms;

namespace AgentRelay.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly RelayServices _services;
    private readonly DispatcherTimer _timer;
    private readonly Forms.NotifyIcon _trayIcon;
    private ProjectRow? _selectedProject;
    private bool _explicitClose;
    private DateTimeOffset _lastQuotaRefresh = DateTimeOffset.MinValue;

    public MainWindow(RelayServices services)
    {
        _services = services;
        InitializeComponent();
        DataContext = this;
        DelegationLevelBox.ItemsSource = Enum.GetValues<DelegationLevel>();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Agent Relay",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFromTray());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _explicitClose = true;
            Close();
        });
        _trayIcon.ContextMenuStrip = menu;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) =>
        {
            await RefreshProjectsAsync();
            if (DateTimeOffset.UtcNow - _lastQuotaRefresh >= TimeSpan.FromSeconds(30))
            {
                await RefreshQuotaAsync();
            }
        };
        Loaded += async (_, _) =>
        {
            await RunDoctorAsync();
            await LoadPolicyAsync();
            await RefreshProjectsAsync();
            await RefreshQuotaAsync();
            _timer.Start();
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
            }
        };
        Closing += OnClosing;
    }

    public ObservableCollection<ProjectRow> Projects { get; } = [];

    public ProjectRow? SelectedProject
    {
        get => _selectedProject;
        set
        {
            _selectedProject = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void Doctor_Click(object sender, RoutedEventArgs e)
    {
        await RunDoctorAsync();
        await RefreshQuotaAsync();
    }

    private async void RepairCodex_Click(object sender, RoutedEventArgs e)
    {
        await GuardAsync(async () =>
        {
            await _services.Codex.InstallOrRepairAsync();
            await RunDoctorAsync();
            StatusText.Text = "Интеграция Codex установлена идемпотентно.";
        });
    }

    private async void ApplyPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (DelegationLevelBox.SelectedItem is not DelegationLevel level)
        {
            return;
        }
        await GuardAsync(async () =>
        {
            await _services.Policy.SetLevelAsync(_services.Paths.CodexPolicyFile, level);
            StatusText.Text = $"External delegation threshold: {level.ToString().ToUpperInvariant()}.";
        });
    }

    private async void AddProject_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Выберите Git/workspace проект для Agent Relay",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }
        await GuardAsync(async () =>
        {
            await _services.Projects.AddAsync(dialog.SelectedPath);
            await RefreshProjectsAsync();
            StatusText.Text = "Проект зарегистрирован; репозиторий не изменён.";
        });
    }

    private async void RemoveProject_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null)
        {
            return;
        }
        await GuardAsync(async () =>
        {
            await _services.Projects.RemoveAsync(SelectedProject.Id);
            await RefreshProjectsAsync();
            StatusText.Text = "Удалена только регистрация; история проекта сохранена.";
        });
    }

    private async void Trust_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null)
        {
            return;
        }
        var result = System.Windows.MessageBox.Show(
            $"Вы доверяете workspace:\n{SelectedProject.Path}\n\n" +
            "Agent Relay сможет запускать agy.exe с `--mode accept-edits --dangerously-skip-permissions` " +
            "только в этом workspace. Flash сможет изменять файлы без интерактивных подтверждений.",
            "One-time workspace trust",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }
        await GuardAsync(async () =>
        {
            await _services.Projects.TrustAsync(SelectedProject.Id);
            await RefreshProjectsAsync();
            StatusText.Text = "One-time trust consent сохранён локально.";
        });
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null)
        {
            return;
        }
        if (!SelectedProject.IsTrusted)
        {
            System.Windows.MessageBox.Show(
                "Сначала требуется явное one-time trust consent для этого workspace.",
                "Dispatch blocked",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите текстовый task payload",
            Filter = "Task files (*.md;*.txt)|*.md;*.txt|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        await GuardAsync(async () =>
        {
            var project = await _services.Projects.FindAsync(SelectedProject.Id)
                ?? throw new InvalidOperationException("Проект больше не зарегистрирован.");
            var instructions = await File.ReadAllTextAsync(dialog.FileName);
            var handoff = await _services.Protocol.PublishAsync(
                project.Path,
                new MissionRequest(Path.GetFileNameWithoutExtension(dialog.FileName), instructions, []));
            StatusText.Text = $"Handoff {handoff.Control.HandoffId} опубликован. Запускается exact Flash executor.";
            await RefreshProjectsAsync();
            var result = await _services.CreateRunner().RunAsync(
                project, handoff, _services.Doctor.ResolveAgyPath());
            await RefreshProjectsAsync();
            StatusText.Text = result.Detail;
            if (result.State == RelayState.ReportReady)
            {
                _trayIcon.ShowBalloonTip(
                    8000,
                    "Agent Relay: report ready",
                    "Validated report is ready. Copy the exact review prompt into the open Codex task.",
                    Forms.ToolTipIcon.Info);
            }
        });
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null)
        {
            return;
        }
        await GuardAsync(async () =>
        {
            var project = await _services.Projects.FindAsync(SelectedProject.Id)
                ?? throw new InvalidOperationException("Проект не найден.");
            await _services.Runtime.SetPausedAsync(project, true, new SystemClock());
            await RefreshProjectsAsync();
        });
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null)
        {
            return;
        }
        await GuardAsync(async () =>
        {
            var project = await _services.Projects.FindAsync(SelectedProject.Id)
                ?? throw new InvalidOperationException("Проект не найден.");
            await _services.Runtime.SetPausedAsync(project, false, new SystemClock());
            await RefreshProjectsAsync();
            StatusText.Text = "Dispatch снова разрешён; прерванная миссия не перезапускается скрыто.";
        });
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null)
        {
            return;
        }
        var path = _services.Runtime.ProjectLogDirectory(SelectedProject.Id);
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private async void CopyReviewPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null)
        {
            return;
        }
        await GuardAsync(async () =>
        {
            var state = await _services.Runtime.ReadAsync(SelectedProject.Id);
            var reviewPromptPath = state?.ReviewPromptPath;
            if (string.IsNullOrWhiteSpace(reviewPromptPath) ||
                !File.Exists(reviewPromptPath))
            {
                throw new InvalidOperationException("Review prompt ещё не готов.");
            }
            var prompt = await File.ReadAllTextAsync(reviewPromptPath);
            System.Windows.Clipboard.SetText(prompt);
            StatusText.Text = "Точный review prompt скопирован. Codex не запускался скрыто.";
        });
    }

    private async void ProjectsGrid_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
        => await RefreshActionLogAsync();

    private async Task RunDoctorAsync()
    {
        DoctorSummary.Text = "Проверка...";
        var report = await _services.Doctor.RunAsync();
        DoctorSummary.Text = string.Join(
            Environment.NewLine,
            report.Checks.Select(check => $"{(check.Ready ? "✓" : "✕")} {check.Name}: {check.Detail}"));
    }

    private async Task LoadPolicyAsync()
    {
        var policy = await _services.Policy.GetAsync(_services.Paths.CodexPolicyFile);
        DelegationLevelBox.SelectedItem = policy.Level;
    }

    private async Task RefreshQuotaAsync()
    {
        _lastQuotaRefresh = DateTimeOffset.UtcNow;
        var snapshot = await _services.Quota.ReadAsync();
        if (snapshot.RemainingPercentage is not int remaining)
        {
            QuotaSummary.Text = $"N/A — {snapshot.Detail}";
            QuotaProgress.Visibility = Visibility.Collapsed;
            return;
        }

        QuotaSummary.Text = snapshot.Freshness == AgentRelay.Windows.QuotaFreshness.Stale
            ? $"{remaining}% осталось · устаревшее значение · {snapshot.ObservedAt:yyyy-MM-dd HH:mm} UTC"
            : $"{remaining}% осталось · обновлено {snapshot.ObservedAt:HH:mm:ss} UTC";
        QuotaProgress.Value = remaining;
        QuotaProgress.Visibility = Visibility.Visible;
        QuotaProgress.ToolTip = snapshot.Detail;
    }

    private async Task RefreshProjectsAsync()
    {
        var selection = SelectedProject?.Id;
        var projects = await _services.Projects.ListAsync();
        var rows = new List<ProjectRow>();
        foreach (var project in projects)
        {
            var state = await _services.Recovery.RecoverAsync(project);
            rows.Add(new ProjectRow(
                project.Id,
                project.Name,
                project.Path,
                project.TrustedAt is not null,
                state.State,
                state.Detail ?? string.Empty));
        }
        Projects.Clear();
        foreach (var row in rows)
        {
            Projects.Add(row);
        }
        SelectedProject = Projects.FirstOrDefault(item => item.Id == selection) ?? Projects.FirstOrDefault();
        await RefreshActionLogAsync();
    }

    private async Task RefreshActionLogAsync()
    {
        if (SelectedProject is null)
        {
            ActionLogBox.Text = string.Empty;
            return;
        }
        var path = Path.Combine(
            _services.Runtime.ProjectLogDirectory(SelectedProject.Id), "actions.jsonl");
        if (!File.Exists(path))
        {
            ActionLogBox.Text = "Нет действий.";
            return;
        }

        var lines = await File.ReadAllLinesAsync(path);
        var recent = new List<string>();
        foreach (var line in lines.TakeLast(12))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<AgentRelay.Windows.ActionLogEntry>(
                    line, JsonSupport.Options);
                if (entry is not null)
                {
                    recent.Add(
                        $"{entry.Timestamp:HH:mm:ss}  {entry.Action,-10} {entry.Detail}" +
                        (entry.ExitCode is null ? string.Empty : $" [exit {entry.ExitCode}]"));
                }
            }
            catch (JsonException)
            {
                recent.Add("invalid log record");
            }
        }
        ActionLogBox.Text = string.Join(Environment.NewLine, recent);
        ActionLogBox.ScrollToEnd();
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            System.Windows.MessageBox.Show(
                exception.Message, "Agent Relay", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_explicitClose)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(
                3000, "Agent Relay", "Agent Relay продолжает мониторинг в tray.", Forms.ToolTipIcon.Info);
            return;
        }
        _timer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record ProjectRow(
    string Id,
    string Name,
    string Path,
    bool IsTrusted,
    RelayState RelayState,
    string Detail)
{
    public string Trust => IsTrusted ? "trusted" : "blocked";
    public string State => RelayState.ToString().ToLowerInvariant();
}
