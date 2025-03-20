namespace Sa.Host.MessageTypeResolver;

public interface IMessageTypeResolver
{
    Type? ToType(string name);
    string ToName(Type messageType);
    string ToName<T>() => ToName(typeof(T));
}
