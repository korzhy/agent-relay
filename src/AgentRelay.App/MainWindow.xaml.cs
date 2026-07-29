using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AgentRelay.Core;
using AgentRelay.Windows;
using Forms = System.Windows.Forms;

namespace AgentRelay.App;

public partial class MainWindow : Window
{
    private static readonly SolidColorBrush ActiveBrush =
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#6BE3C2"));
    private static readonly SolidColorBrush IdleBrush =
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#718096"));
    private static readonly SolidColorBrush WarningBrush =
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F3B860"));
    private static readonly SolidColorBrush ErrorBrush =
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F17878"));
    private readonly RelayServices _services;
    private readonly DispatcherTimer _runtimeDebounce;
    private readonly DispatcherTimer _quotaTimer;
    private readonly DispatcherTimer _updateTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private FileSystemWatcher? _runtimeWatcher;
    private DashboardMission? _currentMission;
    private bool _loadingPolicy;
    private bool _explicitClose;
    private bool _updateCheckInProgress;
    private string? _lastNotifiedReviewPath;

    public MainWindow(RelayServices services)
    {
        _services = services;
        InitializeComponent();

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

        _runtimeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _runtimeDebounce.Tick += async (_, _) =>
        {
            _runtimeDebounce.Stop();
            await RefreshDashboardAsync();
        };
        _quotaTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _quotaTimer.Tick += async (_, _) => await RefreshQuotaAsync();
        _updateTimer = new DispatcherTimer { Interval = UpdateService.AutoApplyInterval };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync();

        Loaded += OnLoaded;
        Activated += async (_, _) => await RefreshDashboardAsync();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
            }
        };
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await LoadPolicyAsync();
        await RefreshHealthAsync();
        await RefreshQuotaAsync();
        StartRuntimeWatcher();
        await RefreshDashboardAsync();
        _quotaTimer.Start();
        _updateTimer.Start();
        await CheckForUpdatesAsync();
    }

    private async void Threshold_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingPolicy || sender is not System.Windows.Controls.RadioButton { Tag: string levelText } ||
            !Enum.TryParse<DelegationLevel>(levelText, out var level))
        {
            return;
        }

        try
        {
            await _services.Policy.SetLevelAsync(_services.Paths.CodexPolicyFile, level);
            SetThresholdDescription(level);
            StatusText.Text = $"Глобальный порог сохранён: {level.ToString().ToUpperInvariant()}.";
            await RefreshDashboardAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            await LoadPolicyAsync();
        }
    }

    private void Health_Click(object sender, RoutedEventArgs e)
    {
        var window = new DiagnosticsWindow(_services) { Owner = this };
        window.ShowDialog();
    }

    private async void ContextAction_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMission is null)
        {
            Health_Click(sender, e);
            return;
        }

        try
        {
            if (_currentMission.State.State == RelayState.Paused)
            {
                await _services.Runtime.SetPausedAsync(
                    _currentMission.Project, false, new SystemClock());
            }
            else if (_currentMission.State.State == RelayState.ReportReady &&
                     _currentMission.Delivery is { Succeeded: false } &&
                     !string.IsNullOrWhiteSpace(_currentMission.State.ReviewPromptPath))
            {
                var review = await ReadReviewEnvelopeAsync(_currentMission.Project);
                if (review?.ReviewAttemptId is null)
                {
                    throw new InvalidDataException("Review envelope is unavailable.");
                }
                await _services.Delivery.DeliverAsync(
                    _currentMission.Project,
                    review.ReviewAttemptId,
                    _currentMission.State.ReviewPromptPath);
            }
            else
            {
                Health_Click(sender, e);
                return;
            }
            await RefreshDashboardAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async Task LoadPolicyAsync()
    {
        var policy = await _services.Policy.GetAsync(_services.Paths.CodexPolicyFile);
        _loadingPolicy = true;
        ThresholdOff.IsChecked = policy.Level == DelegationLevel.Off;
        ThresholdLow.IsChecked = policy.Level == DelegationLevel.Low;
        ThresholdMedium.IsChecked = policy.Level == DelegationLevel.Medium;
        ThresholdHigh.IsChecked = policy.Level == DelegationLevel.High;
        _loadingPolicy = false;
        SetThresholdDescription(policy.Level);
    }

    private void SetThresholdDescription(DelegationLevel level)
    {
        ThresholdDescription.Text = level switch
        {
            DelegationLevel.Off => "Flash запрещён. Sol выполняет работу самостоятельно.",
            DelegationLevel.Low => "Только очевидная механическая работа с крупной ожидаемой экономией.",
            DelegationLevel.Medium => "Сбалансированная передача ограниченных и локально проверяемых задач.",
            DelegationLevel.High => "Максимум подходящей работы передаётся Flash; финальная проверка остаётся у Sol.",
            _ => string.Empty
        };
    }

    private async Task RefreshHealthAsync()
    {
        var doctor = await _services.Doctor.RunAsync();
        HealthButton.Content = doctor.Ready ? "●  Relay готов" : "●  Требуется настройка";
        HealthButton.Foreground = doctor.Ready ? ActiveBrush : WarningBrush;
        HealthButton.ToolTip = string.Join(
            Environment.NewLine,
            doctor.Checks.Select(check => $"{(check.Ready ? "✓" : "✕")} {check.Name}: {check.Detail}"));
    }

    private async Task RefreshQuotaAsync()
    {
        var quota = await _services.Quota.ReadAsync();
        if (quota.Freshness == QuotaFreshness.Fresh && quota.RemainingPercentage is int remaining)
        {
            QuotaText.Text = $"Квота: {remaining}%";
            QuotaText.Foreground = remaining <= 10 ? WarningBrush : ActiveBrush;
        }
        else
        {
            QuotaText.Text = "Квота: нет свежих данных";
            QuotaText.Foreground = IdleBrush;
        }
        QuotaChip.ToolTip = quota.RemainingPercentage is int lastKnown
            ? $"{quota.Source}\nПоследний известный снимок: {lastKnown}% · {quota.ObservedAt:yyyy-MM-dd HH:mm} UTC\n{quota.Detail}"
            : $"{quota.Source}\n{quota.Detail}";
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updateCheckInProgress)
        {
            return;
        }
        _updateCheckInProgress = true;
        try
        {
            var state = await _services.Updates.CheckAsync();
            UpdateFooterText.Text = state.Status switch
            {
                UpdateStatus.Disabled => $"v{_services.Updates.CurrentVersion} · автообновление выключено",
                UpdateStatus.Staged or UpdateStatus.Deferred =>
                    $"v{_services.Updates.CurrentVersion} · готово обновление {state.LatestVersion}",
                UpdateStatus.Installing =>
                    $"v{_services.Updates.CurrentVersion} · обновление устанавливается",
                UpdateStatus.Failed => $"v{_services.Updates.CurrentVersion} · обновление недоступно",
                _ => $"v{_services.Updates.CurrentVersion} · автообновление включено"
            };

            if (state.Status is not (UpdateStatus.Staged or UpdateStatus.Deferred))
            {
                return;
            }
            if (!await CanInstallUpdateNowAsync())
            {
                await _services.Updates.MarkDeferredAsync(
                    state, "Обновление отложено до завершения активного Flash runner.");
                StatusText.Text = $"Обновление {state.LatestVersion} загружено и отложено.";
                return;
            }
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) ||
                !_services.Updates.IsInstalledBuild(executable))
            {
                StatusText.Text =
                    $"Обновление {state.LatestVersion} проверено; автоматическая установка доступна только installed build.";
                return;
            }

            StatusText.Text = $"Устанавливается Agent Relay {state.LatestVersion}…";
            await _services.Updates.LaunchInstallerAsync(state);
            ExitForUpdate();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or HttpRequestException or
                UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"Автообновление: {exception.Message}";
        }
        finally
        {
            _updateCheckInProgress = false;
        }
    }

    private async Task<bool> CanInstallUpdateNowAsync()
    {
        foreach (var project in await _services.Projects.ListAsync())
        {
            var state = await _services.Recovery.RecoverAsync(project);
            if (state.State is RelayState.Running or RelayState.Waiting)
            {
                return false;
            }
        }
        return true;
    }

    private void ExitForUpdate()
    {
        _explicitClose = true;
        _quotaTimer.Stop();
        _updateTimer.Stop();
        _runtimeDebounce.Stop();
        _runtimeWatcher?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private async Task RefreshDashboardAsync()
    {
        var projects = await _services.Projects.ListAsync();
        var states = new List<(RegisteredProject Project, ProjectRuntimeState State)>();
        foreach (var project in projects)
        {
            states.Add((project, await _services.Recovery.RecoverAsync(project)));
        }

        var selected = MissionSelector.Select(states.Select(item =>
            new MissionCandidate(item.Project.Id, item.State.State, item.State.UpdatedAt)));
        if (selected is null)
        {
            _currentMission = null;
            RenderEmpty();
            return;
        }

        var pair = states.Single(item => item.Project.Id == selected.ProjectId);
        var activity = await ReadActivitySafelyAsync(pair.Project.Id);
        var delivery = await ReadDeliverySafelyAsync(pair.Project.Id);
        var log = await _services.Runtime.ReadLogAsync(pair.Project.Id, 1);
        var title = await ReadMissionTitleAsync(pair.Project, pair.State);
        _currentMission = new DashboardMission(
            pair.Project,
            pair.State,
            activity,
            delivery,
            log.Entries.LastOrDefault(),
            title);
        RenderMission(_currentMission);
    }

    private async Task<SolActivity?> ReadActivitySafelyAsync(string projectId)
    {
        try
        {
            return await _services.Activity.GetAsync(projectId);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            StatusText.Text = "Локальный статус Sol повреждён; подробности доступны в диагностике.";
            return null;
        }
    }

    private async Task<ReviewDeliveryState?> ReadDeliverySafelyAsync(string projectId)
    {
        try
        {
            return await _services.Delivery.GetAsync(projectId);
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            StatusText.Text = "История доставки review prompt повреждена; подробности доступны в диагностике.";
            return null;
        }
    }

    private void RenderEmpty()
    {
        MissionTitleText.Text = "Нет активной делегации";
        MissionMetaText.Text = "Sol использует Flash только когда порог и задача это оправдывают.";
        SolStatusText.Text = ThresholdOff.IsChecked == true
            ? "Делегирование выключено"
            : "Статус не подтверждён";
        SolDetailText.Text = ThresholdOff.IsChecked == true
            ? "Глобальный порог OFF: Flash не будет запущен."
            : "Нет явной операционной фазы от текущей задачи Codex.";
        SolAgeText.Text = string.Empty;
        FlashStatusText.Text = "Не задействован";
        FlashDetailText.Text = "Нет активного внешнего runner.";
        FlashAgeText.Text = string.Empty;
        SolDot.Fill = IdleBrush;
        FlashDot.Fill = IdleBrush;
        LastActionText.Text = "Без hidden reasoning · только подтверждённые действия";
        ContextPanel.Visibility = Visibility.Collapsed;
        SetCurrentStep(0);
    }

    private void RenderMission(DashboardMission mission)
    {
        var now = DateTimeOffset.UtcNow;
        var liveRunner = mission.State.State is RelayState.Running or RelayState.Waiting;
        var activityFresh = mission.Activity?.IsFresh(now) == true || liveRunner;
        MissionTitleText.Text = mission.Title;
        MissionMetaText.Text =
            $"{mission.Project.Name} · mission {Short(mission.State.MissionId)} · обновлено {FormatAge(mission.State.UpdatedAt, now)}";

        if (mission.Activity is not null)
        {
            SolStatusText.Text = SolPhaseLabel(mission.Activity.Phase, activityFresh);
            SolDetailText.Text = mission.Activity.Summary;
            SolAgeText.Text = activityFresh
                ? $"Подтверждено {FormatAge(mission.Activity.UpdatedAt, now)}"
                : $"Последняя подтверждённая фаза · {FormatAge(mission.Activity.UpdatedAt, now)}";
            SolDot.Fill = activityFresh ? ActiveBrush : IdleBrush;
        }
        else
        {
            SolStatusText.Text = mission.State.State == RelayState.ReportReady
                ? "Ожидается проверка Sol"
                : liveRunner
                    ? "Ожидает отчёт Flash"
                    : "Статус не подтверждён";
            SolDetailText.Text = liveRunner
                ? "Sol передал ограниченную задачу и ожидает структурированный отчёт."
                : "Нет свежей явной операционной фазы Sol.";
            SolAgeText.Text = string.Empty;
            SolDot.Fill = liveRunner ? ActiveBrush : IdleBrush;
        }

        (FlashStatusText.Text, FlashDetailText.Text, FlashDot.Fill) = mission.State.State switch
        {
            RelayState.Running => ("Выполняет задачу", $"Сейчас выполняет: {mission.Title}", ActiveBrush),
            RelayState.Waiting => ("Ожидает изменения", "Runner ожидает подтверждённое файловое или process-output событие.", WarningBrush),
            RelayState.ReportReady => ("Отчёт готов", "Валидный отчёт готов к независимой проверке Sol.", ActiveBrush),
            RelayState.Stalled => ("Остановлен", "Runner остановился без валидного завершения. Подробности доступны в диагностике.", ErrorBrush),
            RelayState.QuotaExhausted => ("Квота исчерпана", "Исчерпание подтверждено фактическим выводом runner.", ErrorBrush),
            RelayState.Paused => ("Пауза", "Внешнее выполнение приостановлено.", WarningBrush),
            _ => ("Не задействован", "Нет активного внешнего runner.", IdleBrush)
        };
        FlashAgeText.Text = $"Состояние подтверждено {FormatAge(mission.State.UpdatedAt, now)}";

        var step = mission.Activity?.Phase switch
        {
            SolActivityPhase.Reviewing => 4,
            SolActivityPhase.Integrating or SolActivityPhase.Completed => 5,
            _ => mission.State.State switch
            {
                RelayState.Running or RelayState.Waiting => 2,
                RelayState.ReportReady => 3,
                _ => 1
            }
        };
        SetCurrentStep(step);
        LastActionText.Text = mission.LastAction is null
            ? "Без hidden reasoning · подтверждённых действий пока нет"
            : $"Последнее действие: {FormatLastAction(mission.LastAction, mission.State.State)}";
        RenderContext(mission);

        if (mission.State.State == RelayState.ReportReady &&
            !string.IsNullOrWhiteSpace(mission.State.ReviewPromptPath) &&
            !string.Equals(_lastNotifiedReviewPath, mission.State.ReviewPromptPath, StringComparison.OrdinalIgnoreCase))
        {
            _lastNotifiedReviewPath = mission.State.ReviewPromptPath;
            _trayIcon.ShowBalloonTip(
                8000,
                "Agent Relay: отчёт готов",
                mission.Delivery?.Succeeded == true
                    ? "Точный review prompt автоматически скопирован."
                    : "Отчёт готов; требуется проверка Sol.",
                Forms.ToolTipIcon.Info);
        }
    }

    private void RenderContext(DashboardMission mission)
    {
        ContextPanel.Visibility = Visibility.Collapsed;
        if (mission.State.State == RelayState.Paused)
        {
            ContextText.Text = "Внешнее выполнение приостановлено. Прерванная миссия не перезапускается скрыто.";
            ContextActionButton.Content = "Resume";
            ContextPanel.Visibility = Visibility.Visible;
        }
        else if (mission.State.State is RelayState.Stalled or RelayState.QuotaExhausted)
        {
            ContextText.Text = mission.State.Detail ?? "Внешний runner требует внимания.";
            ContextActionButton.Content = "Диагностика";
            ContextPanel.Visibility = Visibility.Visible;
        }
        else if (mission.State.State == RelayState.ReportReady &&
                 mission.Delivery is { Succeeded: false })
        {
            ContextText.Text = $"Отчёт валиден, но clipboard недоступен: {mission.Delivery.Error}";
            ContextActionButton.Content = "Копировать снова";
            ContextPanel.Visibility = Visibility.Visible;
        }
    }

    private void SetCurrentStep(int current)
    {
        var steps = new[] { DecisionStep, FlashStep, ReportStep, ReviewStep, IntegrateStep };
        for (var index = 0; index < steps.Length; index++)
        {
            steps[index].Foreground = index + 1 == current ? ActiveBrush : IdleBrush;
            steps[index].FontWeight = index + 1 == current ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    private async Task<string> ReadMissionTitleAsync(
        RegisteredProject project,
        ProjectRuntimeState state)
    {
        if (string.IsNullOrWhiteSpace(state.HandoffId))
        {
            return $"Последняя работа · {project.Name}";
        }
        try
        {
            var controlPath = Path.Combine(project.Path, AgentRelayConstants.TransportDirectory, "control.json");
            var control = await _services.Files.ReadJsonAsync<ControlEnvelope>(controlPath);
            if (control is null ||
                !string.Equals(control.HandoffId, state.HandoffId, StringComparison.Ordinal))
            {
                return $"Hand-off {Short(state.HandoffId)}";
            }
            var taskPath = WorkspaceSafety.ResolveRelative(project.Path, control.Task.Path);
            var hash = await AtomicFileStore.Sha256Async(taskPath);
            if (!string.Equals(hash, control.Task.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return $"Hand-off {Short(state.HandoffId)}";
            }
            var task = await _services.Files.ReadJsonAsync<TaskPayload>(taskPath);
            return string.IsNullOrWhiteSpace(task?.Title) ? $"Hand-off {Short(state.HandoffId)}" : task.Title;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return $"Hand-off {Short(state.HandoffId)}";
        }
    }

    private Task<ReviewEnvelope?> ReadReviewEnvelopeAsync(RegisteredProject project)
        => _services.Files.ReadJsonAsync<ReviewEnvelope>(
            Path.Combine(project.Path, AgentRelayConstants.TransportDirectory, "review.json"));

    private void StartRuntimeWatcher()
    {
        Directory.CreateDirectory(_services.Paths.RuntimeDirectory);
        _runtimeWatcher = new FileSystemWatcher(_services.Paths.RuntimeDirectory, "*.json")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        FileSystemEventHandler changed = (_, _) => QueueRuntimeRefresh();
        RenamedEventHandler renamed = (_, _) => QueueRuntimeRefresh();
        _runtimeWatcher.Changed += changed;
        _runtimeWatcher.Created += changed;
        _runtimeWatcher.Deleted += changed;
        _runtimeWatcher.Renamed += renamed;
        _runtimeWatcher.EnableRaisingEvents = true;
    }

    private void QueueRuntimeRefresh()
        => Dispatcher.BeginInvoke(() =>
        {
            _runtimeDebounce.Stop();
            _runtimeDebounce.Start();
        });

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
        _quotaTimer.Stop();
        _updateTimer.Stop();
        _runtimeDebounce.Stop();
        _runtimeWatcher?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private static string SolPhaseLabel(SolActivityPhase phase, bool fresh)
    {
        var label = phase switch
        {
            SolActivityPhase.Evaluating => "Оценивает делегирование",
            SolActivityPhase.Delegating => "Передаёт задачу",
            SolActivityPhase.WaitingForFlash => "Ожидает отчёт Flash",
            SolActivityPhase.Working => "Выполняет свою часть",
            SolActivityPhase.Reviewing => "Проверяет результат",
            SolActivityPhase.Integrating => "Интегрирует изменения",
            SolActivityPhase.Completed => "Работа завершена",
            SolActivityPhase.Blocked => "Требуется внимание",
            _ => "Статус не подтверждён"
        };
        return fresh ? label : $"Последняя фаза: {label}";
    }

    private static string FormatLastAction(ActionLogEntry action, RelayState state)
        => action.Action switch
        {
            "dispatch" => "Flash запущен с exact model.",
            "complete" when state == RelayState.ReportReady => "Валидный отчёт Flash принят.",
            "complete" => "Внешнее выполнение завершилось без принятого отчёта.",
            "prompt-copy" => "Точный review prompt скопирован.",
            "prompt-copy-failed" => "Clipboard временно недоступен.",
            "pause" => "Будущие dispatch приостановлены.",
            "resume" => "Будущие dispatch снова разрешены.",
            _ => $"{action.Action} · {action.Detail}"
        };

    private static string FormatAge(DateTimeOffset timestamp, DateTimeOffset now)
    {
        var age = now - timestamp;
        if (age < TimeSpan.Zero)
        {
            return "только что";
        }
        if (age < TimeSpan.FromMinutes(1))
        {
            return $"{Math.Max(1, (int)age.TotalSeconds)} сек. назад";
        }
        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} мин. назад";
        }
        return timestamp.ToLocalTime().ToString("dd.MM HH:mm");
    }

    private static string Short(string? id)
        => string.IsNullOrWhiteSpace(id) ? "—" : id[..Math.Min(8, id.Length)];

    private sealed record DashboardMission(
        RegisteredProject Project,
        ProjectRuntimeState State,
        SolActivity? Activity,
        ReviewDeliveryState? Delivery,
        ActionLogEntry? LastAction,
        string Title);
}
