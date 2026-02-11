using Application.Segments;
using Domain.Segments;

namespace Application.Services;

public interface ISegmentMessageService
{
    ValueTask<ICollection<FlagReference>> GetAffectedFlagsAsync(Guid envId, OnSegmentChange notification);

    Task PublishSegmentChangeMessage(Guid envId, ICollection<FlagReference> affectedFlags, Segment segment);
}