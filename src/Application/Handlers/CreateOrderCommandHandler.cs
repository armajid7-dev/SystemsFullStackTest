using Application.Commands;
using Application.DTOs;
using Application.Mapping;
using Application.Interfaces;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Handlers;

public class CreateOrderCommandHandler : IRequestHandler<CreateOrderCommand, OrderDto>
{
    private readonly IOrderRepository _orderRepository;
    private readonly IParentRepository _parentRepository;
    private readonly IRepository<Student> _studentRepository;
    private readonly IRepository<Canteen> _canteenRepository;
    private readonly IRepository<MenuItem> _menuItemRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<CreateOrderCommandHandler> _logger;

    public CreateOrderCommandHandler(
        IOrderRepository orderRepository,
        IParentRepository parentRepository,
        IRepository<Student> studentRepository,
        IRepository<Canteen> canteenRepository,
        IRepository<MenuItem> menuItemRepository,
        IUnitOfWork unitOfWork,
        IDateTimeProvider dateTimeProvider,
        ILogger<CreateOrderCommandHandler> logger)
    {
        _orderRepository = orderRepository;
        _parentRepository = parentRepository;
        _studentRepository = studentRepository;
        _canteenRepository = canteenRepository;
        _menuItemRepository = menuItemRepository;
        _unitOfWork = unitOfWork;
        _dateTimeProvider = dateTimeProvider;
        _logger = logger;
    }

    public async Task<OrderDto> Handle(CreateOrderCommand request, CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["ParentId"] = request.Order.ParentId,
            ["StudentId"] = request.Order.StudentId,
            ["CanteenId"] = request.Order.CanteenId,
            ["IdempotencyKey"] = request.IdempotencyKey
        });

        _logger.LogInformation("Creating order");

        var existingOrder = await TryGetExistingOrderAsync(request.IdempotencyKey, cancellationToken);
        if (existingOrder != null)
            return OrderDtoMapper.Map(existingOrder);

        await _unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            var parent = await GetParentAsync(request.Order.ParentId, cancellationToken);
            var student = await GetStudentAsync(request.Order.StudentId, cancellationToken);
            EnsureStudentBelongsToParent(student, parent.Id);

            var canteen = await GetCanteenAsync(request.Order.CanteenId, cancellationToken);

            var fulfilmentDate = request.Order.FulfilmentDate.Date;
            EnsureCanteenOpenAndWithinCutoff(canteen, fulfilmentDate);

            var (items, totalAmount) = await BuildOrderItemsAsync(
                request.Order.CanteenId,
                student.Allergen,
                request.Order.Items,
                cancellationToken);

            EnsureSufficientWalletBalance(parent, totalAmount);

            var order = CreateOrder(parent, student, canteen, fulfilmentDate, items, totalAmount, request.IdempotencyKey);
            ApplySideEffects(order, parent);

            await _orderRepository.AddAsync(order, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await _unitOfWork.CommitTransactionAsync(cancellationToken);

            _logger.LogInformation("Order created successfully. OrderId: {OrderId} Total: {TotalAmount}", order.Id, totalAmount);

            return OrderDtoMapper.Map(order);
        }
        catch
        {
            await _unitOfWork.RollbackTransactionAsync(cancellationToken);
            throw;
        }
    }

    private async Task<Order?> TryGetExistingOrderAsync(string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return null;

        var existingOrder = await _orderRepository.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
        if (existingOrder == null)
            return null;

        _logger.LogInformation("Idempotent request detected. Returning existing order {OrderId}", existingOrder.Id);
        return existingOrder;
    }

    private async Task<Parent> GetParentAsync(Guid parentId, CancellationToken cancellationToken)
    {
        var parent = await _parentRepository.GetByIdAsync(parentId, cancellationToken);
        if (parent == null)
            throw new OrderValidationException($"Parent with id {parentId} not found");

        return parent;
    }

    private async Task<Student> GetStudentAsync(Guid studentId, CancellationToken cancellationToken)
    {
        var student = await _studentRepository.GetByIdAsync(studentId, cancellationToken);
        if (student == null)
            throw new OrderValidationException($"Student with id {studentId} not found");

        return student;
    }

    private static void EnsureStudentBelongsToParent(Student student, Guid parentId)
    {
        if (student.ParentId != parentId)
            throw new OrderValidationException("Student does not belong to the specified parent");
    }

    private async Task<Canteen> GetCanteenAsync(Guid canteenId, CancellationToken cancellationToken)
    {
        var canteen = await _canteenRepository.GetByIdAsync(canteenId, cancellationToken);
        if (canteen == null)
            throw new OrderValidationException($"Canteen with id {canteenId} not found");

        return canteen;
    }

    private void EnsureCanteenOpenAndWithinCutoff(Canteen canteen, DateTime fulfilmentDate)
    {
        var dayOfWeek = fulfilmentDate.DayOfWeek;

        if (!canteen.IsOpenOnDay(dayOfWeek))
            throw new OrderValidationException($"Canteen is not open on {dayOfWeek}");

        var cutoffTime = canteen.GetCutoffTimeForDay(dayOfWeek);
        if (cutoffTime is null)
            return;

        var currentTime = _dateTimeProvider.Now.TimeOfDay;
        if (currentTime > cutoffTime.Value)
            throw new OrderValidationException($"Order cutoff time ({cutoffTime.Value}) has passed");
    }

    private async Task<(List<OrderItem> items, decimal totalAmount)> BuildOrderItemsAsync(
        Guid canteenId,
        string? studentAllergen,
        IReadOnlyCollection<OrderItemDto> requestedItems,
        CancellationToken cancellationToken)
    {
        var orderItems = new List<OrderItem>(requestedItems.Count);
        decimal totalAmount = 0m;

        foreach (var itemDto in requestedItems)
        {
            var menuItem = await _menuItemRepository.GetByIdAsync(itemDto.MenuItemId, cancellationToken);
            if (menuItem == null)
                throw new OrderValidationException($"MenuItem with id {itemDto.MenuItemId} not found");

            if (menuItem.CanteenId != canteenId)
                throw new OrderValidationException($"MenuItem {menuItem.Id} does not belong to the specified canteen");

            if (!menuItem.IsInStock(itemDto.Quantity))
                throw new OrderValidationException($"Insufficient stock for menu item {menuItem.Name}");

            if (!string.IsNullOrEmpty(studentAllergen) && menuItem.HasAllergen(studentAllergen))
                throw new OrderValidationException(
                    $"Menu item {menuItem.Name} contains allergen {studentAllergen} which the student is allergic to");

            var itemTotal = menuItem.Price * itemDto.Quantity;
            _logger.LogInformation(
                "Adding menu item {MenuItemId} qty {Quantity} price {Price} total {ItemTotal}",
                menuItem.Id,
                itemDto.Quantity,
                menuItem.Price,
                itemTotal);

            totalAmount += itemTotal;

            orderItems.Add(new OrderItem
            {
                Id = Guid.NewGuid(),
                MenuItemId = menuItem.Id,
                MenuItem = menuItem,
                Quantity = itemDto.Quantity,
                UnitPrice = menuItem.Price
            });
        }

        return (orderItems, totalAmount);
    }

    private static void EnsureSufficientWalletBalance(Parent parent, decimal totalAmount)
    {
        if (parent.WalletBalance < totalAmount)
            throw new OrderValidationException(
                $"Insufficient wallet balance. Required: {totalAmount}, Available: {parent.WalletBalance}");
    }

    private Order CreateOrder(
        Parent parent,
        Student student,
        Canteen canteen,
        DateTime fulfilmentDate,
        List<OrderItem> items,
        decimal totalAmount,
        string? idempotencyKey)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            ParentId = parent.Id,
            Parent = parent,
            StudentId = student.Id,
            Student = student,
            CanteenId = canteen.Id,
            Canteen = canteen,
            FulfilmentDate = fulfilmentDate,
            CreatedAt = _dateTimeProvider.UtcNow,
            Status = OrderStatus.Placed,
            TotalAmount = totalAmount,
            IdempotencyKey = idempotencyKey,
            Items = items
        };

        foreach (var item in items)
        {
            item.OrderId = order.Id;
            item.Order = order;
        }

        return order;
    }

    private static void ApplySideEffects(Order order, Parent parent)
    {
        foreach (var item in order.Items)
            item.MenuItem.DecrementStock(item.Quantity);

        parent.DebitWallet(order.TotalAmount);
        order.Confirm();
    }
}

