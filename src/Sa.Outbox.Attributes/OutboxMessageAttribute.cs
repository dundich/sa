namespace Sa.Outbox.Support;


// <summary>
/// An attribute used to mark classes or structs as Outbox messages.
/// This attribute can be used to specify the part associated with the Outbox message.
/// </summary>
/// <param name="part">The part identifier for the Outbox message. Default is "root".</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class OutboxMessageAttribute(string part = "root") : Attribute
{
    /// <summary>
    /// Gets the part identifier associated with the Outbox message.
    /// </summary>
    public string Part => part;

    /// <summary>
    /// A default instance of the <see cref="OutboxMessageAttribute"/> with the default part value.
    /// </summary>
    public readonly static OutboxMessageAttribute Default = new();
}