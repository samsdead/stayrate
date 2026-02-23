using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using StayRate.Application.Bookings.ConfirmBooking;
using StayRate.Domain;
using StayRate.Domain.Bookings;

namespace StayRate.Api.Controllers;

[ApiController]
[Route("api/v1/bookings")]
public sealed class BookingsController : ControllerBase
{
    public sealed class MoneyRequest
    {
        [Range(typeof(decimal), "0.01", "79228162514264337593543950335")]
        public decimal Amount { get; init; }

        [Required, MinLength(3), MaxLength(3)]
        public string Currency { get; init; } = string.Empty;
    }

    public sealed class QuoteSnapshotLineItemRequest
    {
        [Required, MinLength(1)]
        public string Code { get; init; } = string.Empty;

        [Required, MinLength(1)]
        public string Description { get; init; } = string.Empty;

        [Required]
        public MoneyRequest Amount { get; init; } = new();
    }

    public sealed class ConfirmBookingRequest : IValidatableObject
    {
        [Required, MinLength(1)]
        public string PropertyId { get; init; } = string.Empty;

        public DateOnly CheckIn { get; init; }

        public DateOnly CheckOut { get; init; }

        [Range(1, int.MaxValue)]
        public int Guests { get; init; }

        [Required, MinLength(1)]
        public List<QuoteSnapshotLineItemRequest> LineItems { get; init; } = [];

        [Required]
        public MoneyRequest Total { get; init; } = new();

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (CheckOut <= CheckIn)
            {
                yield return new ValidationResult(
                    "CheckOut must be after CheckIn.",
                    [nameof(CheckOut), nameof(CheckIn)]);
            }
        }
    }

    private readonly ConfirmBookingHandler _handler;

    public BookingsController(ConfirmBookingHandler handler)
    {
        _handler = handler;
    }

    [HttpPost("confirm")]
    public async Task<ActionResult<ConfirmBookingResult>> Confirm(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] ConfirmBookingRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return BadRequest("Idempotency-Key header is required.");

        var snapshot = new QuoteSnapshot(
            request.PropertyId,
            request.CheckIn,
            request.CheckOut,
            request.Guests,
            request.LineItems
                .Select(item => new QuoteSnapshotLineItem(
                    item.Code,
                    item.Description,
                    new Money(item.Amount.Amount, item.Amount.Currency)))
                .ToList(),
            new Money(request.Total.Amount, request.Total.Currency)
        );

        var result = await _handler.HandleAsync(
            new ConfirmBookingCommand(idempotencyKey, snapshot),
            ct);

        return Ok(result);
    }
}
