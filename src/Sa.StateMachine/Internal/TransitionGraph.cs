using Sa.Classes;

namespace Sa.StateMachine.Internal;

public class TransitionGraph<TNode> : ITransitionGraph<TNode>
    where TNode : IComparable<TNode>
{
    private readonly Dictionary<(TNode start, TNode end), object?> _transitions;

    private readonly ResetLazy<TNode[]> _starts;
    private readonly ResetLazy<TNode[]> _ends;

    private readonly ResetLazy<TNode[]> _nodes;
    private readonly ResetLazy<TNode[]> _leaves;

    private readonly ResetLazy<TNode[]> _roots;

    private readonly ResetLazy<Dictionary<TNode, TNode[]>> _nexts;


    public TransitionGraph(IEnumerable<(TNode start, TNode end)> transitions)
    {
        _transitions = new(transitions.Select(t => KeyValuePair.Create((t.start, t.end), default(object))));

        _starts = new(() => _transitions.Keys.Select(t => t.start).Distinct().ToArray());
        _ends = new(() => _transitions.Keys.Select(t => t.end).Distinct().ToArray());
        _nodes = new(() => _starts.Value.Concat(_ends.Value).Distinct().ToArray());
        _leaves = new(() => _ends.Value.Except(_starts.Value).Distinct().ToArray());

        _roots = new(() => _starts.Value
            .Except(_transitions.Keys
                .Where(c => !IsSelfLoop(c))
                .Select(c => c.end))
            .ToArray());

        _nexts = new(() => _transitions.GroupBy(
            t => t.Key.start,
            (start, items) => KeyValuePair.Create(start, items.Select(c => c.Key.end).ToArray()))
            .ToDictionary());
    }

    public IReadOnlyCollection<TNode> this[TNode start] => _nexts.Value.GetValueOrDefault(start) ?? [];

    public IReadOnlyCollection<TNode> Roots => _roots.Value;
    public IReadOnlyCollection<TNode> Ends => _ends.Value;
    public IReadOnlyCollection<TNode> Leaves => _leaves.Value;
    public IReadOnlyCollection<TNode> Nodes => _nodes.Value;
    public IReadOnlyCollection<TNode> Starts => _starts.Value;


    public TransitionGraph<TNode> Add(TNode start, TNode[] ends, object? state = null)
    {
        Reset();
        foreach ((TNode start, TNode end) transit in ends.Select(end => (start, end)))
        {
            _transitions[transit] = state;
        }
        return this;
    }

    protected void Reset()
    {
        _starts.Reset();
        _ends.Reset();
        _roots.Reset();
        _nodes.Reset();
        _leaves.Reset();
        _nexts.Reset();
    }

    public static bool IsSelfLoop((TNode start, TNode end) node) => IsEquals(node.start, node.end);

    public static bool IsEquals(TNode start, TNode end) => start.CompareTo(end) == 0;


    public bool IsRootNode(TNode node) => Roots.Any(c => c.CompareTo(node) == 0);
    public bool IsLeafNode(TNode node) => Leaves.Any(c => c.CompareTo(node) == 0);
}
