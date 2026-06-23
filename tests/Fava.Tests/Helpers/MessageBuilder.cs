using Fava.Models;

namespace Fava.Tests.Helpers;

internal static class MessageBuilder
{
    private static long _seq = 1000;

    public static SmsMessage Any() =>
        new(
            SmsId: Interlocked.Increment(ref _seq),
            CompanyId: 1,
            UserId: 1,
            CampaignId: 1,
            ChannelId: 1,
            Destination: "+14155551234",
            Body: "test message",
            PartCount: 1,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            CorrelationId: Guid.NewGuid().ToString("N"));
}
