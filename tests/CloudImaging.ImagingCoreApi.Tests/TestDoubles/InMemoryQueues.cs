using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using Moq;

namespace CloudImaging.ImagingCoreApi.Tests.TestDoubles;

/// <summary>A message the in-memory queue received.</summary>
internal sealed record SentQueueMessage(string QueueName, string Body, TimeSpan? Delay, TimeSpan? TimeToLive)
{
    public DriverWorkMessage Work => DriverWorkSender.Parse(Body);
}

/// <summary>In-memory <see cref="QueueServiceClient"/>: records sent messages and created queues.</summary>
internal sealed class InMemoryQueues
{
    private readonly Dictionary<string, Queue> _queues = new(StringComparer.Ordinal);

    public InMemoryQueues()
    {
        var service = new Mock<QueueServiceClient>();
        service.Setup(s => s.GetQueueClient(It.IsAny<string>())).Returns((string name) => Get(name));
        Service = service.Object;
    }

    public QueueServiceClient Service { get; }

    public List<SentQueueMessage> Sent { get; } = [];

    public HashSet<string> Created { get; } = new(StringComparer.Ordinal);

    /// <summary>Removes and returns every message sent so far.</summary>
    public List<SentQueueMessage> Drain()
    {
        var all = Sent.ToList();
        Sent.Clear();
        return all;
    }

    private Queue Get(string name)
    {
        if (!_queues.TryGetValue(name, out var queue))
        {
            queue = new Queue(this, name);
            _queues[name] = queue;
        }

        return queue;
    }

    private sealed class Queue(InMemoryQueues owner, string name) : QueueClient
    {
        public override string Name => name;

        public override Task<Response> CreateIfNotExistsAsync(IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        {
            owner.Created.Add(name);
            return Task.FromResult<Response>(new FakeResponse(201));
        }

        public override Task<Response<SendReceipt>> SendMessageAsync(string messageText, TimeSpan? visibilityTimeout = null, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
        {
            owner.Sent.Add(new SentQueueMessage(name, messageText, visibilityTimeout, timeToLive));
            var receipt = QueuesModelFactory.SendReceipt(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7), "pop", DateTimeOffset.UtcNow);
            return Task.FromResult(Response.FromValue(receipt, new FakeResponse(201)));
        }
    }
}
