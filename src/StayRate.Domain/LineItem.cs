namespace StayRate.Domain;

public sealed record LineItem
{
    public string Sku { get; }
    public Money UnitPrice { get; }
    public int Quantity { get; }

    public LineItem(string sku, Money unitPrice, int quantity)
    {
        if (string.IsNullOrWhiteSpace(sku))
            throw new ArgumentException("Sku is required.", nameof(sku));

        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be > 0.");

        if (unitPrice.Amount < 0)
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "UnitPrice must be >= 0.");

        Sku = sku;
        UnitPrice = unitPrice;
        Quantity = quantity;
    }

    public Money Total() => new(UnitPrice.Amount * Quantity, UnitPrice.Currency);
}