using Microsoft.AspNetCore.Mvc;
using StayRate.Application.Pricing;
using StayRate.Domain;

namespace StayRate.Api.Controllers;

[ApiController]
[Route("api/v1/quote")]
public class QuoteController : ControllerBase
{
    public sealed record QuoteRequest(
        List<QuoteLineItem> Items,
        int PercentDiscount = 0,
        decimal FixedDiscount = 0m,
        string Currency = "GBP"
    );

    public sealed record QuoteLineItem(string Sku, decimal UnitPrice, int Quantity);

    public sealed record QuoteResponse(
        decimal Subtotal,
        decimal PercentDiscountAmount,
        decimal FixedDiscountAmount,
        decimal Total,
        string Currency
    );

    [HttpPost]
    [RequestSizeLimit(32_768)]
    public ActionResult<QuoteResponse> Create([FromBody] QuoteRequest request)
    {
        if (request.Items is not { Count: > 0 and <= 100 } ||
            request.Items.Any(item => item is null || item.Sku is not { Length: > 0 and <= 80 } ||
                item.UnitPrice is < 0m or > 1_000_000m || item.Quantity is < 1 or > 1_000))
            return BadRequest("Provide one to 100 quote items with valid prices and quantities.");

        var currency = string.IsNullOrWhiteSpace(request.Currency) ? "GBP" : request.Currency;

        var items = request.Items.Select(i =>
            new LineItem(
                i.Sku,
                new Money(i.UnitPrice, currency),
                i.Quantity
            )
        );

        var policy = new DiscountPolicy(
            percentDiscount: request.PercentDiscount,
            fixedDiscount: request.FixedDiscount > 0 ? new Money(request.FixedDiscount, currency) : null
        );

        var quote = CalculateQuote.Execute(items, policy);

        return Ok(new QuoteResponse(
            Subtotal: quote.Subtotal.Amount,
            PercentDiscountAmount: quote.PercentDiscountAmount.Amount,
            FixedDiscountAmount: quote.FixedDiscountAmount.Amount,
            Total: quote.Total.Amount,
            Currency: quote.Total.Currency
        ));
    }
}