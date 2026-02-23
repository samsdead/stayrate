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
    public sealed record MoneyRequest(
        [property: Range(typeof(decimal), "0.01", "79228162514264337593543950335")]
        decimal Amount,
        [property: Required, MinLength(3), MaxLength(3)]
        string Currency
    );

    public sealed record QuoteSnapshotLineItemRequest(
        [property: Required, MinLength(1)]
        string Code,
        [property: Required, MinLength(1)]
        string Description,
        [property: Required]
        MoneyRequest Amount
    );

    public sealed record ConfirmBookingRequest(
        [property: Required, MinLength(1)]
        string PropertyId,
        DateOnly CheckIn,
        DateOnly CheckOut,
        [property: Range(1, int.MaxValue)]
        int Guests,
        [property: Required, MinLength(1)]
        List<QuoteSnapshotLineItemRequest> LineItems,
        [property: Required]
        MoneyRequest Total
    ) : IValidatableObject
    {
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
