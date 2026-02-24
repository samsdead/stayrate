
namespace StayRate.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}