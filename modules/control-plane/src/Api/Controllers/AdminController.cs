using Api.Application.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[Route("api/admin")]
[Authorize(AuthenticationSchemes = "ApiKey")]
public class AdminController: ApiControllerBase
{
    [HttpPost("push-eval-full-sync")]
    public async Task<ApiResponse<bool>> PushFullSyncToAllActiveClients()
    {
        var request = new PushFullSync();

        var response = await Mediator.Send(request);
        return Ok(response);
    }
}