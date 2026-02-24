
namespace StayRate.Application.Abstractions;

public interface IRequestHasher
{
    string Hash(object value);
}