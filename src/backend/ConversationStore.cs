namespace Helpdesk.Backend;

public sealed class ConversationStore
{
    private readonly object sync = new();
    private readonly Dictionary<string, Conversation> conversations = [];
    private readonly TimeProvider clock;
    public ConversationStore(TimeProvider clock) => this.clock = clock;
    public Conversation Get(string owner, string? id)
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            foreach (var expired in conversations.Where(p => now - p.Value.LastUsed > TimeSpan.FromMinutes(30)).Select(p => p.Key).ToArray())
                conversations.Remove(expired);
            if (id is not null)
            {
                if (!conversations.TryGetValue(id, out var existing) || existing.Owner != owner) throw new ConversationNotFoundException();
                existing.LastUsed = now;
                return existing;
            }
            if (conversations.Count >= 500 || conversations.Values.Count(c => c.Owner == owner) >= 5)
                throw new ConversationLimitException();
            var created = new Conversation(Guid.NewGuid().ToString("D"), owner, now);
            conversations.Add(created.Id, created);
            return created;
        }
    }
}
public sealed class Conversation(string id, string owner, DateTimeOffset lastUsed)
{
    public string Id { get; } = id;
    public string Owner { get; } = owner;
    public DateTimeOffset LastUsed { get; set; } = lastUsed;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public List<(string Question, string Answer)> History { get; } = [];
}
public sealed class ConversationNotFoundException : Exception;
public sealed class ConversationLimitException : Exception;
