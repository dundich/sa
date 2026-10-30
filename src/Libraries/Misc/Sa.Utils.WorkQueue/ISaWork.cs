namespace Sa.Utils.WorkQueue;


/// <summary>
/// BLL interface.
/// </summary>
public interface ISaWork<in TInput>
{
    Task Execute(TInput input, CancellationToken cancellationToken);
}
