using System.Collections.Concurrent;
using LakeWright.Caching.Redis;
using LakeWright.Conversations;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class RedisConversationOwnershipTests
{
    [Fact]
    public async Task Atomically_claims_a_conversation_across_replicas_and_keeps_identifiers_out_of_keys()
    {
        var redis = new InMemoryRedis();
        var firstReplica = new RedisConversationOwnership(redis);
        var secondReplica = new RedisConversationOwnership(redis);

        await firstReplica.RecordAsync("conversation-sensitive", "owner-a-sensitive", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ConversationOwnershipException>(async () => await secondReplica.RecordAsync(
            "conversation-sensitive", "owner-b-sensitive", TestContext.Current.CancellationToken));

        (await secondReplica.IsOwnerAsync("conversation-sensitive", "owner-a-sensitive", TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await secondReplica.IsOwnerAsync("conversation-sensitive", "owner-b-sensitive", TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await secondReplica.ListAsync("owner-a-sensitive", TestContext.Current.CancellationToken)).ShouldBe(["conversation-sensitive"]);
        redis.Keys.ShouldAllBe(key => !key.Contains("sensitive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deletes_only_the_callers_immutable_ownership_claim()
    {
        var redis = new InMemoryRedis();
        var owner = new RedisConversationOwnership(redis);
        await owner.RecordAsync("conversation-1", "owner-a", TestContext.Current.CancellationToken);

        await owner.RemoveAsync("conversation-1", "owner-b", TestContext.Current.CancellationToken);
        (await owner.IsOwnerAsync("conversation-1", "owner-a", TestContext.Current.CancellationToken)).ShouldBeTrue();

        await owner.RemoveAsync("conversation-1", "owner-a", TestContext.Current.CancellationToken);
        (await owner.IsOwnerAsync("conversation-1", "owner-a", TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await owner.ListAsync("owner-a", TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    private sealed class InMemoryRedis : IRedisConversationOwnershipStore
    {
        private readonly ConcurrentDictionary<string, string> _strings = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sets = new(StringComparer.Ordinal);

        public Task<bool> ClaimAsync(string ownershipKey, string ownerHash) =>
            Task.FromResult(_strings.TryAdd(ownershipKey, ownerHash));

        public Task<string?> GetOwnerAsync(string ownershipKey) =>
            Task.FromResult(_strings.TryGetValue(ownershipKey, out var owner) ? owner : null);

        public Task AddToOwnerAsync(string ownerSetKey, string conversationId)
        {
            var set = _sets.GetOrAdd(ownerSetKey, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            _ = set.TryAdd(conversationId, 0);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListAsync(string ownerSetKey) =>
            Task.FromResult<IReadOnlyList<string>>(_sets.TryGetValue(ownerSetKey, out var set) ? [.. set.Keys] : []);

        public Task DeleteClaimAsync(string ownershipKey)
        {
            _ = _strings.TryRemove(ownershipKey, out _);
            return Task.CompletedTask;
        }

        public Task RemoveFromOwnerAsync(string ownerSetKey, string conversationId)
        {
            if (_sets.TryGetValue(ownerSetKey, out var set))
            {
                _ = set.TryRemove(conversationId, out _);
            }
            return Task.CompletedTask;
        }

        public IReadOnlyCollection<string> Keys => [.. _strings.Keys, .. _sets.Keys];

    }
}
