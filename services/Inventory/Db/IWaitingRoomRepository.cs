using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Inventory.Service.Models;

namespace Inventory.Service.Db;

public interface IWaitingRoomRepository
{
    Task<WaitingRoomEntry> JoinQueueAsync(Guid showId, string customerSub, DateTimeOffset now);
    Task<WaitingRoomEntry?> GetStatusAsync(Guid showId, string customerSub, DateTimeOffset now);
    Task<int> GetTotalWaitingAsync(Guid showId);
    Task<List<WaitingRoomEntry>> AdmitNextCustomersAsync(Guid showId, int batchSize, int tokenDurationMinutes, DateTimeOffset now);
    Task<bool> ValidateAdmissionTokenAsync(Guid showId, string customerSub, string admissionToken, DateTimeOffset now);
}
