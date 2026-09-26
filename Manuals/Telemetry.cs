namespace Manuals;

using System.Diagnostics;

public sealed class Telemetry : IDisposable
{
    public const string SourceName = nameof(Manuals);

    public const string ChatIdTag = "chat.id";

    public const string UserIdTag = "user.id";

    public const string AiModelTag = "ai.model";

    public const string ChatCountTag = "chat_count";

    public const string MessageCountTag = "message_count";

    public const string VerifiedTag = "verified";

    private readonly ActivitySource _activitySource = new(SourceName, typeof(Telemetry).Assembly.GetName().Version?.ToString());

    public Activity? StartActivity(string name) =>
        _activitySource.StartActivity(name);

    public void Dispose() => _activitySource.Dispose();
}
