using Microsoft.Extensions.DependencyInjection;

namespace Sa.Utils.WorkQueue.Tests;



public sealed record TestInput(string Id, int Value);


public sealed class TestWorkProcessor : ISaWork<TestInput>
{
    public int ExecuteCount { get; private set; }
    public TestInput? LastInput { get; private set; }

    public Task Execute(TestInput input, CancellationToken cancellationToken)
    {
        ExecuteCount++;
        LastInput = input;
        return Task.CompletedTask;
    }
}


public sealed class WorkQueueTestFixture : IDisposable
{
    public ServiceProvider ServiceProvider { get; }

    public TestWorkProcessor Processor => ServiceProvider.GetRequiredService<TestWorkProcessor>();

    public WorkQueueTestFixture()
    {
        var services = new ServiceCollection();
        services.AddSaWorkQueue<TestWorkProcessor, TestInput>();
        ServiceProvider = services.BuildServiceProvider();
    }

    public void Dispose() => ServiceProvider.Dispose();
}


public class WorkQueueDiTests(WorkQueueTestFixture fixture) : IClassFixture<WorkQueueTestFixture>
{


    [Fact]
    public async Task WorkQueue_With_Di()
    {
        var queue = fixture.ServiceProvider.GetRequiredService<ISaWorkQueue<TestInput>>();
        Assert.NotNull(queue);


        var input = new TestInput("test-1", 42);
        await queue.Enqueue(input, TestContext.Current.CancellationToken);
        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);

        // Assert

        var processor = fixture.Processor;

        Assert.Equal(1, processor.ExecuteCount);
        Assert.Equal(input, processor.LastInput);
    }
}
