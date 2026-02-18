namespace StayRate.Domain;

public sealed record Quote(
    Money Subtotal,
    Money PercentDiscountAmount,
    Money FixedDiscountAmount,
    Money Total)
{
    public static Quote From(
        Money subtotal,
        Money percentDiscountAmount,
        Money fixedDiscountAmount)
    {
        var total = (subtotal - percentDiscountAmount - fixedDiscountAmount).MaxZero();
        return new Quote(subtotal, percentDiscountAmount, fixedDiscountAmount, total);
    }
}