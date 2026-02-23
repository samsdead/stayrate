using Microsoft.AspNetCore.Mvc;
using StayRate.Application.Bookings.ConfirmBooking;
using StayRate.Domain;
using StayRate.Domain.Bookings;

namespace StayRate.Api.Controllers;

[ApiController]
[Route("api/v1/bookings")]
public sealed class BookingsController : ControllerBase
{
    public sealed record MoneyRequest(decimal Amount, string Currency);

    public sealed record QuoteSnapshotLineItemRequest(
        string Code,
        string Description,
        MoneyRequest Amount
    );

    public sealed record ConfirmBookingRequest(
        string PropertyId,
        DateOnly CheckIn,
        DateOnly CheckOut,
        int Guests,
        List<QuoteSnapshotLineItemRequest> LineItems,
        MoneyRequest Total
    );

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

        try
        {
            var result = await _handler.HandleAsync(
                new ConfirmBookingCommand(idempotencyKey, snapshot),
                ct);

            return Ok(result);
        }
        catch (IdempotencyConflictException ex)
        {
            return Conflict(new { message = ex.Message, ex.IdempotencyKey });
        }
    }
}
