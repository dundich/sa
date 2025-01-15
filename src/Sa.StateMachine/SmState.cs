using Sa.Classes;
using Sa.StateMachine.Internal;

namespace Sa.StateMachine;


public abstract record SmState<TState> : Enumeration<TState>
    where TState : SmState<TState>
{

    public static readonly TState Start = CreateStart();
    public static readonly TState Error = CreateError();
    public static readonly TState Finish = CreateFinish();

    protected SmState(int id, string name, StateKind kind = StateKind.Default)
        : base(id, name)
    {
        Kind = kind;
    }

    public StateKind Kind { get; } = StateKind.Default;

    public static ISmSettings<TState> BuildSettings(Action<ISettingsBuilder<TState>> configure)
    {
        SettingsBulder<TState> sb = new();
        configure.Invoke(sb);
        return sb.Build();
    }

    public static TState Create(int id, string name)
    {
        id = id < 100 ? throw new ArgumentException("id must be greater than `99`") : id;
        name = name ?? throw new ArgumentNullException(nameof(name));
        return (TState)Activator.CreateInstance(typeof(TState), id, name, StateKind.Default)!;
    }

    private static TState CreateStart()
        => (TState)Activator.CreateInstance(typeof(TState), SmStateId.Start, nameof(StateKind.Start), StateKind.Start)!;

    private static TState CreateFinish()
        => (TState)Activator.CreateInstance(typeof(TState), SmStateId.Finish, nameof(StateKind.Finish), StateKind.Finish)!;

    private static TState CreateError()
        => (TState)Activator.CreateInstance(typeof(TState), SmStateId.Error, nameof(StateKind.Error), StateKind.Error)!;
}
