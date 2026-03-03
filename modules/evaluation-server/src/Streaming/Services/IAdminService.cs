namespace Streaming.Services;

public interface IAdminService
{
    Task PushFullSyncToAllActiveClients();
}