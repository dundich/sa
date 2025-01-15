namespace Sa.StateMachine;

public interface ITransitionGraph<TNode> where TNode : IComparable<TNode>
{
    IReadOnlyCollection<TNode> this[TNode start] { get; }

    IReadOnlyCollection<TNode> Starts { get; }
    IReadOnlyCollection<TNode> Ends { get; }

    IReadOnlyCollection<TNode> Roots { get; }
    IReadOnlyCollection<TNode> Leaves { get; }

    IReadOnlyCollection<TNode> Nodes { get; }

    bool IsRootNode(TNode node);
    bool IsLeafNode(TNode node);
}
