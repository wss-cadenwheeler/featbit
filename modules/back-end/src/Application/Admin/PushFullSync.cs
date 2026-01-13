using Domain.Messages;

namespace Application.Admin;

public class PushFullSync : IRequest<bool>;

public class PushFullSyncHandler(IMessageProducer messageProducer) : IRequestHandler<PushFullSync, bool>
{
    private static readonly object EmptyMessage = new { };

    public async Task<bool> Handle(PushFullSync request, CancellationToken cancellationToken)
    {
        try
        {
            await messageProducer.PublishAsync(Topics.PushFullSyncChange, EmptyMessage);
            return true;
        }
        catch (Exception e)
        {
            return false;
        }
    }
}