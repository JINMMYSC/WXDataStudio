using System.Text;
using System.Text.Json;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed record MessageKindCount(
    MessageKind Kind,
    int Count,
    int Outgoing,
    int Incoming);

public sealed record UnknownMessageType(
    int RawType,
    int Count);

/// <summary>
/// Privacy-safe aggregate over loaded messages. It records counts and raw type
/// numbers only, never message content, chat names or device identifiers.
/// </summary>
public sealed class MessageCensus
{
    public int ConversationCount { get; init; }
    public int GroupConversationCount { get; init; }
    public int MessageCount { get; init; }
    public int OutgoingCount { get; init; }
    public int IncomingCount { get; init; }
    public int GroupMessageCount { get; init; }
    public int GroupSenderResolvedCount { get; init; }
    public int SensitiveCount { get; init; }
    public IReadOnlyList<MessageKindCount> Kinds { get; init; } = Array.Empty<MessageKindCount>();
    public IReadOnlyList<UnknownMessageType> UnknownRawTypes { get; init; } =
        Array.Empty<UnknownMessageType>();
    /// <summary>Appmsg sub-type histogram over every type-49 message.</summary>
    public IReadOnlyList<UnknownMessageType> AppMessageTypes { get; init; } =
        Array.Empty<UnknownMessageType>();
    /// <summary>Appmsg sub-type histogram restricted to still-unclassified messages.</summary>
    public IReadOnlyList<UnknownMessageType> UnclassifiedAppMessageTypes { get; init; } =
        Array.Empty<UnknownMessageType>();
    public IReadOnlyList<MessageKind> MissingKinds { get; init; } = Array.Empty<MessageKind>();

    public int CountOf(MessageKind kind) =>
        Kinds.FirstOrDefault(x => x.Kind == kind)?.Count ?? 0;

    public int UnknownMessageCount => CountOf(MessageKind.Unknown);

    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine($"conversations={ConversationCount}");
        text.AppendLine($"group-conversations={GroupConversationCount}");
        text.AppendLine($"messages={MessageCount}");
        text.AppendLine($"outgoing={OutgoingCount}");
        text.AppendLine($"incoming={IncomingCount}");
        text.AppendLine($"group-messages={GroupMessageCount}");
        text.AppendLine($"group-sender-resolved={GroupSenderResolvedCount}");
        text.AppendLine($"sensitive-readonly={SensitiveCount}");
        foreach (var kind in Kinds)
            text.AppendLine(
                $"kind-{kind.Kind.ToString().ToLowerInvariant()}=" +
                $"{kind.Count} (out={kind.Outgoing}, in={kind.Incoming})");
        foreach (var unknown in UnknownRawTypes)
            text.AppendLine($"unknown-raw-type-{unknown.RawType}={unknown.Count}");
        foreach (var appType in AppMessageTypes)
            text.AppendLine($"appmsg-subtype-{appType.RawType}={appType.Count}");
        foreach (var appType in UnclassifiedAppMessageTypes)
            text.AppendLine($"unclassified-appmsg-subtype-{appType.RawType}={appType.Count}");
        text.AppendLine(MissingKinds.Count == 0
            ? "missing-kinds=(none)"
            : "missing-kinds=" + string.Join(',', MissingKinds));
        return text.ToString();
    }

    public string ToJson()
    {
        var payload = new
        {
            conversationCount = ConversationCount,
            groupConversationCount = GroupConversationCount,
            messageCount = MessageCount,
            outgoingCount = OutgoingCount,
            incomingCount = IncomingCount,
            groupMessageCount = GroupMessageCount,
            groupSenderResolvedCount = GroupSenderResolvedCount,
            sensitiveCount = SensitiveCount,
            kinds = Kinds.Select(x => new
            {
                kind = x.Kind.ToString(),
                count = x.Count,
                outgoing = x.Outgoing,
                incoming = x.Incoming
            }),
            unknownRawTypes = UnknownRawTypes.Select(x => new
            {
                rawType = x.RawType,
                count = x.Count
            }),
            appMessageTypes = AppMessageTypes.Select(x => new
            {
                subType = x.RawType,
                count = x.Count
            }),
            unclassifiedAppMessageTypes = UnclassifiedAppMessageTypes.Select(x => new
            {
                subType = x.RawType,
                count = x.Count
            }),
            missingKinds = MissingKinds.Select(x => x.ToString())
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
}

public sealed class MessageCensusService
{
    /// <summary>Message classes the product spec claims to classify.</summary>
    public static readonly IReadOnlyList<MessageKind> ExpectedKinds = new[]
    {
        MessageKind.Text, MessageKind.Image, MessageKind.Voice, MessageKind.Video,
        MessageKind.Emoji, MessageKind.Location, MessageKind.File, MessageKind.Link,
        MessageKind.MiniProgram, MessageKind.ContactCard, MessageKind.Quote,
        MessageKind.System, MessageKind.Transfer, MessageKind.RedPacket,
        MessageKind.Payment, MessageKind.Call
    };

    public MessageCensus Build(
        IReadOnlyList<ConversationItem>? conversations,
        IEnumerable<WeChatMessage>? messages)
    {
        var list = messages?.ToList() ?? new List<WeChatMessage>();
        var conversationList = conversations ?? Array.Empty<ConversationItem>();

        var kinds = list
            .GroupBy(x => x.Kind)
            .Select(g => new MessageKindCount(
                g.Key,
                g.Count(),
                g.Count(x => x.IsOutgoing),
                g.Count(x => !x.IsOutgoing)))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Kind)
            .ToArray();

        var unknownTypes = list
            .Where(x => x.Kind == MessageKind.Unknown)
            .GroupBy(x => x.RawType)
            .Select(g => new UnknownMessageType(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.RawType)
            .ToArray();

        var present = kinds.Where(x => x.Count > 0).Select(x => x.Kind).ToHashSet();

        return new MessageCensus
        {
            ConversationCount = conversationList.Count,
            GroupConversationCount = conversationList.Count(x => x.IsGroup),
            MessageCount = list.Count,
            OutgoingCount = list.Count(x => x.IsOutgoing),
            IncomingCount = list.Count(x => !x.IsOutgoing),
            GroupMessageCount = list.Count(IsGroupMessage),
            GroupSenderResolvedCount = list.Count(x =>
                IsGroupMessage(x) && !x.IsOutgoing && !string.IsNullOrWhiteSpace(x.Sender)),
            SensitiveCount = list.Count(x => x.Sensitive),
            Kinds = kinds,
            UnknownRawTypes = unknownTypes,
            AppMessageTypes = BuildAppMessageTypes(list, unknownOnly: false),
            UnclassifiedAppMessageTypes = BuildAppMessageTypes(list, unknownOnly: true),
            MissingKinds = ExpectedKinds.Where(x => !present.Contains(x)).ToArray()
        };
    }

    private static UnknownMessageType[] BuildAppMessageTypes(
        IReadOnlyList<WeChatMessage> messages,
        bool unknownOnly) =>
        messages
            .Where(x => (x.RawType & 0xffff) == 49 &&
                        (!unknownOnly || x.Kind == MessageKind.Unknown))
            .GroupBy(x => MessageTypeClassifier.ReadAppMessageSubtype(x.Content))
            .Select(g => new UnknownMessageType(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.RawType)
            .ToArray();

    private static bool IsGroupMessage(WeChatMessage message) =>
        message.ConversationId.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase);
}
