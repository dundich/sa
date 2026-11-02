namespace Sa.Outbox.PlugServices;

/// <summary>
/// DI marker written by <c>AddOutboxProvider</c>. Kept separate from the descriptor registration so
/// the duplicate check can inspect the <see cref="IServiceCollection"/> before the container is
/// built, and so the registry can tell "no provider" apart from "provider registered but broken".
/// </summary>
internal sealed record OutboxProviderRegistration(OutboxProviderDescriptor Descriptor);
