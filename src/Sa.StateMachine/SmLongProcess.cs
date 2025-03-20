namespace Sa.StateMachine;


public abstract class SmLongProcess : SmEnumerable<SmLongProcess.State>
{
    public record State : SmState<State>
    {
        public static readonly State WaitingToRun = Create(SmStateId.WaitingToRun, nameof(WaitingToRun));
        public static readonly State Running = Create(SmStateId.Running, nameof(Running));
        public static readonly State Succeed = Create(SmStateId.Succeed, nameof(Succeed));


        public static readonly ISmSettings<State> Settings = BuildSettings(builder => builder
                .Add(Start, [WaitingToRun])
                .Add(WaitingToRun, [WaitingToRun, Running])
                .Add(Running, [Succeed, Error])
                .Add(Succeed, [Finish])
                .Add(Error, [Finish])
        );

        public State(int id, string name, StateKind state)
            : base(id, name, state)
        {
        }
    }

    protected SmLongProcess() : base(State.Settings)
    {
    }

    protected override ISmProcessor<State> CreateProcessor() => new Processor();

    public class Processor : ISmProcessor<State>
    {
        public ValueTask Finished(ISmContext<State> context)
        {
            return ValueTask.CompletedTask;
        }

        public virtual ValueTask<State> MoveNext(ISmContext<State> context)
        {
            return context.CurrentState.Id switch
            {
                SmStateId.Start => ValueTask.FromResult(State.WaitingToRun),
                SmStateId.WaitingToRun => ValueTask.FromResult(State.Running),
                SmStateId.Running => ValueTask.FromResult(State.Succeed),
                SmStateId.Succeed => ValueTask.FromResult(State.Finish),
                _ => throw new NotImplementedException(),
            };
        }
    }
}
