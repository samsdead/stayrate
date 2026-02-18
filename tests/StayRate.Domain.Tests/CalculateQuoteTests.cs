using StayRate.Application.Pricing;
using StayRate.Domain;
using Xunit;

namespace StayRate.Domain.Tests;

public class CalculateQuoteTests
{
    [Fact]
    public void Execute_ComputesSubtotalCorrectly()
    {
        var items = new[]
        {
            new LineItem("A", new Money(10m, "GBP"), 2),
            new LineItem("B", new Money(5.50m, "GBP"), 3)
        };

        var quote = CalculateQuote.Execute(items);

        Assert.Equal(36.50m, quote.Subtotal.Amount);
        Assert.Equal(0m, quote.PercentDiscountAmount.Amount);
        Assert.Equal(0m, quote.FixedDiscountAmount.Amount);
        Assert.Equal(36.50m, quote.Total.Amount);
    }

    [Fact]
    public void Execute_AppliesPercentThenFixedDiscount()
    {
        var items = new[]
        {
            new LineItem("A", new Money(100m, "GBP"), 1)
        };

        var policy = new DiscountPolicy(
            percentDiscount: 10,
            fixedDiscount: new Money(5m, "GBP")
        );

        var quote = CalculateQuote.Execute(items, policy);

        Assert.Equal(100m, quote.Subtotal.Amount);
        Assert.Equal(10m, quote.PercentDiscountAmount.Amount);
        Assert.Equal(5m, quote.FixedDiscountAmount.Amount);
        Assert.Equal(85m, quote.Total.Amount);
    }

    [Fact]
    public void Execute_TotalNeverBelowZero()
    {
        var items = new[]
        {
            new LineItem("A", new Money(10m, "GBP"), 1)
        };

        var policy = new DiscountPolicy(
            percentDiscount: 0,
            fixedDiscount: new Money(999m, "GBP")
        );

        var quote = CalculateQuote.Execute(items, policy);

        Assert.Equal(0m, quote.Total.Amount);
    }

    [Fact]
    public void Execute_ThrowsIfItemsNull()
    {
        Assert.Throws<ArgumentNullException>(() => CalculateQuote.Execute(null!));
    }

    [Fact]
    public void Execute_ThrowsIfItemsEmpty()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CalculateQuote.Execute(Array.Empty<LineItem>()));
    }

    [Fact]
    public void DiscountPolicy_ThrowsIfPercentInvalid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiscountPolicy(percentDiscount: 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiscountPolicy(percentDiscount: -1));
    }

    [Fact]
    public void LineItem_ThrowsIfQuantityInvalid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LineItem("A", new Money(1m, "GBP"), 0));
    }

    [Fact]
    public void Execute_ThrowsIfCurrenciesMixed()
    {
        var items = new[]
        {
            new LineItem("A", new Money(10m, "GBP"), 1),
            new LineItem("B", new Money(10m, "USD"), 1)
        };

        Assert.Throws<InvalidOperationException>(() => CalculateQuote.Execute(items));
    }
}
