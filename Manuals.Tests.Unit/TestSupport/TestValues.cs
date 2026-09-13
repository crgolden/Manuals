namespace Manuals.Tests.Unit.TestSupport;

internal static class TestValues
{
    internal static string LowercaseToken(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)Random.Shared.Next('a', 'z' + 1)));

    internal static string NewBlank() => new string(' ', Random.Shared.Next(1, 4));

    internal static string NewChatTitle() => $"{LowercaseToken(5)} {LowercaseToken(7)}";

    internal static string NewModelName() => $"{LowercaseToken(4)}-{LowercaseToken(4)}";

    internal static string NewMaxOutputTokenCount() =>
        Random.Shared.Next(256, 4096).ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static string NewInstructions() => $"{LowercaseToken(8)} {LowercaseToken(6)}";

    internal static double NewSortedSetScore() => Random.Shared.Next(1, 1_000_000);

    internal static long NewRedisListLength() => Random.Shared.Next(1, 100);

    internal static string NewResponseId() => $"resp_{LowercaseToken(12)}";

    internal static string NewOutputMessageId() => $"msg_{LowercaseToken(10)}";

    internal static string NewNonGuidSortedSetMember() => LowercaseToken(12);

    internal static string NewUnparseableTimestamp() => LowercaseToken(9);

    internal static string NewMessageText() => $"{LowercaseToken(6)} {LowercaseToken(9)}";

    internal static string NewUserId() => Guid.NewGuid().ToString();

    internal static string NewEmailAddress() => $"{LowercaseToken(10)}@{LowercaseToken(8)}.example";

    internal static string NewFailureReason() => $"failure-{LowercaseToken(10)}";

    internal static int NewLatencyMilliseconds() => Random.Shared.Next(1, 1000);

    internal static string NewRequestActivityName() => $"GET /{LowercaseToken(6)}";

    internal static string NewWorkActivityName() => $"{LowercaseToken(7)}.{LowercaseToken(5)}.{LowercaseToken(6)}";

    internal static long NewUnixSeconds() =>
        DateTimeOffset.UtcNow.AddSeconds(-Random.Shared.Next(1, 10_000_000)).ToUnixTimeSeconds();
}
