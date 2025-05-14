namespace Sa.Classes;


public interface IHasId
{
    object Id { get; }
}

public interface IHasId<out T>
{
    T Id { get; }
}
