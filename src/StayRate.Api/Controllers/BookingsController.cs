using StayRate.Application.Abstractions;
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
        [Required, MinLength(1), MaxLength(80)]
        public string Code { get; init; } = string.Empty;

        [Required, MinLength(1), MaxLength(200)]
        public string Description { get; init; } = string.Empty;

        [Required]
        public required MoneyRequest Amount { get; init; }
    }

    public class BookingQuoteRequest : IValidatableObject
    {
        [Required, MinLength(1), MaxLength(80)]
        public string PropertyId { get; init; } = string.Empty;

        public DateOnly CheckIn { get; init; }

        public DateOnly CheckOut { get; init; }

        [Range(1, 20)]
        public int Guests { get; init; }

        public virtual IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (CheckOut <= CheckIn || CheckOut.DayNumber - CheckIn.DayNumber > 30)
            {
                yield return new ValidationResult(
                    "CheckOut must be one to thirty nights after CheckIn.",
                    [nameof(CheckOut), nameof(CheckIn)]);
            }
        }
    }

    public sealed class ConfirmBookingRequest : BookingQuoteRequest
    {
        [Required, MinLength(1), MaxLength(20)]
        public List<QuoteSnapshotLineItemRequest> LineItems { get; init; } = [];

        [Required]
        public required MoneyRequest Total { get; init; }

        public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            foreach (var error in base.Validate(validationContext))
                yield return error;
            if (LineItems?.Any(item => item is null) == true)
                yield return new ValidationResult("Each line item is required.", [nameof(LineItems)]);
        }
    }

    private readonly ConfirmBookingHandler _handler;
    private readonly BookingCaller _caller;
    private readonly IBookingQuoteCatalog _catalog;

    public BookingsController(ConfirmBookingHandler handler, BookingCaller caller, IBookingQuoteCatalog catalog)
    {
        _handler = handler;
        _caller = caller;
        _catalog = catalog;
    }

    [HttpPost("quote")]
    [RequestSizeLimit(4096)]
    public ActionResult<QuoteSnapshot> Quote([FromBody] BookingQuoteRequest request)
    {
        if (_caller.Resolve(ControllerContext.HttpContext, "POST /api/v1/bookings/quote") is null)
            return Unauthorized();

        var quote = _catalog.FindQuote(request.PropertyId, request.CheckIn, request.CheckOut, request.Guests);
        if (quote is null)
            return UnprocessableEntity();

        return Ok(quote);
    }

    [HttpPost("confirm")]
    [RequestSizeLimit(32_768)]
    public async Task<ActionResult<ConfirmBookingResult>> Confirm(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] ConfirmBookingRequest request,
        CancellationToken ct)
    {
        var caller = _caller.Resolve(ControllerContext.HttpContext, "POST /api/v1/bookings/confirm");
        if (caller is null)
            return Unauthorized();

        if (idempotencyKey is not { Length: > 0 and <= 128 } ||
            idempotencyKey.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
            return BadRequest("Provide an Idempotency-Key of 1 to 128 letters, digits, hyphens, underscores, or periods.");

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
            new ConfirmBookingCommand(idempotencyKey, snapshot, caller),
            ct);

        return Ok(result);
    }
}
