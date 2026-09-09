using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowRecall.Core;

/// <summary>Reads and writes one locally persisted undo receipt. Raw window titles are always stripped
/// from the stored copy because undo needs only window identity and geometry, never title content.</summary>
public static class UndoReceiptJsonSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(UndoReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return JsonSerializer.Serialize(ScrubTitles(receipt), Options) + Environment.NewLine;
    }

    public static UndoReceipt Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ProfileFormatException("Undo receipt JSON is required.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ProfileFormatException("Undo receipt JSON root must be an object.");
            UndoReceipt receipt = JsonSerializer.Deserialize<UndoReceipt>(json, Options)
                ?? throw new ProfileFormatException("Undo receipt JSON contained no receipt.");
            if (receipt.ReceiptId == Guid.Empty || receipt.PlanId == Guid.Empty)
                throw new ProfileFormatException("Undo receipt identifiers are required.");
            if (receipt.BeforeRestore is null || receipt.BeforeRestore.Displays.IsDefaultOrEmpty
                || receipt.BeforeRestore.Windows.IsDefault || receipt.Outcomes.IsDefault)
                throw new ProfileFormatException("Undo receipt must retain a pre-apply snapshot and per-window outcomes.");
            return receipt;
        }
        catch (ProfileFormatException) { throw; }
        catch (JsonException exception) { throw new ProfileFormatException("Undo receipt JSON is malformed.", exception); }
        catch (NotSupportedException exception) { throw new ProfileFormatException("Undo receipt JSON contains unsupported data.", exception); }
    }

    private static UndoReceipt ScrubTitles(UndoReceipt receipt) =>
        receipt with
        {
            BeforeRestore = receipt.BeforeRestore with
            {
                Windows = receipt.BeforeRestore.Windows.Select(window => window with { Title = null }).ToImmutableArray(),
            },
        };
}
