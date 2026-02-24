using System;
using System.Collections.Generic;
using System.Text;

namespace StayRate.Domain.Bookings;
using StayRate.Domain;


public sealed record QuoteSnapshot(
    string PropertyId,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Guests,
    IReadOnlyList<QuoteSnapshotLineItem> LineItems,
    Money Total
);

public sealed record QuoteSnapshotLineItem(
    string Code,
    string Description,
    Money Amount
);