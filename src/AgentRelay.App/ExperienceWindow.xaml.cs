using System.Windows;
using System.Windows.Controls;
using AgentRelay.Core;

namespace AgentRelay.App;

public partial class ExperienceWindow : Window
{
    private readonly RelayServices _services;
    private int _selectionRevision;

    public ExperienceWindow(RelayServices services)
    {
        _services = services;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try
            {
                ProjectsBox.ItemsSource = await _services.Projects.ListAsync();
                if (ProjectsBox.Items.Count > 0) ProjectsBox.SelectedIndex = 0;
                else SummaryText.Text = "Пока нет зарегистрированных проектов.";
            }
            catch (Exception exception) { SummaryText.Text = exception.Message; }
        };
    }

    private async void Project_Changed(object sender, SelectionChangedEventArgs e)
    {
        var revision = ++_selectionRevision;
        if (ProjectsBox.SelectedItem is not RegisteredProject project) return;
        try
        {
            var summary = await new ExperienceStore(_services.Paths, _services.Files).RecallAsync(project.Id);
            if (revision != _selectionRevision) return;
            SummaryText.Text = $"Последние 90 дней (выборка до 200 записей): {summary.SampleCount} итогов · " +
                $"сразу принято: {summary.Accepted} · доработано: {summary.Corrected} · " +
                $"отклонено: {summary.Rejected} · блокировки: {summary.Blocked} · прекращено: {summary.Abandoned}";
            DetailsText.Text = summary.Recent.Count == 0
                ? "История ещё пуста. После handoff Codex сохраняет короткий итог независимой проверки. " +
                  "Отчёт исполнителя сам по себе не считается успехом.\n\n" +
                  "Дополнительные модели и фоновые запросы не используются."
                : string.Join("\n\n────────────\n\n", summary.Recent.Select(entry =>
                    $"{entry.RecordedAt:yyyy-MM-dd HH:mm} UTC · {entry.Model}\n" +
                    $"{entry.TaskKind} · {entry.Outcome} · {entry.Failure}\n" +
                    $"{entry.Lesson}"));
            if (summary.SkippedInvalid > 0) DetailsText.Text += $"\n\nПропущено повреждённых записей: {summary.SkippedInvalid}.";
        }
        catch (Exception exception)
        {
            if (revision == _selectionRevision) { SummaryText.Text = exception.Message; DetailsText.Clear(); }
        }
    }
}
