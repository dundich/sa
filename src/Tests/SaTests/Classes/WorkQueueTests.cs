using Sa.Classes;

namespace SaTests.Classes;

public class WorkQueueTests
{
    public class SampleModel
    {
        private int _count;

        public int Count => _count;

        public Exception? Exception { get; set; }

        public void IncCount() => _count++;
    }


    internal class SampleWork : IWork<SampleModel>
    {
        public async Task Execute(SampleModel model, CancellationToken cancellationToken)
        {
            model.IncCount();
            await Task.Delay(100, cancellationToken);
        }
    }


    internal class SampleFailWork : IWorkWithHandleError<SampleModel>
    {
        public Task Execute(SampleModel model, CancellationToken cancellationToken)
        {
            throw new Exception("test error");
        }

        public Task HandelError(Exception exception, SampleModel model, CancellationToken cancellationToken)
        {
            model.Exception = exception;
            return Task.CompletedTask;
        }
    }


    [Fact]
    public async Task Schedule_ExecutesWorkSuccessfully()
    {
        var workService = new WorkQueue<SampleModel, SampleWork>();
        var model = new SampleModel { };
        var work = new SampleWork { };

        // Act
        workService.Enqueue(model, work, TestContext.Current.CancellationToken);

        // Wait for a short time to allow the work to be processed
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Stop the work service
        await workService.Stop(model);

        // Assert
        Assert.Equal(1, model.Count);

        await workService.DisposeAsync();
    }

    [Fact]
    public async Task CancelledWork_MustBeStopped()
    {
        // Arrange
        var workService = new WorkQueue<SampleModel, SampleWork>();
        var model = new SampleModel { };
        var work = new SampleWork { };
        using var cancellationTokenSource = new CancellationTokenSource();

        // Act
        workService.Enqueue(model, work, cancellationTokenSource.Token);

        // Cancel the token before the work is completed
        await cancellationTokenSource.CancelAsync();

        // Wait for a short time to allow the work to be processed
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Stop the work service
        await workService.Stop(model);
        await workService.Stop(model);

        // Assert
        Assert.Equal(0, model.Count);

        await workService.DisposeAsync();
    }


    [Fact]
    public async Task MultiWork_ExecutesWorkSuccessfully()
    {
        // Arrange
        var workService = new WorkQueue<SampleModel, SampleWork>();

        var model = new SampleModel { };

        int excepted = 100;

        for (int i = 0; i < excepted; i++)
        {
            var work = new SampleWork { };

            // Act
            workService.Enqueue(model, work, TestContext.Current.CancellationToken);
        }

        // Wait for a short time to allow the work to be processed
        await workService.DisposeAsync();

        // Assert
        Assert.Equal(excepted, model.Count);

    }


    [Fact]
    public async Task WaitingEndedWork_AfterDispose()
    {
        // Arrange
        var workService = new WorkQueue<SampleModel, SampleWork>();
        var model = new SampleModel { };
        var work = new SampleWork { };
        using var cancellationTokenSource = new CancellationTokenSource();

        // Act
        workService.Enqueue(model, work, cancellationTokenSource.Token);
        await workService.DisposeAsync();

        // Assert
        Assert.Equal(1, model.Count);
    }


    [Fact]
    public async Task FailWork_MustBeHandled()
    {
        // Arrange
        var workService = new WorkQueue<SampleModel, SampleFailWork>();
        var model = new SampleModel { };
        var work = new SampleFailWork { };

        // Act
        workService.Enqueue(model, work, TestContext.Current.CancellationToken);

        await workService.DisposeAsync();

        // Assert
        Assert.NotNull(model.Exception);
    }
}