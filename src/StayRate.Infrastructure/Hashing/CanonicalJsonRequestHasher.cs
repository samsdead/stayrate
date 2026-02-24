using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StayRate.Application.Abstractions;

namespace StayRate.Infrastructure.Hashing;

public sealed class CanonicalJsonRequestHasher : IRequestHasher
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    public string Hash(object value)
    {
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
