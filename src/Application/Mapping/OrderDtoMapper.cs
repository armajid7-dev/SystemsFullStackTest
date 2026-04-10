using Application.DTOs;
using Domain.Entities;

namespace Application.Mapping;

public static class OrderDtoMapper
{
    public static OrderDto Map(Order order)
    {
        return new OrderDto
        {
            Id = order.Id,
            ParentId = order.ParentId,
            StudentId = order.StudentId,
            CanteenId = order.CanteenId,
            FulfilmentDate = order.FulfilmentDate,
            CreatedAt = order.CreatedAt,
            Status = order.Status.ToString(),
            TotalAmount = order.TotalAmount,
            Items = order.Items.Select(MapItem).ToList()
        };
    }

    private static OrderItemDetailDto MapItem(OrderItem item)
    {
        return new OrderItemDetailDto
        {
            MenuItemId = item.MenuItemId,
            MenuItemName = item.MenuItem.Name,
            Quantity = item.Quantity,
            UnitPrice = item.UnitPrice
        };
    }
}

