namespace StayRate.Domain;

public sealed record DiscountPolicy
{
    public int PercentDiscount { get; }
    public Money? FixedDiscount { get; }

    public DiscountPolicy(int percentDiscount = 0, Money? fixedDiscount = null)
    {
        if (percentDiscount is not (0 or >= 1 and <= 100))
            throw new ArgumentOutOfRangeException(nameof(percentDiscount), "PercentDiscount must be 0 or 1..100.");

        if (fixedDiscount is not null && fixedDiscount.Value.Amount < 0)
            throw new ArgumentOutOfRangeException(nameof(fixedDiscount), "FixedDiscount must be >= 0.");

        PercentDiscount = percentDiscount;
        FixedDiscount = fixedDiscount;
    }
}