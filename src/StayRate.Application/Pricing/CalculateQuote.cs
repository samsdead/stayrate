using StayRate.Domain;

namespace StayRate.Application.Pricing;

public static class CalculateQuote
{
    public static Quote Execute(IEnumerable<LineItem> items, DiscountPolicy? discountPolicy = null)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));

        var list = items.ToList();
        if (list.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(items), "At least one line item is required.");

        var currency = list[0].UnitPrice.Currency;
        if (list.Any(i => i.UnitPrice.Currency != currency))
            throw new InvalidOperationException("All line items must have the same currency.");

        var subtotal = Money.Zero(currency);
        foreach (var item in list)
            subtotal += item.Total();

        discountPolicy ??= new DiscountPolicy();

        var percentAmount = Money.Zero(currency);
        if (discountPolicy.PercentDiscount > 0)
        {
            var pct = discountPolicy.PercentDiscount / 100m;
            percentAmount = new Money(subtotal.Amount * pct, currency);
        }

        var fixedAmount = discountPolicy.FixedDiscount ?? Money.Zero(currency);

        return Quote.From(subtotal, percentAmount, fixedAmount);
    }
}