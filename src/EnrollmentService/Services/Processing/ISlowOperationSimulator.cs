namespace EnrollmentService.Services.Processing;

public interface ISlowOperationSimulator
{
    Task SimulateAsync(bool isAsyncModel, CancellationToken cancellationToken = default);
}

public sealed class SlowOperationSimulator : ISlowOperationSimulator
{
    private readonly ServiceOptions _options;

    public SlowOperationSimulator(ServiceOptions options)
    {
        _options = options;
    }

    public async Task SimulateAsync(bool isAsyncModel, CancellationToken cancellationToken = default)
    {
        int delayMs = Random.Shared.Next(_options.SlowCallMinMs, _options.SlowCallMaxMs + 1);
        if (isAsyncModel)
        {
            await Task.Delay(delayMs, cancellationToken);
        }
        else
        {
            Thread.Sleep(delayMs);
        }
    }
}
