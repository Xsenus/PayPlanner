namespace PayPlanner.Api.Services.Storage;

public sealed class InternalChatSendRecovery(InternalChatOriginalWriter writer, IStoragePolicyService policies,
    IProductStorageBindingService bindings, ILogger<InternalChatSendRecovery> logger)
{
    public async Task<InternalChatSendRecoveryBatchResult> ProcessBatchAsync(CancellationToken ct)
    {
        var result = await writer.ProcessRecoveryBatchAsync(policies, bindings, ct);
        if (result.Claimed > 0)
            logger.LogInformation("Chat recovery processed {Claimed} attempts: {Published} published, {Deferred} deferred, {DeadLettered} dead-letter",
                result.Claimed, result.Published, result.Deferred, result.DeadLettered);
        return result;
    }
}

public static class InternalChatStorageRecoveryRegistration
{
    public static IServiceCollection AddInternalChatStorageRecovery(this IServiceCollection services)
    {
        services.AddScoped<InternalChatOriginalWriter>();
        services.AddScoped<InternalChatSendRecovery>();
        return services;
    }
}
