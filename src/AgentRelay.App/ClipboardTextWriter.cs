using AgentRelay.Windows;

namespace AgentRelay.App;

internal sealed class ClipboardTextWriter : IClipboardWriter
{
    public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                System.Windows.Clipboard.SetText(text);
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(new InvalidOperationException(
                    "Windows clipboard is temporarily unavailable.", exception));
            }
        })
        {
            IsBackground = true,
            Name = "AgentRelay.Clipboard"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
