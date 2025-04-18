namespace Sa.Infrastructure;


public interface IHasId
{
    object Id { get; }
}

public interface IHasId<out T>
{
    T Id { get; }
}
