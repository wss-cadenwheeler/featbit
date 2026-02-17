using Api.Application.Admin;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[Route("api/admin")]
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