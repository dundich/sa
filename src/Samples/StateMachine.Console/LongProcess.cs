using Sa.StateMachine;

namespace StateMachine.Console;

class LongProcess : SmLongProcess
{
    protected override ISmProcessor<State> CreateProcessor()
    {
        return new MyProcessor();
    }

    class MyProcessor : Processor
    {
        public async override ValueTask<State> MoveNext(ISmContext<State> context)
        {
            // some work
            await Task.Delay(1000, context.CancellationToken);
            System.Console.WriteLine($"process #{context}");
            return await base.MoveNext(context);
        }
    }
}
