using System.Text.Json;
using Azure.Storage.Queues;
using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>Kinds of background work (data-model.md section 3.12).</summary>
public enum DriverWorkKind
{
    /// <summary>Starts a scheduled run when its slot arrives.</summary>
    ScheduledSync,

    /// <summary>Reads one vendor's catalogs for a run.</summary>
    FetchVendorCatalog,

    /// <summary>Re-resolves one model and release from its stored catalog data.</summary>
    ResolveModel,

    /// <summary>Downloads and verifies one vendor file.</summary>
    DownloadFile,

    /// <summary>Builds the driver set of a dirty model and release.</summary>
    BuildSet,

    /// <summary>Finishes a run: result, clean-up, lock release.</summary>
    FinalizeRun,
}

/// <summary>One message on the <c>driver-work</c> queue.</summary>
public sealed record DriverWorkMessage
{
    /// <summary>What to do.</summary>
    public DriverWorkKind Kind { get; init; }

    /// <summary>Slot of a scheduled sync, UTC.</summary>
    public DateTimeOffset? SlotUtc { get; init; }

    /// <summary>Run the work belongs to.</summary>
    public Guid? RunId { get; init; }

    /// <summary>Vendor of a catalog fetch or download.</summary>
    public DriverVendor? Vendor { get; init; }

    /// <summary>Model of a model check, resolve or build.</summary>
    public Guid? ModelId { get; init; }

    /// <summary>Release of a resolve or build.</summary>
    public WindowsRelease? Release { get; init; }

    /// <summary>File of a download (<c>&lt;componentId&gt;_&lt;version&gt;</c>).</summary>
    public string? FileKey { get; init; }

    /// <summary>Set build that waits for a download.</summary>
    public string? BuildId { get; init; }

    /// <summary>
    /// The message is not due before this time. Set when the wanted delay exceeds the queue's
    /// 7-day visibility limit; the handler re-enqueues it for the rest.
    /// </summary>
    public DateTimeOffset? NotBefore { get; init; }
}

/// <summary>Sends messages to the driver work queue.</summary>
public interface IDriverWorkSender
{
    /// <summary>Enqueues <paramref name="message"/>, invisible for <paramref name="delay"/>.</summary>
    Task EnqueueAsync(DriverWorkMessage message, TimeSpan delay, CancellationToken ct = default);
}

/// <summary>
/// The <c>driver-work</c> queue on Core's own storage account (research R1). Messages are JSON,
/// Base64-encoded for the queue trigger. Delays beyond the queue's 7-day limit are capped and
/// carry <see cref="DriverWorkMessage.NotBefore"/>.
/// </summary>
public sealed class DriverWorkSender(QueueServiceClient queues, TimeProvider time) : IDriverWorkSender
{
    /// <summary>The work queue.</summary>
    public const string QueueName = "driver-work";

    /// <summary>Where the host moves a message after its last failed attempt.</summary>
    public const string PoisonQueueName = "driver-work-poison";

    /// <summary>Longest visibility delay the queue accepts.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromDays(7);

    // Infinite time-to-live: a message must outlive its own visibility delay.
    private static readonly TimeSpan NeverExpires = TimeSpan.FromSeconds(-1);

    /// <summary>Creates both queues when missing.</summary>
    public async Task EnsureQueuesExistAsync(CancellationToken ct = default)
    {
        await queues.GetQueueClient(QueueName).CreateIfNotExistsAsync(cancellationToken: ct);
        await queues.GetQueueClient(PoisonQueueName).CreateIfNotExistsAsync(cancellationToken: ct);
    }

    /// <inheritdoc/>
    public async Task EnqueueAsync(DriverWorkMessage message, TimeSpan delay, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        if (delay > MaxDelay)
        {
            message = message with { NotBefore = time.GetUtcNow() + delay };
            delay = MaxDelay;
        }

        var json = JsonSerializer.Serialize(message, DriverJson.Options);
        await queues.GetQueueClient(QueueName).SendMessageAsync(json, delay == TimeSpan.Zero ? null : delay, NeverExpires, ct);
    }

    /// <summary>Reads a message body.</summary>
    public static DriverWorkMessage Parse(string body) =>
        JsonSerializer.Deserialize<DriverWorkMessage>(body, DriverJson.Options)
        ?? throw new InvalidDataException("A driver work message is empty.");
}
