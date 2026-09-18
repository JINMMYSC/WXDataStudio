using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed class TimelineValidationService
{
    public IReadOnlyList<MigrationCheckItem> Validate(IEnumerable<WeChatMessage> source)
    {
        var messages = source.ToList();
        var issues = new List<MigrationCheckItem>();

        foreach (var duplicate in messages.GroupBy(x => x.LocalId).Where(x => x.Count() > 1))
            issues.Add(new("duplicate-local-id", MigrationCheckSeverity.Error,
                $"Duplicate local message id: {duplicate.Key}."));

        long previousTime = 0;
        long previousSequence = 0;
        foreach (var message in messages)
        {
            if (message.CreateTime <= 0)
                issues.Add(new("invalid-time", MigrationCheckSeverity.Warning,
                    $"Message {message.LocalId} has an invalid timestamp."));
            if (previousTime > 0 && message.CreateTime < previousTime)
                issues.Add(new("time-order", MigrationCheckSeverity.Warning,
                    $"Message {message.LocalId} is earlier than the previous message."));
            if (previousSequence > 0 && message.Sequence > 0 && message.Sequence < previousSequence)
                issues.Add(new("sequence-order", MigrationCheckSeverity.Warning,
                    $"Message {message.LocalId} has a decreasing sequence value."));
            previousTime = Math.Max(previousTime, message.CreateTime);
            if (message.Sequence > 0) previousSequence = Math.Max(previousSequence, message.Sequence);
        }

        return issues;
    }
}
