namespace Manuals;

using System.Diagnostics;

public static class Telemetry
{
    public static readonly ActivitySource ActivitySource = new(nameof(Manuals), "1.0.0");

    public static Activity? StartActivity(string name) =>
        ActivitySource.StartActivity(name, ActivityKind.Internal, parentContext: default);
}
