using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Notification.Service.Models;

namespace Notification.Service.Db;

public interface INotificationRepository
{
    Task<NotificationRecord> CreateAsync(NotificationRecord record);
    Task<bool> ExistsForOrderAndEmailAsync(Guid orderId, string customerEmail);
    Task<IReadOnlyList<NotificationRecord>> GetByOrderIdAsync(Guid orderId);
}
