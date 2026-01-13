using Application.Admin;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Api.Controllers;

[Route("api/v{version:apiVersion}/admin")]
public class AdminController : ApiControllerBase
{
    [HttpPost("push-eval-full-sync")]
    public async Task<ApiResponse<bool>> PushFullSyncToAllActiveClients()
    {
        var request = new PushFullSync();

        var accessTokens = await Mediator.Send(request);
        return Ok(true);
    }
}