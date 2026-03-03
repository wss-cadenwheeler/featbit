namespace Application.Segments.MessagePublishing.SegmentChange;

public interface ISegmentChangePublisher
{
    Task PublishAsync(OnSegmentChange notification);
}