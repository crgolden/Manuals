namespace Manuals.Tests.Unit.TestSupport;

internal static class TestValues
{
    internal static string LowercaseToken(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)Random.Shared.Next('a', 'z' + 1)));

    internal static string NewChatTitle() => $"{LowercaseToken(5)} {LowercaseToken(7)}";

    internal static string NewMessageText() => $"{LowercaseToken(6)} {LowercaseToken(9)}";

    internal static string NewUserId() => Guid.NewGuid().ToString();

    internal static string NewEmailAddress() => $"{LowercaseToken(10)}@{LowercaseToken(8)}.example";

    internal static string NewFailureReason() => $"failure-{LowercaseToken(10)}";

    internal static int NewLatencyMilliseconds() => Random.Shared.Next(1, 1000);

    internal static long NewUnixSeconds() =>
        DateTimeOffset.UtcNow.AddSeconds(-Random.Shared.Next(1, 10_000_000)).ToUnixTimeSeconds();
}
