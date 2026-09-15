using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Inventory.Service.Models;

namespace Inventory.Service.Services;

public interface IWaitingRoomService
{
    Task<QueueStatusResponse> JoinQueueAsync(Guid showId, string customerSub);
    Task<QueueStatusResponse?> GetQueueStatusAsync(Guid showId, string customerSub);
    Task<AdmitCustomersResponse> AdmitNextCustomersAsync(Guid showId, int batchSize = 10, int tokenDurationMinutes = 10);
    Task<bool> ValidateAdmissionTokenAsync(Guid showId, string customerSub, string admissionToken);
}
