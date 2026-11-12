namespace EnrollmentService.Queues;

// ============================================================================
// Уніфікований інтерфейс обмеженої черги (О1.2, Д1.3 РПО)
// ============================================================================
public interface IBoundedRequestQueue
{
    int Capacity { get; }
    int Count { get; }
    string EngineName { get; }

    // Неблокуюча спроба додати елемент. Повертає false, якщо черга заповнена (для відмови 429 у О1.2)
    bool TryEnqueue(QueuedRequestItem item);

    // О1.2: Швидке резервування слота в черзі ДО звернення до бази даних (швидкий 429 Backpressure)
    bool TryReserveSlot();
    void ReleaseSlot();
    bool EnqueueReserved(QueuedRequestItem item);

    // Неблокуюче асинхронне отримання елемента (для моделі async_tasks у Д1.1)
    ValueTask<QueuedRequestItem?> DequeueAsync(CancellationToken cancellationToken);

    // Синхронне блокуюче отримання елемента на Monitor.Wait (для моделі dedicated_threads у Д1.1 та Д1.3)
    QueuedRequestItem? DequeueBlocking(CancellationToken cancellationToken);

    // Сигнал про припинення приймання нових елементів (для коректного завершення Д1.4)
    void Complete();

    // Очищення черги для скидання стану стенду між дослідами
    void Clear();
}
