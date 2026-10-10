using Inventory.Service.Db;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Service.Controllers;

[ApiController, Authorize(Policy = "CancellationInternal")]
[Route("internal/inventory/cancellations")]
public class CancellationController(CancellationRepository repository) : ControllerBase
{
    [HttpPut("holds/{id:guid}"), ProducesResponseType(204), ProducesResponseType(404)]
    public async Task<IActionResult> Return(Guid id) => await repository.ReturnAsync(id) ? NoContent() : NotFound();

    [HttpPut("shows/{id:guid}"), ProducesResponseType(204)]
    public async Task<IActionResult> Close(Guid id)
    {
        await repository.CloseAsync(id);
        return NoContent();
    }
}
